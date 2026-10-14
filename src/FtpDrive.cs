// FTP/SFTP 서버를 드라이브 문자로 연결·해제하는 작은 창 프로그램 (rclone mount + WinFsp 사용).
using System;
using System.Collections;
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
using System.Web.Script.Serialization;
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

    class Profile
    {
        public string Alias = "";
        public string Protocol = "ftp";
        public string Host = "";
        public int Port = 21;
        public string User = "";
        public string RemotePath = "";
        public string Drive = "Z";

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
        public readonly List<Profile> Profiles = new List<Profile>(); // 최근에 연결한 것이 맨 앞
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
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines, Encoding.UTF8);
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

        readonly ComboBox cboProfiles = new ComboBox();
        readonly Button btnNew = new Button();
        readonly Button btnDelete = new Button();
        readonly TextBox txtAlias = new TextBox();
        readonly ComboBox cboProtocol = new ComboBox();
        readonly TextBox txtHost = new TextBox();
        readonly NumericUpDown numPort = new NumericUpDown();
        readonly TextBox txtUser = new TextBox();
        readonly TextBox txtPass = new TextBox();
        readonly TextBox txtPath = new TextBox();
        readonly ComboBox cboDrive = new ComboBox();
        readonly CheckBox chkOpen = new CheckBox();
        readonly Button btnConnect = new Button();
        readonly Button btnDisconnect = new Button();
        readonly Button btnOpen = new Button();
        readonly Label lblStatus = new Label();
        readonly System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();   // 연결될 때까지 확인
        readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer();  // 연결 중 업로드 상황 확인
        readonly Queue<string> logLines = new Queue<string>();

        readonly Settings settings = Settings.Load();
        Profile selected;          // 목록에서 고른 연결. [새 연결]을 누르면 null 이다.
        bool fillingProfiles;      // 목록을 다시 채우는 동안 선택 변경 이벤트를 무시한다.
        bool disconnectWhenIdle;   // 업로드가 끝나면 자동으로 해제한다.
        bool closeWhenIdle;        // 자동 해제 뒤 창도 닫는다.
        bool asking;               // 확인 창을 띄운 동안에는 자동 해제를 미룬다.
        State state = State.Disconnected;
        Process rclone;
        string mountedDrive, mountedAlias, rcAddr, rcPass;
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

            cboProfiles.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProfiles.Margin = Padding.Empty;
            btnNew.Text = "새 연결";
            btnDelete.Text = "삭제";
            var profileRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
            profileRow.Controls.Add(cboProfiles);
            foreach (var b in new[] { btnNew, btnDelete })
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Margin = new Padding(Em(0.2), 0, 0, 0);
                profileRow.Controls.Add(b);
            }
            txtAlias.Width = field;
            SetCue(txtAlias, "예: 회사 NAS (탐색기에 보일 이름)");

            cboProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProtocol.Items.AddRange(ProtoNames);
            cboProtocol.Width = field;
            txtHost.Width = field;
            numPort.Minimum = 1; numPort.Maximum = 65535; numPort.Value = 21; numPort.Width = Em(6);
            txtUser.Width = field;
            txtPass.Width = field; txtPass.UseSystemPasswordChar = true;
            txtPath.Width = field;
            cboDrive.DropDownStyle = ComboBoxStyle.DropDownList; cboDrive.Width = Em(6);
            chkOpen.Text = "연결되면 탐색기로 열기"; chkOpen.AutoSize = true;
            SetCue(txtHost, "예: 192.168.0.10 또는 ftp.example.com");
            SetCue(txtPath, "비우면 로그인 기본 폴더");

            AddRow(grid, "연결 목록", profileRow);
            AddRow(grid, "별칭", txtAlias);
            AddRow(grid, "방식", cboProtocol);
            AddRow(grid, "서버 주소", txtHost);
            AddRow(grid, "포트", numPort);
            AddRow(grid, "아이디", txtUser);
            AddRow(grid, "비밀번호", txtPass);
            AddRow(grid, "원격 폴더", txtPath);
            AddRow(grid, "드라이브", cboDrive);
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

            // 업로드 중에는 두 줄을 쓰므로 높이를 고정해 창 크기가 들썩이지 않게 한다.
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(Em(21), Em(2.4));
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.AutoEllipsis = true;
            lblStatus.Anchor = AnchorStyles.None;
            lblStatus.Margin = new Padding(0, Em(0.4), 0, 0);
            grid.Controls.Add(lblStatus, 0, grid.RowCount);
            grid.SetColumnSpan(lblStatus, 2);
            grid.RowCount++;

            Controls.Add(grid);
            AcceptButton = btnConnect;
            // 목록 줄 전체 너비를 아래 입력 칸과 맞춘다. 버튼 글꼴이 정해지도록 폼에 붙인 뒤 계산한다.
            cboProfiles.Width = field - btnNew.PreferredSize.Width - btnNew.Margin.Left - btnDelete.PreferredSize.Width - btnDelete.Margin.Left;

            cboProfiles.SelectedIndexChanged += delegate { OnProfileSelected(); };
            btnNew.Click += delegate { NewProfile(); };
            btnDelete.Click += delegate { DeleteProfile(); };
            cboProtocol.SelectedIndexChanged += delegate { OnProtocolChanged(); };
            cboDrive.DropDown += delegate { FillDrives(SelectedLetter()); };
            btnConnect.Click += delegate { Connect(); };
            btnDisconnect.Click += delegate { Disconnect(true); };
            btnOpen.Click += delegate { OpenDrive(); };
            waitTimer.Interval = 500;
            waitTimer.Tick += delegate { OnWaitTick(); };
            statsTimer.Interval = 1000;
            statsTimer.Tick += delegate { OnStatsTick(); };

            chkOpen.Checked = settings.OpenAfterConnect;
            FillProfiles(settings.Profiles.Count > 0 ? settings.Profiles[0] : null);
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
            if (selected != null) txtPass.Focus();
            else txtAlias.Focus();
        }

        // 목록을 다시 채우고 sel 을 고른다. sel 이 null 이면 빈 새 연결 입력 상태로 둔다.
        void FillProfiles(Profile sel)
        {
            fillingProfiles = true;
            cboProfiles.Items.Clear();
            foreach (var p in settings.Profiles) cboProfiles.Items.Add(p.Alias);
            cboProfiles.SelectedIndex = sel == null ? -1 : settings.Profiles.IndexOf(sel);
            fillingProfiles = false;
            ShowProfile(sel);
        }

        void ShowProfile(Profile p)
        {
            selected = p;
            if (p == null) p = new Profile { Drive = FirstFreeLetter() };
            txtAlias.Text = p.Alias;
            int pi = Array.IndexOf(ProtoKeys, p.Protocol);
            cboProtocol.SelectedIndex = pi < 0 ? 0 : pi;
            numPort.Value = Math.Max(1, Math.Min(65535, p.Port));
            txtHost.Text = p.Host;
            txtUser.Text = p.User;
            txtPass.Clear();
            txtPath.Text = p.RemotePath;
            FillDrives(p.Drive);
            btnDelete.Enabled = state == State.Disconnected && selected != null;
        }

        void OnProfileSelected()
        {
            if (fillingProfiles || cboProfiles.SelectedIndex < 0) return;
            ShowProfile(settings.Profiles[cboProfiles.SelectedIndex]);
            txtPass.Focus();
        }

        void NewProfile()
        {
            FillProfiles(null);
            txtAlias.Focus();
        }

        void DeleteProfile()
        {
            if (selected == null) return;
            if (MessageBox.Show(this, "'" + selected.Alias + "' 연결을 목록에서 지울까요?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            settings.Profiles.Remove(selected);
            settings.Save();
            FillProfiles(settings.Profiles.Count > 0 ? settings.Profiles[0] : null);
        }

        // 입력한 내용을 연결 목록에 저장하고 맨 앞으로 올린다. 같은 별칭이 따로 있으면 덮어쓸지 묻는다.
        Profile SaveCurrentProfile(string host, string user, string letter)
        {
            string alias = Profile.CleanAlias(txtAlias.Text);
            if (alias.Length == 0) alias = Profile.CleanAlias(host);
            var same = settings.Find(alias);
            if (same != null && same != selected)
            {
                if (MessageBox.Show(this, "'" + alias + "' 별칭이 이미 목록에 있습니다. 덮어쓸까요?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return null;
                settings.Profiles.Remove(same);
            }
            if (selected != null) settings.Profiles.Remove(selected);
            var p = new Profile
            {
                Alias = alias, Protocol = ProtoKeys[cboProtocol.SelectedIndex], Host = host, Port = (int)numPort.Value,
                User = user, RemotePath = txtPath.Text.Trim().Replace('\\', '/'), Drive = letter
            };
            settings.Profiles.Insert(0, p);
            settings.OpenAfterConnect = chkOpen.Checked;
            settings.Save();
            string pass = txtPass.Text;
            FillProfiles(p);   // 목록 표시를 갱신하면서 비밀번호 칸이 비워지므로 되돌린다.
            txtPass.Text = pass;
            return p;
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

        static string FirstFreeLetter()
        {
            var used = Environment.GetLogicalDrives();
            for (char c = 'Z'; c >= 'D'; c--)
                if (!Array.Exists(used, d => char.ToUpperInvariant(d[0]) == c)) return c.ToString();
            return "Z";
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
            foreach (Control c in new Control[] { cboProfiles, btnNew, txtAlias, cboProtocol, txtHost, numPort, txtUser, txtPass, txtPath, cboDrive })
                c.Enabled = idle;
            btnDelete.Enabled = idle && selected != null;
            btnConnect.Enabled = idle;
            btnDisconnect.Enabled = !idle;
            btnOpen.Enabled = s == State.Connected;
            switch (s)
            {
                case State.Disconnected:
                    statsTimer.Stop();
                    disconnectWhenIdle = closeWhenIdle = false;
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
                    ShowConnected(null);
                    statsTimer.Start();
                    break;
            }
        }

        void ShowConnected(UploadInfo u)
        {
            string head = mountedAlias + " (" + mountedDrive + ":)";
            Text = "FTP 드라이브 - " + head;
            if (u == null || u.Pending == 0)
            {
                lblStatus.Text = "연결됨  " + head;
                lblStatus.ForeColor = Color.ForestGreen;
                return;
            }
            string current = u.Name == null ? "" : "\n" + u.Name + (u.Percent >= 0 ? "  " + u.Percent + "%" : "");
            lblStatus.Text = (disconnectWhenIdle ? "업로드가 끝나면 해제합니다" : "서버로 업로드 중") + " · 남은 파일 " + u.Pending + "개" + current;
            lblStatus.ForeColor = Color.DarkOrange;
        }

        UploadInfo GetUploadInfo(int timeoutMs)
        {
            string vfs = RcCall("vfs/stats", timeoutMs);
            if (vfs == null) return null;
            var u = UploadInfo.Parse(vfs, null);
            if (u != null && u.Pending > 0) u = UploadInfo.Parse(vfs, RcCall("core/stats", timeoutMs));
            return u;
        }

        void OnStatsTick()
        {
            if (state != State.Connected || asking) return;
            var u = GetUploadInfo(1000);
            if (u == null) return;
            ShowConnected(u);
            if (disconnectWhenIdle && u.Pending == 0)
            {
                bool close = closeWhenIdle;
                Disconnect(false);
                if (close) Close();
            }
        }

        void Connect()
        {
            string host = txtHost.Text.Trim(), user = txtUser.Text.Trim(), letter = SelectedLetter();
            if (host.Length == 0) { Warn("서버 주소를 입력하세요."); txtHost.Focus(); return; }
            if (user.Length == 0) { Warn("아이디를 입력하세요."); txtUser.Focus(); return; }
            var profile = SaveCurrentProfile(host, user, letter); // 연결에 실패해도 입력한 정보는 목록에 남긴다.
            if (profile == null) return;
            string exe = FindRclone();
            if (exe == null) { Warn("rclone.exe 를 찾을 수 없습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (!WinFspInstalled()) { Warn("WinFsp 가 설치되어 있지 않습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (Array.Exists(Environment.GetLogicalDrives(), d => d.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
            { Warn(letter + ": 드라이브는 이미 사용 중입니다. 다른 드라이브를 고르세요."); return; }

            string proto = profile.Protocol, remotePath = profile.RemotePath;

            string obscured = "";
            if (txtPass.Text.Length > 0)
            {
                obscured = Obscure(exe, txtPass.Text);
                if (obscured == null) { Warn("비밀번호를 rclone 형식으로 바꾸지 못했습니다."); return; }
            }

            rcAddr = "127.0.0.1:" + FreePort();
            rcPass = Guid.NewGuid().ToString("N");
            string volname = @"\\" + Regex.Replace(host, @"[^A-Za-z0-9.\-_]", "-") + @"\" + profile.Alias;
            string args = string.Join(" ", new[] {
                "mount", Quote((proto == "sftp" ? ":sftp:" : ":ftp:") + remotePath), letter + ":",
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
            mountedAlias = profile.Alias;
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

        // 업로드가 남아 있고 askIfUploading 이면 어떻게 할지 묻는다. 지금 해제했으면 true 를 돌려준다.
        bool Disconnect(bool askIfUploading)
        {
            var p = rclone;
            if (p == null) return true;
            if (askIfUploading && state == State.Connected)
            {
                var u = GetUploadInfo(3000);
                if (u != null && u.Pending > 0)
                {
                    asking = true;
                    var r = MessageBox.Show(this,
                        "아직 서버로 올리는 중인 파일이 " + u.Pending + "개 있습니다.\n\n" +
                        "[예]  업로드가 끝나면 자동으로 해제합니다.\n" +
                        "[아니요]  지금 바로 해제합니다. 다 올라가지 못한 파일은 서버에 저장되지 않을 수 있습니다.\n" +
                        "[취소]  해제하지 않고 계속 연결해 둡니다.",
                        Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    asking = false;
                    if (rclone != p) return true; // 묻는 사이에 연결이 이미 끊어졌다.
                    disconnectWhenIdle = r == DialogResult.Yes;
                    if (r != DialogResult.No)
                    {
                        ShowConnected(u);
                        return false;
                    }
                }
            }
            rclone = null;
            waitTimer.Stop();
            statsTimer.Stop();
            lblStatus.Text = "연결 해제 중...";
            lblStatus.Refresh();
            Cursor.Current = Cursors.WaitCursor;
            // rc 로 정상 종료를 요청하고, 응답이 없으면 강제로 끝낸다.
            if (RcCall("core/quit", 5000) == null || !p.WaitForExit(15000)) Kill(p);
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
                    asking = true;
                    bool yes = MessageBox.Show(this, "연결을 해제하고 종료할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    asking = false;
                    if (!yes || !Disconnect(true))
                    {
                        closeWhenIdle = yes && disconnectWhenIdle; // [예]를 골랐으면 업로드가 끝난 뒤 창도 닫는다.
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
