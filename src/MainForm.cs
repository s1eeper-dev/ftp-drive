// FTP 드라이브 메인 창: 연결 목록(여러 서버 동시 연결), 선택한 연결의 설정 편집·연결·해제, 업데이트 버튼.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FtpDrive
{
    class MainForm : Form
    {
        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        // 설치 폴더(Program Files)는 쓰기 금지라서 설정과 로그는 사용자별 폴더에 둔다.
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FtpDrive");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

        readonly ListView lvProfiles = new ListView();
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
        readonly CheckBox chkCert = new CheckBox();
        readonly CheckBox chkOpen = new CheckBox();
        readonly Button btnConnect = new Button();
        readonly Button btnDisconnect = new Button();
        readonly Button btnOpen = new Button();
        readonly Label lblStatus = new Label();
        readonly LinkLabel lnkVersion = new LinkLabel();
        readonly Button btnUpdate = new Button();
        readonly ToolTip tips = new ToolTip();
        readonly System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();   // 드라이브가 나타날 때까지 확인
        readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer();  // 연결마다 업로드 상황 확인
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer(); // 시작 2초 뒤, 그다음 30분마다 새 버전 확인
        readonly Control[] editors;

        readonly Settings settings = Settings.Load();
        readonly Dictionary<Profile, Connection> conns = new Dictionary<Profile, Connection>(); // 연결 중이거나 연결된 서버
        Profile selected;          // 목록에서 고른 연결. [새 연결]을 누르면 null 이다.
        bool filling;              // 목록을 다시 채우는 동안 선택 변경 이벤트를 무시한다.
        bool closeWhenIdle;        // 업로드가 끝나 모두 해제되면 창도 닫는다.
        int asking;                // 확인 창을 띄운 동안에는 타이머가 자동 해제·실패 알림을 미룬다.
        UpdateInfo update;         // 찾아 둔 새 버전, 없으면 null
        bool checkingUpdate;
        bool updating;             // 업데이트 설치를 시작해 창을 닫는 중

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

            // 연결 목록: 별칭 | 드라이브 | 상태
            lvProfiles.View = View.Details;
            lvProfiles.FullRowSelect = true;
            lvProfiles.HideSelection = false;
            lvProfiles.MultiSelect = false;
            lvProfiles.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvProfiles.Columns.Add("별칭");
            lvProfiles.Columns.Add("드라이브");
            lvProfiles.Columns.Add("상태");
            lvProfiles.Size = new Size(Em(10), Em(7.5));
            lvProfiles.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            lvProfiles.Margin = new Padding(0, 0, 0, Em(0.3));
            grid.Controls.Add(lvProfiles, 0, grid.RowCount);
            grid.SetColumnSpan(lvProfiles, 2);
            grid.RowCount++;

            btnNew.Text = "새 연결";
            btnDelete.Text = "삭제";
            var listButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, Em(0.5)) };
            foreach (var b in new[] { btnNew, btnDelete })
            {
                b.AutoSize = true;
                b.MinimumSize = new Size(Em(4.5), 0);
                listButtons.Controls.Add(b);
            }
            grid.Controls.Add(listButtons, 0, grid.RowCount);
            grid.SetColumnSpan(listButtons, 2);
            grid.RowCount++;

            txtAlias.Width = field;
            cboProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProtocol.Items.AddRange(Profile.Names);
            cboProtocol.Width = field;
            txtHost.Width = field;
            numPort.Minimum = 1; numPort.Maximum = 65535; numPort.Value = 21; numPort.Width = Em(6);
            txtUser.Width = field;
            txtPass.Width = field; txtPass.UseSystemPasswordChar = true;
            txtPath.Width = field;
            cboDrive.DropDownStyle = ComboBoxStyle.DropDownList; cboDrive.Width = Em(6);
            chkCert.Text = "인증서 확인 안 함 (자체 서명 인증서)"; chkCert.AutoSize = true;
            chkOpen.Text = "연결되면 탐색기로 열기"; chkOpen.AutoSize = true;
            SetCue(txtAlias, "예: 회사 NAS (탐색기에 보일 이름)");
            SetCue(txtHost, "예: 192.168.0.10 또는 nas.example.com");
            SetCue(txtPath, "비우면 로그인 기본 폴더");
            tips.SetToolTip(chkCert, "시놀로지 기본 인증서처럼 공인되지 않은 인증서를 쓰는 서버에 연결할 때 켭니다.\n연결은 여전히 암호화되지만, 상대가 진짜 그 서버인지는 확인하지 않습니다.");

            AddRow(grid, "별칭", txtAlias);
            AddRow(grid, "방식", cboProtocol);
            AddRow(grid, "서버 주소", txtHost);
            AddRow(grid, "포트", numPort);
            AddRow(grid, "아이디", txtUser);
            AddRow(grid, "비밀번호", txtPass);
            AddRow(grid, "원격 폴더", txtPath);
            AddRow(grid, "드라이브", cboDrive);
            AddRow(grid, "", chkCert);
            AddRow(grid, "", chkOpen);
            editors = new Control[] { txtAlias, cboProtocol, txtHost, numPort, txtUser, txtPass, txtPath, cboDrive };

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

            // 맨 아래 줄: 왼쪽은 지금 버전과 [업데이트 확인], 오른쪽은 새 버전이 있을 때만 보이는 노란 버튼.
            var footer = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, Em(0.3), 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lnkVersion.AutoSize = true;
            lnkVersion.Anchor = AnchorStyles.Left;
            lnkVersion.LinkColor = SystemColors.GrayText;
            lnkVersion.Text = "버전 " + Updater.Text(Updater.Current) + " · 업데이트 확인";
            btnUpdate.AutoSize = true;
            btnUpdate.Anchor = AnchorStyles.Right;
            btnUpdate.Margin = Padding.Empty;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.BackColor = Color.FromArgb(255, 205, 64);
            btnUpdate.FlatAppearance.BorderColor = Color.FromArgb(214, 160, 0);
            btnUpdate.Visible = false;
            tips.SetToolTip(btnUpdate, "누르면 새 버전으로 업데이트할 수 있습니다.");
            footer.Controls.Add(lnkVersion, 0, 0);
            footer.Controls.Add(btnUpdate, 1, 0);
            grid.Controls.Add(footer, 0, grid.RowCount);
            grid.SetColumnSpan(footer, 2);
            grid.RowCount++;

            Controls.Add(grid);
            AcceptButton = btnConnect;
            // 버튼이 나타나고 사라져도 창 높이가 바뀌지 않게 맨 아래 줄 높이를 버튼에 맞춰 고정한다.
            btnUpdate.Text = "새 버전 0.0.0";
            footer.Height = btnUpdate.PreferredSize.Height;
            footer.MinimumSize = new Size(0, footer.Height);

            lvProfiles.SelectedIndexChanged += delegate { OnListSelection(); };
            lvProfiles.DoubleClick += delegate { OnListDoubleClick(); };
            lvProfiles.ClientSizeChanged += delegate { SizeColumns(); };
            btnNew.Click += delegate { FillList(null); txtAlias.Focus(); };
            btnDelete.Click += delegate { DeleteProfile(); };
            cboProtocol.SelectedIndexChanged += delegate { OnProtocolChanged(); };
            cboDrive.DropDown += delegate { FillDrives(SelectedLetter()); };
            btnConnect.Click += delegate { Connect(); };
            btnDisconnect.Click += delegate { var c = Active(selected); if (c != null) Disconnect(c, true); };
            btnOpen.Click += delegate { var c = Active(selected); if (c != null) OpenDrive(c.Drive); };
            waitTimer.Interval = 500;
            waitTimer.Tick += delegate { OnWaitTick(); };
            statsTimer.Interval = 1000;
            statsTimer.Tick += delegate { OnStatsTick(); };
            updateTimer.Interval = 2000;
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = 30 * 60 * 1000;
                CheckForUpdate(false);
            };
            lnkVersion.LinkClicked += delegate { CheckForUpdate(true); };
            btnUpdate.Click += delegate { OpenUpdate(); };

            chkOpen.Checked = settings.OpenAfterConnect;
            FillList(settings.Profiles.Count > 0 ? settings.Profiles[0] : null);
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
            SizeColumns();
            if (selected != null) txtPass.Focus();
            else txtAlias.Focus();
            updateTimer.Start();
        }

        // 드라이브·상태 칸은 고정 폭, 별칭 칸이 나머지를 쓴다.
        void SizeColumns()
        {
            int drive = Em(3.8), status = Em(6.5);
            lvProfiles.Columns[1].Width = drive;
            lvProfiles.Columns[2].Width = status;
            lvProfiles.Columns[0].Width = Math.Max(Em(4), lvProfiles.ClientSize.Width - drive - status);
        }

        Connection Active(Profile p)
        {
            Connection c;
            return p != null && conns.TryGetValue(p, out c) ? c : null;
        }

        DialogResult Ask(string msg, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def = MessageBoxDefaultButton.Button1)
        {
            asking++;
            try { return MessageBox.Show(this, msg, Text, buttons, icon, def); }
            finally { asking--; }
        }

        void Warn(string msg) { Ask(msg, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        // ───────────── 연결 목록과 편집 칸 ─────────────

        // 목록을 다시 채우고 sel 을 고른다. sel 이 null 이면 빈 새 연결 입력 상태로 둔다.
        void FillList(Profile sel)
        {
            filling = true;
            lvProfiles.BeginUpdate();
            lvProfiles.Items.Clear();
            foreach (var p in settings.Profiles)
                lvProfiles.Items.Add(new ListViewItem(new[] { p.Alias, "", "" }) { Tag = p, UseItemStyleForSubItems = false });
            lvProfiles.EndUpdate();
            foreach (ListViewItem it in lvProfiles.Items)
                if (it.Tag == sel) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
            filling = false;
            ShowProfile(sel);
        }

        void OnListSelection()
        {
            if (filling) return;
            if (lvProfiles.SelectedItems.Count > 0)
            {
                ShowProfile((Profile)lvProfiles.SelectedItems[0].Tag);
                if (Active(selected) == null) txtPass.Focus();
                return;
            }
            // 다른 줄로 옮길 때도 '선택 없음'이 잠깐 지나가므로, 다 끝난 뒤에도 비어 있으면(빈 곳 클릭) 원래 줄을 다시 고른다.
            BeginInvoke(new Action(() =>
            {
                if (lvProfiles.SelectedItems.Count > 0 || selected == null) return;
                foreach (ListViewItem it in lvProfiles.Items)
                    if (it.Tag == selected) it.Selected = true;
            }));
        }

        void OnListDoubleClick()
        {
            var c = Active(selected);
            if (c != null && c.Mounted) OpenDrive(c.Drive);
            else if (c == null) txtPass.Focus();
        }

        void ShowProfile(Profile p)
        {
            selected = p;
            var shown = p ?? new Profile { Drive = FirstFreeLetter() };
            txtAlias.Text = shown.Alias;
            int pi = Array.IndexOf(Profile.Keys, shown.Protocol);
            cboProtocol.SelectedIndex = pi < 0 ? 0 : pi;
            numPort.Value = Math.Max(1, Math.Min(65535, shown.Port));
            txtHost.Text = shown.Host;
            txtUser.Text = shown.User;
            txtPass.Clear();
            txtPath.Text = shown.RemotePath;
            chkCert.Checked = shown.SkipCertCheck;
            var c = Active(p);
            FillDrives(c != null ? c.Drive : shown.Drive);
            RefreshView();
        }

        void OnProtocolChanged()
        {
            // 포트가 기본값 중 하나였을 때만 새 방식의 기본 포트로 바꾼다. 직접 입력한 포트는 건드리지 않는다.
            if (Array.IndexOf(Profile.Ports, (int)numPort.Value) >= 0)
                numPort.Value = Profile.Ports[cboProtocol.SelectedIndex];
            chkCert.Enabled = Active(selected) == null && Profile.UsesTls(CurrentProtocol());
        }

        string CurrentProtocol() { return Profile.Keys[Math.Max(0, cboProtocol.SelectedIndex)]; }

        void DeleteProfile()
        {
            if (selected == null || Active(selected) != null) return;
            if (Ask("'" + selected.Alias + "' 연결을 목록에서 지울까요?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            settings.Profiles.Remove(selected);
            settings.Save();
            FillList(settings.Profiles.Count > 0 ? settings.Profiles[0] : null);
        }

        // 입력한 내용을 목록에 저장한다. 고른 연결은 그 자리에서 고치고, 새 연결은 목록 끝에 붙인다.
        Profile SaveCurrentProfile(string host, string user, string letter)
        {
            string alias = Profile.CleanAlias(txtAlias.Text);
            if (alias.Length == 0) alias = Profile.CleanAlias(host);
            var p = selected;
            var same = settings.Find(alias);
            if (same != null && same != selected)
            {
                if (Active(same) != null) { Warn("'" + alias + "' 별칭은 지금 연결된 다른 서버가 쓰고 있습니다. 다른 별칭을 입력하세요."); return null; }
                if (Ask("'" + alias + "' 별칭이 이미 목록에 있습니다. 덮어쓸까요?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return null;
                if (p == null) p = same; // 새 연결이면 같은 별칭 항목 자리를 그대로 쓴다.
                else settings.Profiles.Remove(same);
            }
            if (p == null)
            {
                p = new Profile();
                settings.Profiles.Add(p);
            }
            p.Alias = alias;
            p.Protocol = CurrentProtocol();
            p.Host = host;
            p.Port = (int)numPort.Value;
            p.User = user;
            p.RemotePath = txtPath.Text.Trim().Replace('\\', '/');
            p.Drive = letter;
            p.SkipCertCheck = chkCert.Checked && Profile.UsesTls(p.Protocol);
            settings.OpenAfterConnect = chkOpen.Checked;
            settings.Save();
            string pass = txtPass.Text;
            FillList(p);   // 목록 표시를 갱신하면서 비밀번호 칸이 비워지므로 되돌린다.
            txtPass.Text = pass;
            return p;
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

        // 버튼 사용 가능 여부, 목록의 상태 칸, 아래 상태 줄, 창 제목을 지금 연결 상황에 맞춘다.
        void RefreshView()
        {
            var c = Active(selected);
            bool editable = c == null;
            foreach (var x in editors) x.Enabled = editable;
            chkCert.Enabled = editable && Profile.UsesTls(CurrentProtocol());
            btnDelete.Enabled = editable && selected != null;
            btnConnect.Enabled = editable;
            btnDisconnect.Enabled = c != null;
            btnOpen.Enabled = c != null && c.Mounted;

            foreach (ListViewItem it in lvProfiles.Items)
            {
                var p = (Profile)it.Tag;
                var pc = Active(p);
                string drive = (pc != null ? pc.Drive : p.Drive) + ":", status;
                Color color;
                if (pc == null) { status = "연결 안 됨"; color = SystemColors.GrayText; }
                else if (!pc.Mounted) { status = "연결 중..."; color = Color.DarkOrange; }
                else if (pc.Upload != null && pc.Upload.Pending > 0)
                {
                    status = (pc.DisconnectWhenIdle ? "해제 대기 " : "업로드 ") + pc.Upload.Pending + "개";
                    color = Color.DarkOrange;
                }
                else { status = "연결됨"; color = Color.ForestGreen; }
                if (it.SubItems[1].Text != drive) it.SubItems[1].Text = drive;
                if (it.SubItems[2].Text != status) it.SubItems[2].Text = status;
                if (it.SubItems[2].ForeColor != color) it.SubItems[2].ForeColor = color;
            }

            if (c == null)
            {
                lblStatus.Text = selected == null ? "새 연결" : "연결 안 됨";
                lblStatus.ForeColor = SystemColors.GrayText;
            }
            else if (!c.Mounted)
            {
                lblStatus.Text = "연결 중...";
                lblStatus.ForeColor = Color.DarkOrange;
            }
            else if (c.Upload != null && c.Upload.Pending > 0)
            {
                var u = c.Upload;
                string current = u.Name == null ? "" : "\n" + u.Name + (u.Percent >= 0 ? "  " + u.Percent + "%" : "");
                lblStatus.Text = (c.DisconnectWhenIdle ? "업로드가 끝나면 해제합니다" : "서버로 업로드 중") + " · 남은 파일 " + u.Pending + "개" + current;
                lblStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                lblStatus.Text = "연결됨  " + c.Profile.Alias + " (" + c.Drive + ":)";
                lblStatus.ForeColor = Color.ForestGreen;
            }
            Text = conns.Count == 0 ? "FTP 드라이브" : "FTP 드라이브 - 연결 " + conns.Count + "개";
        }

        // ───────────── 연결과 해제 ─────────────

        void Connect()
        {
            string host = txtHost.Text.Trim(), user = txtUser.Text.Trim(), letter = SelectedLetter();
            if (host.Length == 0) { Warn("서버 주소를 입력하세요."); txtHost.Focus(); return; }
            if (user.Length == 0) { Warn("아이디를 입력하세요."); txtUser.Focus(); return; }
            var profile = SaveCurrentProfile(host, user, letter); // 연결에 실패해도 입력한 정보는 목록에 남긴다.
            if (profile == null) return;
            string exe = Connection.FindRclone();
            if (exe == null) { Warn("rclone.exe 를 찾을 수 없습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (!Connection.WinFspInstalled()) { Warn("WinFsp 가 설치되어 있지 않습니다.\n\n설치 프로그램(FtpDrive-Setup.exe)을 다시 실행하세요."); return; }
            if (Array.Exists(Environment.GetLogicalDrives(), d => d.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
            { Warn(letter + ": 드라이브는 이미 사용 중입니다. 다른 드라이브를 고르세요."); return; }

            string obscured = "";
            if (txtPass.Text.Length > 0)
            {
                obscured = Connection.Obscure(exe, txtPass.Text);
                if (obscured == null) { Warn("비밀번호를 rclone 형식으로 바꾸지 못했습니다."); return; }
            }
            var c = new Connection(profile);
            string err = c.Start(exe, obscured, OnExitedFromOtherThread);
            if (err != null) { Warn(err); return; }
            conns[profile] = c;
            closeWhenIdle = false;
            waitTimer.Start();
            statsTimer.Start();
            RefreshView();
        }

        void OnExitedFromOtherThread(Connection c)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => OnConnectionExited(c))); }
            catch (InvalidOperationException) { } // 창이 이미 닫히는 중
        }

        void OnConnectionExited(Connection c)
        {
            if (Active(c.Profile) != c) return; // 직접 해제한 경우는 Disconnect 에서 이미 정리했다.
            conns.Remove(c.Profile);
            try { c.Process.WaitForExit(); } catch { } // 남은 출력까지 받아 둔다.
            c.SaveLog();
            RefreshView();
            ShowFailure(c, c.Mounted ? "연결이 끊어졌습니다." : "연결하지 못했습니다.");
        }

        void OnWaitTick()
        {
            if (asking > 0) return;
            bool waiting = false;
            foreach (var c in new List<Connection>(conns.Values))
            {
                if (c.Mounted || Active(c.Profile) != c) continue;
                if (Directory.Exists(c.Drive + @":\"))
                {
                    c.Mounted = true;
                    if (c.Profile == selected) txtPass.Clear();
                    RefreshView();
                    if (chkOpen.Checked) OpenDrive(c.Drive);
                }
                else if ((DateTime.Now - c.Started).TotalSeconds > 30)
                {
                    conns.Remove(c.Profile);
                    c.Kill();
                    c.SaveLog();
                    RefreshView();
                    ShowFailure(c, "30초 안에 연결되지 않았습니다.");
                }
                else waiting = true;
            }
            if (!waiting) waitTimer.Stop();
        }

        void OnStatsTick()
        {
            if (asking > 0) return;
            foreach (var c in new List<Connection>(conns.Values))
            {
                if (!c.Mounted || Active(c.Profile) != c) continue;
                var u = c.GetUploadInfo(1000);
                if (u == null) continue;
                c.Upload = u;
                if (c.DisconnectWhenIdle && u.Pending == 0) Disconnect(c, false);
            }
            RefreshView();
            if (conns.Count == 0)
            {
                statsTimer.Stop();
                if (closeWhenIdle) Close();
            }
        }

        // 업로드가 남아 있고 ask 이면 어떻게 할지 묻는다. 지금 해제했으면 true 를 돌려준다.
        bool Disconnect(Connection c, bool ask)
        {
            if (Active(c.Profile) != c) return true;
            if (ask && c.Mounted)
            {
                var u = c.GetUploadInfo(3000);
                if (u != null && u.Pending > 0)
                {
                    var r = Ask("'" + c.Profile.Alias + "'에 아직 서버로 올리는 중인 파일이 " + u.Pending + "개 있습니다.\n\n" +
                        "[예]  업로드가 끝나면 자동으로 해제합니다.\n" +
                        "[아니요]  지금 바로 해제합니다. 다 올라가지 못한 파일은 서버에 저장되지 않을 수 있습니다.\n" +
                        "[취소]  해제하지 않고 계속 연결해 둡니다.",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (Active(c.Profile) != c) return true; // 묻는 사이에 연결이 이미 끊어졌다.
                    c.DisconnectWhenIdle = r == DialogResult.Yes;
                    if (r != DialogResult.No)
                    {
                        c.Upload = u;
                        RefreshView();
                        return false;
                    }
                }
            }
            conns.Remove(c.Profile);
            lblStatus.Text = "'" + c.Profile.Alias + "' 연결 해제 중...";
            lblStatus.Refresh();
            Cursor.Current = Cursors.WaitCursor;
            c.Stop();
            Cursor.Current = Cursors.Default;
            RefreshView();
            if (c.Profile == selected) txtPass.Focus();
            return true;
        }

        // 모든 연결을 해제한다. 지금 모두 해제됐으면 true. [예]로 업로드를 기다리기로 했거나 취소하면 false.
        bool DisconnectAll(bool ask)
        {
            var all = new List<Connection>(conns.Values);
            if (ask)
            {
                int pending = 0;
                var busy = new List<Connection>();
                foreach (var c in all)
                {
                    var u = c.Mounted ? c.GetUploadInfo(3000) : null;
                    if (u == null || u.Pending == 0) continue;
                    c.Upload = u;
                    pending += u.Pending;
                    busy.Add(c);
                }
                if (pending > 0)
                {
                    var r = Ask("아직 서버로 올리는 중인 파일이 " + pending + "개 있습니다.\n\n" +
                        "[예]  업로드가 끝나는 대로 연결을 해제합니다.\n" +
                        "[아니요]  지금 모두 해제합니다. 다 올라가지 못한 파일은 서버에 저장되지 않을 수 있습니다.\n" +
                        "[취소]  해제하지 않습니다.",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (r == DialogResult.Cancel) return false;
                    if (r == DialogResult.Yes)
                    {
                        foreach (var c in all)
                        {
                            if (busy.Contains(c)) c.DisconnectWhenIdle = true;
                            else Disconnect(c, false);
                        }
                        RefreshView();
                        return conns.Count == 0;
                    }
                }
            }
            foreach (var c in all) Disconnect(c, false);
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (conns.Count > 0)
            {
                if (e.CloseReason == CloseReason.UserClosing && !updating)
                {
                    bool yes = Ask("연결 " + conns.Count + "개를 모두 해제하고 종료할까요?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    if (!yes || !DisconnectAll(true))
                    {
                        closeWhenIdle = yes && conns.Count > 0; // [예]로 업로드를 기다리기로 했으면 다 끝난 뒤 창도 닫는다.
                        e.Cancel = true;
                        return;
                    }
                }
                else DisconnectAll(false);
            }
            base.OnFormClosing(e);
        }

        static void OpenDrive(string drive)
        {
            try { Process.Start("explorer.exe", drive + @":\"); } catch { }
        }

        void ShowFailure(Connection c, string head)
        {
            string log = c.LogText();
            var sb = new StringBuilder("'" + c.Profile.Alias + "' " + head);
            string hint = Connection.Hint(log);
            if (hint != null) sb.Append("\n\n").Append(hint);
            var lines = log.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                sb.Append("\n\n[rclone 메시지]");
                for (int i = Math.Max(0, lines.Length - 6); i < lines.Length; i++)
                    sb.Append("\n").Append(lines[i].Length > 300 ? lines[i].Substring(0, 300) + "..." : lines[i]);
            }
            sb.Append("\n\n전체 기록: ").Append(c.LogPath);
            Warn(sb.ToString());
        }

        // ───────────── 업데이트 ─────────────

        // 새 버전을 확인한다. manual 이면 결과를 알려 주고, 새 버전이 있으면 바로 업데이트 창을 연다.
        void CheckForUpdate(bool manual)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            string cur = "버전 " + Updater.Text(Updater.Current) + " · ";
            if (manual) lnkVersion.Text = cur + "확인 중...";
            Updater.CheckAsync((info, error) =>
            {
                checkingUpdate = false;
                if (IsDisposed) return;
                if (error == null) update = info; // 확인하지 못했으면 찾아 둔 새 버전은 그대로 둔다.
                btnUpdate.Visible = update != null;
                if (update != null) btnUpdate.Text = "새 버전 " + update.VersionText;
                lnkVersion.Text = cur + (!manual ? "업데이트 확인" : error != null ? "확인 실패" : update == null ? "최신 버전입니다" : "새 버전이 있습니다");
                if (!manual) return;
                if (error != null) Warn(error);
                else if (update != null) OpenUpdate();
            });
        }

        void OpenUpdate()
        {
            if (update == null) return;
            using (var dlg = new UpdateForm(update, Font, PrepareForUpdate))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // 조용히 설치하고, 설치 프로그램이 끝나면 FTP 드라이브를 다시 연다 (FtpDrive.iss 의 [Run]).
                    Process.Start(new ProcessStartInfo(dlg.SetupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Warn("업데이트를 시작하지 못했습니다. 관리자 권한 확인 창에서 [아니요]를 눌렀다면 다시 시도하세요.\n\n" + ex.Message);
                    return;
                }
            }
            updating = true;
            Close();
        }

        // 설치하는 동안 프로그램이 닫히므로 연결부터 모두 해제한다. 해제하지 못하면 false.
        bool PrepareForUpdate()
        {
            if (conns.Count == 0) return true;
            return Ask("업데이트하려면 연결 " + conns.Count + "개를 모두 해제해야 합니다. 지금 해제할까요?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
                && DisconnectAll(true);
        }
    }
}
