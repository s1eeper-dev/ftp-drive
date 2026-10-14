// FTP 드라이브 진입점과 연결 설정(Profile·Settings)·업로드 상황(UploadInfo) 자료 구조. 화면은 MainForm.cs, rclone 은 Connection.cs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("FTP 드라이브")]
[assembly: AssemblyProduct("FTP 드라이브")]
[assembly: AssemblyVersion("1.3.0.0")] // 버전은 여기 한 곳에서만 바꾼다. build.ps1 이 설치 프로그램에도 넘긴다.

namespace FtpDrive
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            // 두 번 뜨면 설정과 연결이 꼬이므로 하나만 실행한다. 설치 프로그램도 이 이름으로 실행 중인지 확인한다.
            bool created;
            using (new Mutex(true, "FtpDrive_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("FTP 드라이브가 이미 실행 중입니다.", "FTP 드라이브", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    class Profile
    {
        // 방식 목록. 포트는 처음 고를 때의 기본값이다 (WebDAV 는 시놀로지 기본 포트).
        public static readonly string[] Keys = { "ftp", "ftps-explicit", "ftps-implicit", "sftp", "webdav-https", "webdav-http" };
        public static readonly string[] Names = { "FTP", "FTPS (명시적 TLS)", "FTPS (암시적 TLS)", "SFTP", "WebDAV (HTTPS)", "WebDAV (HTTP)" };
        public static readonly int[] Ports = { 21, 21, 990, 22, 5006, 5005 };

        public static bool UsesTls(string protocol)
        {
            return protocol == "ftps-explicit" || protocol == "ftps-implicit" || protocol == "webdav-https";
        }

        public string Alias = "";
        public string Protocol = "ftp";
        public string Host = "";
        public int Port = 21;
        public string User = "";
        public string RemotePath = "";
        public string Drive = "Z";
        public bool SkipCertCheck;   // 자체 서명 인증서를 받아들인다 (FTPS, WebDAV HTTPS)

        // 별칭은 설정 파일 구역 이름과 탐색기 드라이브 이름으로 쓰이므로 문제 되는 문자를 뺀다.
        public static string CleanAlias(string alias)
        {
            alias = Regex.Replace(alias ?? "", @"[\[\]\\/:*?""<>|]", "").Trim();
            return alias.Length > 40 ? alias.Substring(0, 40).Trim() : alias;
        }
    }

    // rclone 원격 제어 응답(vfs/stats, core/stats)에서 업로드 진행 상황만 뽑는다.
    class UploadInfo
    {
        public int Pending;       // 올리는 중이거나 올릴 차례를 기다리는 파일 수
        public string Name;       // 지금 올리고 있는 파일 이름, 없으면 null
        public int Percent = -1;

        public static UploadInfo Parse(string vfsJson, string statsJson)
        {
            try
            {
                var js = new JavaScriptSerializer();
                var vfs = js.DeserializeObject(vfsJson) as Dictionary<string, object>;
                if (vfs == null) return null;
                var info = new UploadInfo();
                var disk = Get(vfs, "diskCache") as Dictionary<string, object>;
                if (disk != null) info.Pending = ToInt(Get(disk, "uploadsInProgress")) + ToInt(Get(disk, "uploadsQueued"));
                var stats = statsJson == null ? null : js.DeserializeObject(statsJson) as Dictionary<string, object>;
                var list = stats == null ? null : Get(stats, "transferring") as IList;
                var first = list != null && list.Count > 0 ? list[0] as Dictionary<string, object> : null;
                if (first != null)
                {
                    var name = Get(first, "name") as string;
                    if (name != null) info.Name = name.Substring(name.LastIndexOf('/') + 1);
                    info.Percent = ToInt(Get(first, "percentage"));
                }
                return info;
            }
            catch (ArgumentException) { return null; } // JSON 형식이 아닐 때
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static int ToInt(object v)
        {
            try { return v == null ? 0 : Convert.ToInt32(v); }
            catch (Exception) { return 0; }
        }
    }

    class Settings
    {
        public readonly List<Profile> Profiles = new List<Profile>(); // 목록에 보이는 순서 (새 연결은 끝에 붙는다)
        public bool OpenAfterConnect = true;

        public static string DefaultPath { get { return Path.Combine(MainForm.DataDir, "settings.ini"); } }

        public Profile Find(string alias)
        {
            return Profiles.Find(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase));
        }

        public static Settings Load() { return Load(DefaultPath); }

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!File.Exists(path)) return s;
            Profile cur = null, legacy = new Profile();
            string legacyLabel = "";
            int port;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string section = line.Substring(1, line.Length - 2);
                    cur = null;
                    if (section.StartsWith("Profile:") && s.Find(section.Substring(8)) == null)
                    {
                        cur = new Profile { Alias = section.Substring(8) };
                        s.Profiles.Add(cur);
                    }
                    continue;
                }
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                if (k == "OpenAfterConnect") { s.OpenAfterConnect = v != "0"; continue; }
                var p = cur ?? legacy; // 1.0.0 형식(구역 없이 서버 하나)은 연결 목록의 첫 항목으로 옮긴다.
                switch (k)
                {
                    case "Protocol": p.Protocol = v; break;
                    case "Host": p.Host = v; break;
                    case "Port": if (int.TryParse(v, out port)) p.Port = port; break;
                    case "User": p.User = v; break;
                    case "RemotePath": p.RemotePath = v; break;
                    case "Drive": if (v.Length > 0) p.Drive = v.Substring(0, 1); break;
                    case "SkipCertCheck": p.SkipCertCheck = v == "1"; break;
                    case "Label": legacyLabel = v; break;
                }
            }
            if (s.Profiles.Count == 0 && legacy.Host.Length > 0)
            {
                legacy.Alias = Profile.CleanAlias(legacyLabel == "FTP" ? "" : legacyLabel);
                if (legacy.Alias.Length == 0) legacy.Alias = Profile.CleanAlias(legacy.Host);
                s.Profiles.Add(legacy);
            }
            return s;
        }

        public void Save() { Save(DefaultPath); }

        public void Save(string path)
        {
            var lines = new List<string> { "[General]", "OpenAfterConnect=" + (OpenAfterConnect ? "1" : "0") };
            foreach (var p in Profiles)
            {
                lines.Add("");
                lines.Add("[Profile:" + p.Alias + "]");
                lines.Add("Protocol=" + p.Protocol);
                lines.Add("Host=" + p.Host);
                lines.Add("Port=" + p.Port);
                lines.Add("User=" + p.User);
                lines.Add("RemotePath=" + p.RemotePath);
                lines.Add("Drive=" + p.Drive);
                lines.Add("SkipCertCheck=" + (p.SkipCertCheck ? "1" : "0"));
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch { } // 저장에 실패해도 연결 자체는 계속 가능하다.
        }
    }
}
