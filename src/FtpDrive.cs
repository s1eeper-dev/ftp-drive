// FTP/SFTP 서버를 드라이브 문자로 연결·해제하는 작은 창 프로그램 (rclone mount + WinFsp 사용).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FTP 드라이브")]
[assembly: AssemblyProduct("FTP 드라이브")]
[assembly: AssemblyVersion("1.0.0.0")]

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

    class Settings
    {
        public string Protocol = "ftp";
        public string Host = "";
        public int Port = 21;
        public string User = "";
        public string RemotePath = "";
        public string Drive = "Z";
        public string Label = "FTP";
        public bool OpenAfterConnect = true;

        static string FilePath { get { return Path.Combine(MainForm.DataDir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;
            int port;
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                switch (k)
                {
                    case "Protocol": s.Protocol = v; break;
                    case "Host": s.Host = v; break;
                    case "Port": if (int.TryParse(v, out port)) s.Port = port; break;
                    case "User": s.User = v; break;
                    case "RemotePath": s.RemotePath = v; break;
                    case "Drive": if (v.Length > 0) s.Drive = v.Substring(0, 1); break;
                    case "Label": s.Label = v; break;
                    case "OpenAfterConnect": s.OpenAfterConnect = v != "0"; break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(MainForm.DataDir);
                File.WriteAllLines(FilePath, new[] {
                    "Protocol=" + Protocol, "Host=" + Host, "Port=" + Port, "User=" + User,
                    "RemotePath=" + RemotePath, "Drive=" + Drive, "Label=" + Label,
                    "OpenAfterConnect=" + (OpenAfterConnect ? "1" : "0")
                }, Encoding.UTF8);
            }
            catch { } // 저장에 실패해도 연결 자체는 계속 가능하다.
        }
    }

    class MainForm : Form
    {
        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        // 설치 폴더(Program Files)는 쓰기 금지라서 설정과 로그는 사용자별 폴더에 둔다.
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FtpDrive");

        static readonly string[] ProtoKeys = { "ftp", "ftps-explicit", "ftps-implicit", "sftp" };
        static readonly string[] ProtoNames = { "FTP", "FTPS (명시적 TLS)", "FTPS (암시적 TLS)", "SFTP" };
        static readonly int[] ProtoPorts = { 21, 21, 990, 22 };
        const string RcUser = "ftpdrive";

        enum State { Disconnected, Connecting, Connected }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

        readonly ComboBox cboProtocol = new ComboBox();
        readonly TextBox txtHost = new TextBox();
        readonly NumericUpDown numPort = new NumericUpDown();
        readonly TextBox txtUser = new TextBox();
        readonly TextBox txtPass = new TextBox();
        readonly TextBox txtPath = new TextBox();
        readonly ComboBox cboDrive = new ComboBox();
        readonly TextBox txtLabel = new TextBox();
        readonly CheckBox chkOpen = new CheckBox();
        readonly Button btnConnect = new Button();
        readonly Button btnDisconnect = new Button();
        readonly Button btnOpen = new Button();
        readonly Label lblStatus = new Label();
        readonly System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();
        readonly Queue<string> logLines = new Queue<string>();

        State state = State.Disconnected;
        Process rclone;
        string mountedDrive, mountedHost, rcAddr, rcPass;
        DateTime connectStart;

        public MainForm()
        {
            Text = "FTP 드라이브";
            Font = new Font("Malgun Gothic", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            IntPtr large, small;
            if (ExtractIconEx("shell32.dll", 9, out large, out small, 1) > 0 && large != IntPtr.Zero)
                Icon = Icon.FromHandle(large); // 9번은 '네트워크 드라이브' 아이콘

            int pad = Em(0.8), field = Em(16);
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(pad, pad), Margin = new Padding(0, 0, pad, pad) };

            cboProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProtocol.Items.AddRange(ProtoNames);
            cboProtocol.Width = field;
            txtHost.Width = field;
            numPort.Minimum = 1; numPort.Maximum = 65535; numPort.Value = 21; numPort.Width = Em(6);
            txtUser.Width = field;
            txtPass.Width = field; txtPass.UseSystemPasswordChar = true;
            txtPath.Width = field;
            cboDrive.DropDownStyle = ComboBoxStyle.DropDownList; cboDrive.Width = Em(6);
            txtLabel.Width = field;
            chkOpen.Text = "연결되면 탐색기로 열기"; chkOpen.AutoSize = true;
            SetCue(txtHost, "예: 192.168.0.10 또는 ftp.example.com");
            SetCue(txtPath, "비우면 로그인 기본 폴더");
            SetCue(txtLabel, "탐색기에 보일 이름");

            AddRow(grid, "방식", cboProtocol);
            AddRow(grid, "서버 주소", txtHost);
            AddRow(grid, "포트", numPort);
            AddRow(grid, "아이디", txtUser);
            AddRow(grid, "비밀번호", txtPass);
            AddRow(grid, "원격 폴더", txtPath);
            AddRow(grid, "드라이브", cboDrive);
            AddRow(grid, "표시 이름", txtLabel);
            AddRow(grid, "", chkOpen);

            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.None, Margin = new Padding(0, Em(0.6), 0, 0) };
            foreach (var b in new[] { btnConnect, btnDisconnect, btnOpen })
            {
                b.AutoSize = true;
                b.MinimumSize = new Size(Em(5.5), 0);
                buttons.Controls.Add(b);
            }
            btnConnect.Text = "연결";
            btnDisconnect.Text = "연결 해제";
            btnOpen.Text = "드라이브 열기";
            grid.Controls.Add(buttons, 0, grid.RowCount);
            grid.SetColumnSpan(buttons, 2);
            grid.RowCount++;

            lblStatus.AutoSize = true;
            lblStatus.MaximumSize = new Size(Em(22), 0);
            lblStatus.Anchor = AnchorStyles.None;
            lblStatus.Margin = new Padding(0, Em(0.4), 0, 0);
            grid.Controls.Add(lblStatus, 0, grid.RowCount);
            grid.SetColumnSpan(lblStatus, 2);
            grid.RowCount++;

            Controls.Add(grid);
            AcceptButton = btnConnect;

            cboProtocol.SelectedIndexChanged += delegate { OnProtocolChanged(); };
            cboDrive.DropDown += delegate { FillDrives(SelectedLetter()); };
            btnConnect.Click += delegate { Connect(); };
            btnDisconnect.Click += delegate { Disconnect(true); };
            btnOpen.Click += delegate { OpenDrive(); };
            waitTimer.Interval = 500;
            waitTimer.Tick += delegate { OnWaitTick(); };

            var s = Settings.Load();
            int pi = Array.IndexOf(ProtoKeys, s.Protocol);
            cboProtocol.SelectedIndex = pi < 0 ? 0 : pi;
            numPort.Value = Math.Max(1, Math.Min(65535, s.Port));
            txtHost.Text = s.Host;
            txtUser.Text = s.User;
            txtPath.Text = s.RemotePath;
            txtLabel.Text = s.Label;
            chkOpen.Checked = s.OpenAfterConnect;
            FillDrives(s.Drive);
            SetState(State.Disconnected);
        }

        int Em(double n) { return (int)Math.Round(n * Font.Height); }

        void AddRow(TableLayoutPanel g, string caption, Control c)
        {
            g.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, Em(0.8), 3) }, 0, g.RowCount);
            c.Anchor = AnchorStyles.Left;
            g.Controls.Add(c, 1, g.RowCount);
            g.RowCount++;
        }

        static void SetCue(TextBox tb, string text)
        {
            tb.HandleCreated += delegate { SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); }; // EM_SETCUEBANNER
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (txtHost.Text.Length > 0 && txtUser.Text.Length > 0) txtPass.Focus();
            else txtHost.Focus();
        }

        void OnProtocolChanged()
        {
            // 포트가 기본값 중 하나였을 때만 새 방식의 기본 포트로 바꾼다. 직접 입력한 포트는 건드리지 않는다.
            if (Array.IndexOf(ProtoPorts, (int)numPort.Value) >= 0)
                numPort.Value = ProtoPorts[cboProtocol.SelectedIndex];
        }

        void FillDrives(string preferred)
        {
            var used = new HashSet<char>();
            foreach (var d in Environment.GetLogicalDrives()) used.Add(char.ToUpperInvariant(d[0]));
            cboDrive.Items.Clear();
            for (char c = 'Z'; c >= 'D'; c--)
                if (!used.Contains(c)) cboDrive.Items.Add(c + ":");
            string want = preferred.ToUpperInvariant() + ":";
            if (!cboDrive.Items.Contains(want)) cboDrive.Items.Insert(0, want);
            cboDrive.SelectedItem = want;
        }

        string SelectedLetter()
        {
            var item = cboDrive.SelectedItem as string;
            return item == null ? "Z" : item.Substring(0, 1);
        }

        void SetState(State s)
        {
            state = s;
            bool idle = s == State.Disconnected;
            foreach (Control c in new Control[] { cboProtocol, txtHost, numPort, txtUser, txtPass, txtPath, cboDrive, txtLabel })
                c.Enabled = idle;
            btnConnect.Enabled = idle;
            btnDisconnect.Enabled = !idle;
            btnOpen.Enabled = s == State.Connected;
            switch (s)
            {
                case State.Disconnected:
                    lblStatus.Text = "연결 안 됨";
                    lblStatus.ForeColor = SystemColors.GrayText;
                    Text = "FTP 드라이브";
                    break;
                case State.Connecting:
                    lblStatus.Text = "연결 중...";
                    lblStatus.ForeColor = Color.DarkOrange;
                    Text = "FTP 드라이브 - 연결 중";
                    break;
                case State.Connected:
                    lblStatus.Text = "연결됨  " + mountedDrive + ":  ←  " + mountedHost;
                    lblStatus.ForeColor = Color.ForestGreen;
                    Text = "FTP 드라이브 - " + mountedDrive + ": 연결됨";
                    break;
            }
        }

        void Connect()
        {
            string host = txtHost.Text.Trim(), user = txtUser.Text.Trim(), letter = SelectedLetter();
            if (host.Length == 0) { Warn("서버 주소를 입력하세요."); txtHost.Focus(); return; }
            if (user.Length == 0) { Warn("아이디를 입력하세요."); txtUser.Focus(); return; }
            string exe = FindRclone();
            if (exe == null) { Warn("rclone.exe 를 찾을 수 없습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (!WinFspInstalled()) { Warn("WinFsp 가 설치되어 있지 않습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (Array.Exists(Environment.GetLogicalDrives(), d => d.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
            { Warn(letter + ": 드라이브는 이미 사용 중입니다. 다른 드라이브를 고르세요."); return; }

            string proto = ProtoKeys[cboProtocol.SelectedIndex];
            string remotePath = txtPath.Text.Trim().Replace('\\', '/');
            string label = Regex.Replace(txtLabel.Text, @"[\\/:*?""<>|]", "").Trim();
            if (label.Length == 0) label = "FTP";
            new Settings
            {
                Protocol = proto, Host = host, Port = (int)numPort.Value, User = user, RemotePath = remotePath,
                Drive = letter, Label = label, OpenAfterConnect = chkOpen.Checked
            }.Save();

            string obscured = "";
            if (txtPass.Text.Length > 0)
            {
                obscured = Obscure(exe, txtPass.Text);
                if (obscured == null) { Warn("비밀번호를 rclone 형식으로 바꾸지 못했습니다."); return; }
            }

            rcAddr = "127.0.0.1:" + FreePort();
            rcPass = Guid.NewGuid().ToString("N");
            string volname = @"\\" + Regex.Replace(host, @"[^A-Za-z0-9.\-_]", "-") + @"\" + label;
            string args = string.Join(" ", new[] {
                "mount", Quote((proto == "sftp" ? ":sftp:" : ":ftp:") + remotePath), letter + ":",
                "--vfs-cache-mode", "writes",
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
                WorkingDirectory = AppDir
            };
            // 접속 정보와 비밀번호는 명령줄에 남지 않도록 환경변수로만 넘긴다.
            var env = psi.EnvironmentVariables;
            string prefix = proto == "sftp" ? "RCLONE_SFTP_" : "RCLONE_FTP_";
            env[prefix + "HOST"] = host;
            env[prefix + "PORT"] = ((int)numPort.Value).ToString();
            env[prefix + "USER"] = user;
            if (obscured.Length > 0) env[prefix + "PASS"] = obscured;
            if (proto == "ftps-explicit") env["RCLONE_FTP_EXPLICIT_TLS"] = "true";
            if (proto == "ftps-implicit") env["RCLONE_FTP_TLS"] = "true";
            if (proto == "sftp") env["RCLONE_SFTP_DISABLE_HASHCHECK"] = "true";
            env["RCLONE_RC_USER"] = RcUser;
            env["RCLONE_RC_PASS"] = rcPass;

            lock (logLines) logLines.Clear();
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += OnLog;
            p.ErrorDataReceived += OnLog;
            p.Exited += delegate
            {
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => OnRcloneExited(p))); }
                catch (InvalidOperationException) { } // 창이 이미 닫히는 중
            };
            try { p.Start(); }
            catch (Exception ex) { Warn("rclone 을 실행하지 못했습니다.\n\n" + ex.Message); return; }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            rclone = p;
            mountedDrive = letter;
            mountedHost = host;
            connectStart = DateTime.Now;
            SetState(State.Connecting);
            waitTimer.Start();
        }

        void OnWaitTick()
        {
            if (state != State.Connecting || rclone == null) { waitTimer.Stop(); return; }
            if (Directory.Exists(mountedDrive + @":\"))
            {
                waitTimer.Stop();
                txtPass.Clear();
                SetState(State.Connected);
                if (chkOpen.Checked) OpenDrive();
                return;
            }
            if ((DateTime.Now - connectStart).TotalSeconds > 30)
            {
                waitTimer.Stop();
                var p = rclone;
                rclone = null;
                Kill(p);
                SaveLog();
                SetState(State.Disconnected);
                ShowFailure("30초 안에 연결되지 않았습니다.");
            }
        }

        void OnRcloneExited(Process p)
        {
            if (p != rclone) return; // 직접 해제한 경우는 Disconnect 에서 이미 정리했다.
            rclone = null;
            waitTimer.Stop();
            try { p.WaitForExit(); } catch { } // 남은 출력까지 받아 둔다.
            SaveLog();
            var was = state;
            SetState(State.Disconnected);
            ShowFailure(was == State.Connected ? "연결이 끊어졌습니다." : "연결하지 못했습니다.");
            txtPass.Focus();
        }

        bool Disconnect(bool checkUploads)
        {
            var p = rclone;
            if (p == null) return true;
            if (checkUploads && state == State.Connected)
            {
                int n = PendingUploads();
                if (n > 0 && MessageBox.Show(this,
                        "아직 서버로 올리고 있는 파일이 " + n + "개 있습니다.\n지금 해제하면 이 파일들이 서버에 올라가지 않을 수 있습니다.\n\n그래도 해제할까요? ([아니요]를 누르고 잠시 뒤 다시 해제하세요.)",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return false;
            }
            rclone = null;
            waitTimer.Stop();
            lblStatus.Text = "연결 해제 중...";
            lblStatus.Refresh();
            Cursor.Current = Cursors.WaitCursor;
            // rc 로 정상 종료를 요청하고, 응답이 없으면 강제로 끝낸다.
            if (RcCall("core/quit") == null || !p.WaitForExit(15000)) Kill(p);
            Cursor.Current = Cursors.Default;
            SaveLog();
            SetState(State.Disconnected);
            txtPass.Focus();
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (rclone != null)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    if (MessageBox.Show(this, "연결을 해제하고 종료할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes
                        || !Disconnect(true))
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                else Disconnect(false);
            }
            base.OnFormClosing(e);
        }

        void OpenDrive()
        {
            try { Process.Start("explorer.exe", mountedDrive + @":\"); } catch { }
        }

        int PendingUploads()
        {
            string json = RcCall("vfs/stats");
            if (json == null) return 0;
            int n = 0;
            foreach (Match m in Regex.Matches(json, "\"(uploadsInProgress|uploadsQueued)\"\\s*:\\s*(\\d+)"))
                n += int.Parse(m.Groups[2].Value);
            return n;
        }

        string RcCall(string path)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + rcAddr + "/" + path);
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = 5000;
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
            lock (logLines)
            {
                logLines.Enqueue(e.Data);
                while (logLines.Count > 300) logLines.Dequeue();
            }
        }

        string LogText()
        {
            lock (logLines) return string.Join("\r\n", logLines.ToArray());
        }

        void SaveLog()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(Path.Combine(DataDir, "rclone.log"), LogText(), Encoding.UTF8);
            }
            catch { }
        }

        void ShowFailure(string head)
        {
            string log = LogText();
            var sb = new StringBuilder(head);
            string hint = Hint(log);
            if (hint != null) sb.Append("\n\n").Append(hint);
            var lines = log.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                sb.Append("\n\n[rclone 메시지]");
                for (int i = Math.Max(0, lines.Length - 6); i < lines.Length; i++)
                    sb.Append("\n").Append(lines[i].Length > 300 ? lines[i].Substring(0, 300) + "..." : lines[i]);
            }
            sb.Append("\n\n전체 기록: ").Append(Path.Combine(DataDir, "rclone.log"));
            MessageBox.Show(this, sb.ToString(), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static string Hint(string log)
        {
            string l = log.ToLowerInvariant();
            if (Regex.IsMatch(l, @"\b530\b") || l.Contains("login incorrect") || l.Contains("unable to authenticate"))
                return "아이디 또는 비밀번호가 맞지 않습니다.";
            if (l.Contains("no such host"))
                return "서버 주소를 찾을 수 없습니다. 주소를 확인하세요.";
            if (l.Contains("refused") || l.Contains("거부"))
                return "서버가 연결을 거부했습니다. 포트와 방식(FTP/FTPS/SFTP)을 확인하세요.";
            if (l.Contains("timeout") || l.Contains("timed out") || l.Contains("did not properly respond"))
                return "서버가 응답하지 않습니다. 주소, 포트, 방화벽을 확인하세요.";
            if (l.Contains("x509") || l.Contains("certificate") || l.Contains("tls"))
                return "보안 연결(TLS)에 실패했습니다. 방식(FTPS 종류)을 확인하세요.";
            if (l.Contains("winfsp") || l.Contains("cgofuse"))
                return "WinFsp 에 문제가 있습니다. 설치 프로그램을 다시 실행하세요.";
            return null;
        }

        void Warn(string msg)
        {
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch { }
        }

        static string Obscure(string exe, string pass)
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
                using (var p = Process.Start(psi))
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

        static string FindRclone()
        {
            var candidates = new List<string> { Path.Combine(AppDir, "rclone.exe") };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "rclone.exe")); }
                catch (ArgumentException) { }
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        static bool WinFspInstalled()
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
