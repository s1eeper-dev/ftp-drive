// GitHub 릴리스에서 새 버전을 확인하고 설치 파일을 내려받아 검증하는 자동 업데이트와 그 확인 창.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FtpDrive
{
    class UpdateInfo
    {
        public Version Version;
        public string Date = "";
        public long Size;
        public string Url;
        public string Sha256;      // GitHub 이 알려 준 설치 파일 SHA-256, 없으면 null

        public string VersionText { get { return Updater.Text(Version); } }
    }

    static class Updater
    {
        const string ReleasesUrl = "https://api.github.com/repos/s1eeper-dev/ftp-drive/releases?per_page=30";
        const string AssetName = "FtpDrive-Setup.exe";
        public static readonly Version Current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        // 1.2 와 1.2.0.0 처럼 자릿수가 달라도 같은 버전으로 비교되도록 세 자리로 맞춘다.
        public static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        public static string Text(Version v) { return v.ToString(3); }

        static WebClient NewClient()
        {
            // .NET Framework 기본 설정으로는 TLS 1.2 를 쓰지 않아 GitHub 에 접속하지 못할 수 있다.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "FtpDrive-Updater";
            return wc;
        }

        // 새 버전을 확인한다. done(새 버전 또는 null, 오류 문구 또는 null) 은 부른 스레드(UI)에서 불린다.
        public static void CheckAsync(Action<UpdateInfo, string> done)
        {
            var wc = NewClient();
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            wc.DownloadStringCompleted += (s, e) =>
            {
                wc.Dispose();
                if (e.Error != null) { done(null, "새 버전을 확인하지 못했습니다. 인터넷 연결을 확인하세요."); return; }
                try { done(Parse(e.Result, Current), null); }
                catch (FormatException ex) { done(null, ex.Message); }
            };
            wc.DownloadStringAsync(new Uri(ReleasesUrl));
        }

        // GitHub 릴리스 목록(JSON)에서 current 보다 새 버전 중 가장 새 것을 고른다. 없으면 null.
        public static UpdateInfo Parse(string json, Version current)
        {
            IList releases;
            try { releases = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as IList; }
            catch (ArgumentException) { releases = null; }
            if (releases == null) throw new FormatException("새 버전 정보를 읽지 못했습니다.");

            current = Normalize(current);
            UpdateInfo best = null;
            foreach (var item in releases)
            {
                var r = item as Dictionary<string, object>;
                if (r == null || Flag(r, "draft") || Flag(r, "prerelease")) continue;
                var v = ParseTag(Get(r, "tag_name") as string);
                if (v == null || v <= current || (best != null && v <= best.Version)) continue;
                var asset = FindAsset(Get(r, "assets") as IList);
                if (asset == null) continue; // 설치 파일이 없는 릴리스는 건너뛴다.
                string date = Get(r, "published_at") as string ?? "";
                string digest = Get(asset, "digest") as string;
                best = new UpdateInfo
                {
                    Version = v, Date = date.Length > 10 ? date.Substring(0, 10) : date, Size = ToLong(Get(asset, "size")),
                    Url = (string)Get(asset, "browser_download_url"),
                    Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null
                };
            }
            return best;
        }

        static Dictionary<string, object> FindAsset(IList assets)
        {
            if (assets == null) return null;
            foreach (var a in assets)
            {
                var d = a as Dictionary<string, object>;
                if (d != null && Get(d, "name") as string == AssetName && Get(d, "browser_download_url") is string) return d;
            }
            return null;
        }

        static Version ParseTag(string tag)
        {
            Version v;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            return Normalize(v);
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static bool Flag(Dictionary<string, object> d, string key) { return (Get(d, key) as bool?) == true; }

        static long ToLong(object v)
        {
            try { return v == null ? 0 : Convert.ToInt64(v); }
            catch (Exception) { return 0; }
        }

        // 내려받은 파일의 크기와 SHA-256 이 GitHub 이 알려 준 값과 같은지 확인한다.
        public static bool Verify(string path, UpdateInfo info)
        {
            var f = new FileInfo(path);
            if (!f.Exists || f.Length != info.Size) return false;
            if (info.Sha256 == null) return true;
            using (var sha = SHA256.Create())
            using (var s = f.OpenRead())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant() == info.Sha256;
        }

        // 설치 파일을 임시 폴더에 내려받는다. progress(%) 와 done(경로 또는 null, 오류 문구 또는 null) 은 부른 스레드에서 불린다.
        public static void DownloadAsync(UpdateInfo info, Action<int> progress, Action<string, string> done)
        {
            string dir = Path.Combine(Path.GetTempPath(), "FtpDriveUpdate");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true); // 지난 업데이트 파일 정리
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "FtpDrive-Setup-" + info.VersionText + ".exe");
            var wc = NewClient();
            wc.DownloadProgressChanged += (s, e) =>
            {
                if (info.Size > 0) progress((int)Math.Min(100, e.BytesReceived * 100 / info.Size));
            };
            wc.DownloadFileCompleted += (s, e) =>
            {
                wc.Dispose();
                if (e.Error != null) { done(null, "내려받는 중 연결이 끊겼습니다. 다시 시도하세요."); return; }
                if (!Verify(path, info))
                {
                    try { File.Delete(path); } catch (IOException) { }
                    done(null, "내려받은 파일이 손상되었습니다. 다시 시도하세요.");
                    return;
                }
                done(path, null);
            };
            wc.DownloadFileAsync(new Uri(info.Url), path);
        }
    }

    // [새 버전] 버튼을 누르면 뜨는 창. [지금 업데이트]를 누르면 내려받아 검증한 뒤 OK 로 닫힌다.
    class UpdateForm : Form
    {
        readonly Func<bool> prepare;   // 설치 전에 연결을 해제한다. 해제하지 못하면 false.
        readonly Label lblHead = new Label();
        readonly Label lblInfo = new Label();
        readonly ProgressBar bar = new ProgressBar();
        readonly Button btnLater = new Button();
        readonly Button btnGo = new Button();
        UpdateInfo info;
        bool busy;

        public string SetupPath { get; private set; }

        public UpdateForm(UpdateInfo info, Font font, Func<bool> prepare)
        {
            this.prepare = prepare;
            Font = font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            int pad = Em(0.8), width = Em(22);
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(pad, pad), Margin = new Padding(0, 0, pad, pad) };
            lblHead.AutoSize = true;
            lblHead.MaximumSize = new Size(width, 0);
            lblHead.Margin = new Padding(0, 0, 0, Em(0.4));
            lblInfo.AutoSize = true;
            lblInfo.MaximumSize = new Size(width, 0);
            lblInfo.ForeColor = SystemColors.GrayText;
            lblInfo.Margin = new Padding(0, Em(0.4), 0, 0);
            lblInfo.Text = "업데이트하는 동안 프로그램이 잠깐 닫혔다가 다시 열립니다. 연결 목록은 그대로입니다.\n관리자 권한 확인 창이 뜨면 [예]를 누르세요.";
            bar.Size = new Size(width, Em(1));
            bar.Visible = false;
            btnLater.Text = "나중에";
            btnGo.Text = "지금 업데이트";
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0, Em(0.6), 0, 0) };
            foreach (var b in new[] { btnLater, btnGo })
            {
                b.AutoSize = true;
                b.MinimumSize = new Size(Em(5.5), 0);
                buttons.Controls.Add(b);
            }
            foreach (Control c in new Control[] { lblHead, lblInfo, bar, buttons }) grid.Controls.Add(c);
            Controls.Add(grid);
            AcceptButton = btnGo;
            CancelButton = btnLater;
            btnLater.Click += delegate { Close(); };
            btnGo.Click += delegate { Start(); };
            ShowInfo(info);
        }

        int Em(double n) { return (int)Math.Round(n * Font.Height); }

        void ShowInfo(UpdateInfo i)
        {
            info = i;
            Text = "새 버전 " + i.VersionText;
            lblHead.Text = "지금 " + Updater.Text(Updater.Current) + "  →  새 버전 " + i.VersionText
                + (i.Date.Length > 0 ? " · " + i.Date : "") + " · 약 " + (i.Size / 1e6).ToString("0.0") + "MB";
        }

        void Start()
        {
            if (!prepare())
            {
                lblInfo.ForeColor = Color.Firebrick;
                lblInfo.Text = "연결을 해제한 뒤 [지금 업데이트]를 다시 누르세요. 업로드가 남아 있으면 끝난 뒤에 해제됩니다.";
                return;
            }
            busy = true;
            btnGo.Enabled = btnLater.Enabled = false;
            lblInfo.ForeColor = SystemColors.GrayText;
            lblInfo.Text = "최신 정보를 확인하는 중...";
            // 받기 직전에 한 번 더 확인해 그사이 더 새 버전이 올라왔으면 그것을 받는다. 확인하지 못하면 찾아 둔 것을 받는다.
            Updater.CheckAsync((newer, error) =>
            {
                if (newer != null && newer.Version > info.Version) ShowInfo(newer);
                bar.Value = 0;
                bar.Visible = true;
                lblInfo.Text = "내려받는 중... 0%";
                Updater.DownloadAsync(info, pct =>
                {
                    bar.Value = pct;
                    lblInfo.Text = "내려받는 중... " + pct + "%";
                }, (path, err) =>
                {
                    busy = false;
                    if (err != null)
                    {
                        bar.Visible = false;
                        btnGo.Enabled = btnLater.Enabled = true;
                        lblInfo.ForeColor = Color.Firebrick;
                        lblInfo.Text = err;
                        return;
                    }
                    SetupPath = path;
                    lblInfo.Text = "다 받았습니다. 프로그램을 닫고 업데이트합니다...";
                    DialogResult = DialogResult.OK;
                });
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) e.Cancel = true; // 내려받는 동안에는 닫지 않는다.
            base.OnFormClosing(e);
        }
    }
}
