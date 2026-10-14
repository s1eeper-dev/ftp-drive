// 서버 하나를 드라이브 문자로 붙여 두는 rclone mount 프로세스와 그 상태(업로드, 로그)를 다룬다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FtpDrive
{
    class Connection
    {
        const string RcUser = "ftpdrive";

        public readonly Profile Profile;
        public readonly string Drive;       // 연결할 때 정한 드라이브 문자 (연결 중에는 바뀌지 않는다)
        public readonly DateTime Started = DateTime.Now;
        public bool Mounted;                // 드라이브가 나타나고 파일 목록까지 읽었으면 true, 그 전은 연결 중
        public bool Verifying;              // 드라이브가 나타나 파일 목록을 읽어 보는 중
        public bool DisconnectWhenIdle;     // 업로드가 끝나면 자동으로 해제
        public UploadInfo Upload;           // 마지막으로 확인한 업로드 상황
        public Process Process { get; private set; }

        readonly string rcAddr = "127.0.0.1:" + FreePort();
        readonly string rcPass = Guid.NewGuid().ToString("N");
        readonly Queue<string> log = new Queue<string>();

        public Connection(Profile profile)
        {
            Profile = profile;
            Drive = profile.Drive;
        }

        public string LogPath { get { return Path.Combine(MainForm.DataDir, "rclone-" + Profile.Alias + ".log"); } }

        // rclone mount 를 띄운다. 실패하면 사용자에게 보일 문구를, 성공하면 null 을 돌려준다.
        // exited 는 rclone 이 끝나면(정상 해제 포함) 다른 스레드에서 불린다.
        public string Start(string exe, string obscuredPass, Action<Connection> exited)
        {
            var p = Profile;
            bool sftp = p.Protocol == "sftp", webdav = p.Protocol.StartsWith("webdav");
            string backend = sftp ? "sftp" : webdav ? "webdav" : "ftp";
            string volname = @"\\" + Regex.Replace(p.Host, @"[^A-Za-z0-9.\-_]", "-") + @"\" + p.Alias;
            string args = string.Join(" ", new[] {
                "mount", Quote(":" + backend + ":" + p.RemotePath), Drive + ":",
                "--vfs-cache-mode", "writes",
                "--vfs-write-back", "1s",   // 파일을 닫고 1초 뒤 업로드 시작 (기본 5초)
                "--dir-cache-time", "30s",
                "--network-mode",
                "--volname", Quote(volname),
                "--rc", "--rc-addr", rcAddr,
                "--log-level", "NOTICE"
            });

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = MainForm.AppDir
            };
            // 접속 정보와 비밀번호는 명령줄에 남지 않도록 환경변수로만 넘긴다.
            var env = psi.EnvironmentVariables;
            string prefix = "RCLONE_" + backend.ToUpperInvariant() + "_";
            if (webdav)
            {
                string host = p.Host.Contains(":") && !p.Host.StartsWith("[") ? "[" + p.Host + "]" : p.Host; // IPv6 주소
                env["RCLONE_WEBDAV_URL"] = (p.Protocol == "webdav-https" ? "https://" : "http://") + host + ":" + p.Port + "/";
                env["RCLONE_WEBDAV_VENDOR"] = "other";
            }
            else
            {
                env[prefix + "HOST"] = p.Host;
                env[prefix + "PORT"] = p.Port.ToString();
            }
            env[prefix + "USER"] = p.User;
            if (obscuredPass.Length > 0) env[prefix + "PASS"] = obscuredPass;
            if (p.Protocol == "ftps-explicit") env["RCLONE_FTP_EXPLICIT_TLS"] = "true";
            if (p.Protocol == "ftps-implicit") env["RCLONE_FTP_TLS"] = "true";
            if (sftp) env["RCLONE_SFTP_DISABLE_HASHCHECK"] = "true";
            if (p.SkipCertCheck && Profile.UsesTls(p.Protocol))
            {
                env["RCLONE_NO_CHECK_CERTIFICATE"] = "true";      // WebDAV(HTTPS)
                env["RCLONE_FTP_NO_CHECK_CERTIFICATE"] = "true";  // FTPS
            }
            env["RCLONE_RC_USER"] = RcUser;
            env["RCLONE_RC_PASS"] = rcPass;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnLog;
            proc.ErrorDataReceived += OnLog;
            proc.Exited += delegate { exited(this); };
            try { proc.Start(); }
            catch (Exception ex) { return "rclone 을 실행하지 못했습니다.\n\n" + ex.Message; }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            Process = proc;
            return null;
        }

        // rc 로 정상 종료를 요청하고, 응답이 없으면 강제로 끝낸다.
        public void Stop()
        {
            if (RcCall("core/quit", 5000) == null || !Process.WaitForExit(15000)) Kill();
            try { if (Process.HasExited) Process.WaitForExit(); } catch { } // 남은 출력(오류 원인)까지 받아 둔다.
            SaveLog();
        }

        public void Kill()
        {
            try { if (!Process.HasExited) Process.Kill(); Process.WaitForExit(5000); } catch { }
        }

        public UploadInfo GetUploadInfo(int timeoutMs)
        {
            string vfs = RcCall("vfs/stats", timeoutMs);
            if (vfs == null) return null;
            var u = UploadInfo.Parse(vfs, null);
            if (u != null && u.Pending > 0) u = UploadInfo.Parse(vfs, RcCall("core/stats", timeoutMs));
            return u;
        }

        string RcCall(string path, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + rcAddr + "/" + path);
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.ContentType = "application/json";
                req.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(RcUser + ":" + rcPass));
                byte[] body = Encoding.ASCII.GetBytes("{}");
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        void OnLog(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (log)
            {
                log.Enqueue(e.Data);
                while (log.Count > 300) log.Dequeue();
            }
        }

        public string LogText()
        {
            lock (log) return string.Join("\r\n", log.ToArray());
        }

        public void SaveLog()
        {
            try
            {
                Directory.CreateDirectory(MainForm.DataDir);
                File.WriteAllText(LogPath, LogText(), Encoding.UTF8);
            }
            catch { }
        }

        // rclone 로그에서 흔한 실패 원인을 찾아 알기 쉬운 문구로 바꾼다.
        public static string Hint(string log)
        {
            string l = log.ToLowerInvariant();
            if (Regex.IsMatch(l, @"\b(530|401)\b") || l.Contains("login incorrect") || l.Contains("unable to authenticate") || l.Contains("unauthorized"))
                return "아이디 또는 비밀번호가 맞지 않습니다.";
            if (l.Contains("no such host"))
                return "서버 주소를 찾을 수 없습니다. 주소를 확인하세요.";
            if (l.Contains("x509") || l.Contains("certificate"))
                return "서버 인증서를 확인할 수 없습니다. 시놀로지 기본(자체 서명) 인증서라면 '인증서 확인 안 함'을 켜세요.";
            if (l.Contains("refused") || l.Contains("거부"))
                return "서버가 연결을 거부했습니다. 포트와 방식(FTP/SFTP/WebDAV)을 확인하세요.";
            if (l.Contains("timeout") || l.Contains("timed out") || l.Contains("did not properly respond"))
                return "서버가 응답하지 않습니다. 주소, 포트, 방화벽을 확인하세요.";
            if (l.Contains("tls") || l.Contains("http: server gave http response to https client"))
                return "보안 연결(TLS)에 실패했습니다. 방식(FTPS 종류, WebDAV HTTPS/HTTP)을 확인하세요.";
            if (l.Contains("winfsp") || l.Contains("cgofuse"))
                return "WinFsp 에 문제가 있습니다. 설치 프로그램을 다시 실행하세요.";
            return null;
        }

        public static string Obscure(string exe, string pass)
        {
            // 비밀번호는 명령줄 인자 대신 표준입력으로 넘긴다. 한글 비밀번호도 깨지지 않게 UTF-8 바이트로 직접 쓴다.
            var psi = new ProcessStartInfo(exe, "obscure -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    byte[] data = Encoding.UTF8.GetBytes(pass + "\n");
                    p.StandardInput.BaseStream.Write(data, 0, data.Length);
                    p.StandardInput.Close();
                    p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    if (!p.WaitForExit(10000) || p.ExitCode != 0 || output.Length == 0) return null;
                    return output;
                }
            }
            catch { return null; }
        }

        public static string FindRclone()
        {
            var candidates = new List<string> { Path.Combine(MainForm.AppDir, "rclone.exe") };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "rclone.exe")); }
                catch (ArgumentException) { }
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        public static bool WinFspInstalled()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\WinFsp"))
                {
                    var dir = key == null ? null : key.GetValue("InstallDir") as string;
                    if (dir != null && File.Exists(Path.Combine(dir, @"bin\winfsp-x64.dll"))) return true;
                }
            }
            catch { }
            return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"WinFsp\bin\winfsp-x64.dll"));
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // Windows 명령줄 규칙(MSVC/Go 공통)에 맞게 인자를 따옴표로 감싼다.
        static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(c);
                bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }
    }
}
