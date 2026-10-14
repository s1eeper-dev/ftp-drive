// FtpDrive 의 설정 저장·불러오기 로직을 확인하는 콘솔 테스트 (build.ps1 -Test 로 실행).
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

            Console.WriteLine(failed == 0 ? "모두 통과" : failed + "개 실패");
            return failed == 0 ? 0 : 1;
        }
    }
}
