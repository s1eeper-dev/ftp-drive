// FtpDrive 의 설정 저장·불러오기와 업로드 상황 해석을 확인하는 콘솔 테스트 (build.ps1 이 빌드 전에 실행).
using System;
using System.IO;
using System.Text;

namespace FtpDrive
{
    static class Tests
    {
        static int failed;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failed++;
        }

        static string TempFile(params string[] lines)
        {
            string path = Path.Combine(Path.GetTempPath(), "ftpdrive-test-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return path;
        }

        static int Main()
        {
            // 1.0.0 형식(서버 하나, 표시 이름 기본값)은 서버 주소를 별칭으로 옮긴다.
            var s = Settings.Load(TempFile("Protocol=sftp", "Host=10.0.0.5", "Port=2222", "User=kim", "RemotePath=/data", "Drive=Y", "Label=FTP", "OpenAfterConnect=0"));
            Check(s.Profiles.Count == 1, "1.0.0 설정이 연결 하나로 옮겨진다");
            var p = s.Profiles[0];
            Check(p.Alias == "10.0.0.5" && p.Protocol == "sftp" && p.Port == 2222 && p.User == "kim" && p.RemotePath == "/data" && p.Drive == "Y",
                "1.0.0 설정 값이 그대로 옮겨진다");
            Check(!s.OpenAfterConnect, "1.0.0 의 탐색기 열기 설정이 유지된다");

            // 1.0.0 에서 표시 이름을 바꿨다면 그것을 별칭으로 쓴다.
            s = Settings.Load(TempFile("Host=nas.local", "User=kim", "Label=회사 NAS"));
            Check(s.Profiles.Count == 1 && s.Profiles[0].Alias == "회사 NAS", "1.0.0 표시 이름이 별칭이 된다");

            // 저장 후 다시 읽으면 순서와 값이 같다.
            var orig = new Settings { OpenAfterConnect = false };
            orig.Profiles.Add(new Profile { Alias = "회사 NAS", Protocol = "ftps-explicit", Host = "nas.local", Port = 21, User = "kim", RemotePath = "", Drive = "X" });
            orig.Profiles.Add(new Profile { Alias = "집", Protocol = "ftp", Host = "1.2.3.4", Port = 2121, User = "me", RemotePath = "/공유", Drive = "Z" });
            string path = TempFile();
            orig.Save(path);
            s = Settings.Load(path);
            Check(s.Profiles.Count == 2 && s.Profiles[0].Alias == "회사 NAS" && s.Profiles[1].Alias == "집", "저장 후 순서가 유지된다");
            Check(s.Profiles[0].Protocol == "ftps-explicit" && s.Profiles[0].Drive == "X" && s.Profiles[1].Port == 2121 && s.Profiles[1].RemotePath == "/공유",
                "저장 후 값이 유지된다");
            Check(!s.OpenAfterConnect, "저장 후 탐색기 열기 설정이 유지된다");
            Check(s.Find("회사 nas") == s.Profiles[0], "별칭 찾기는 대소문자를 가리지 않는다");

            // 같은 별칭 구역이 두 번 있으면 첫 번째만 쓴다.
            s = Settings.Load(TempFile("[Profile:A]", "Host=h1", "[Profile:A]", "Host=h2"));
            Check(s.Profiles.Count == 1 && s.Profiles[0].Host == "h1", "중복 별칭은 첫 번째만 남는다");

            Check(Profile.CleanAlias(" [회사]/NAS:*?\"<>|\\ ") == "회사NAS", "별칭에서 쓸 수 없는 문자를 뺀다");
            Check(Profile.CleanAlias(new string('가', 50)).Length == 40, "별칭은 40자까지만 쓴다");

            // rclone 원격 제어 응답에서 업로드 상황을 읽는다.
            const string vfsBusy = "{\"diskCache\":{\"bytesUsed\":1048576,\"erroredFiles\":0,\"files\":3,\"outOfSpace\":false,\"uploadsInProgress\":1,\"uploadsQueued\":2},\"fs\":\":ftp:\",\"inUse\":1}";
            const string vfsIdle = "{\"diskCache\":{\"uploadsInProgress\":0,\"uploadsQueued\":0},\"inUse\":1}";
            const string coreStats = "{\"bytes\":512,\"transfers\":0,\"transferring\":[{\"bytes\":512,\"name\":\"사진/2026/IMG_0001.jpg\",\"percentage\":45,\"size\":1024,\"speed\":100}]}";
            var u = UploadInfo.Parse(vfsBusy, coreStats);
            Check(u != null && u.Pending == 3, "올리는 중 1개 + 대기 2개 = 남은 파일 3개");
            Check(u != null && u.Name == "IMG_0001.jpg" && u.Percent == 45, "지금 올리는 파일 이름(폴더 제외)과 진행률을 읽는다");
            u = UploadInfo.Parse(vfsBusy, "{\"bytes\":0}");
            Check(u != null && u.Pending == 3 && u.Name == null, "대기만 있고 전송 목록이 없을 때");
            u = UploadInfo.Parse(vfsIdle, null);
            Check(u != null && u.Pending == 0, "업로드가 없으면 0개");
            Check(UploadInfo.Parse("not json", null) == null, "JSON 이 아니면 null");

            // GitHub 릴리스 목록에서 새 버전을 고른다.
            const string releases = "[" +
                "{\"tag_name\":\"v1.4.0\",\"draft\":true,\"prerelease\":false,\"published_at\":\"2026-10-20T00:00:00Z\",\"body\":\"초안\",\"assets\":[{\"name\":\"FtpDrive-Setup.exe\",\"size\":9,\"browser_download_url\":\"https://x/d\"}]}," +
                "{\"tag_name\":\"v1.3.1\",\"draft\":false,\"prerelease\":true,\"published_at\":\"2026-10-19T00:00:00Z\",\"body\":\"시험판\",\"assets\":[{\"name\":\"FtpDrive-Setup.exe\",\"size\":9,\"browser_download_url\":\"https://x/p\"}]}," +
                "{\"tag_name\":\"v1.3.0\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-18T09:00:00Z\",\"body\":\"**새 기능**\\r\\n- 복수 연결\",\"assets\":[" +
                    "{\"name\":\"other.zip\",\"size\":1,\"browser_download_url\":\"https://x/o\"}," +
                    "{\"name\":\"FtpDrive-Setup.exe\",\"size\":2112859,\"digest\":\"sha256:ABCDEF\",\"browser_download_url\":\"https://x/130\"}]}," +
                "{\"tag_name\":\"v1.2.5\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-16T09:00:00Z\",\"body\":\"- 설치 파일 없는 릴리스\",\"assets\":[]}," +
                "{\"tag_name\":\"v1.2.1\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-15T09:00:00Z\",\"body\":\"- `오류` 수정\",\"assets\":[{\"name\":\"FtpDrive-Setup.exe\",\"size\":5,\"browser_download_url\":\"https://x/121\"}]}," +
                "{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-14T09:00:00Z\",\"body\":\"- 지금 버전\",\"assets\":[{\"name\":\"FtpDrive-Setup.exe\",\"size\":5,\"browser_download_url\":\"https://x/120\"}]}" +
                "]";
            var up = Updater.Parse(releases, new Version(1, 2, 0, 0));
            Check(up != null && up.VersionText == "1.3.0", "초안·시험판은 빼고 가장 새 정식 버전을 고른다");
            Check(up != null && up.Url == "https://x/130" && up.Size == 2112859 && up.Sha256 == "abcdef" && up.Date == "2026-10-18",
                "설치 파일 주소·크기·SHA-256·날짜를 읽는다");
            Check(Updater.Parse(releases, new Version(1, 3, 0)) == null, "이미 최신이면 null (1.3 과 1.3.0.0 을 같게 본다)");
            Check(Updater.Parse("[]", new Version(1, 0, 0)) == null, "릴리스가 없으면 null");
            bool threw = false;
            try { Updater.Parse("{\"message\":\"API rate limit exceeded\"}", new Version(1, 0, 0)); }
            catch (FormatException) { threw = true; }
            Check(threw, "목록이 아닌 응답(요청 한도 초과 등)은 오류로 알린다");

            string file = TempFile();
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
            var ok = new UpdateInfo { Size = 3, Sha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" };
            Check(Updater.Verify(file, ok), "크기와 SHA-256 이 맞으면 통과");
            ok.Sha256 = "00" + ok.Sha256.Substring(2);
            Check(!Updater.Verify(file, ok), "SHA-256 이 다르면 거부");
            Check(!Updater.Verify(file, new UpdateInfo { Size = 4 }), "크기가 다르면 거부");

            Console.WriteLine(failed == 0 ? "모두 통과" : failed + "개 실패");
            return failed == 0 ? 0 : 1;
        }
    }
}
