// ============================================================
//  Bootstrap - Chuẩn bị môi trường chạy ở lần khởi động đầu tiên.
//
//  Thiết kế cho bản phát hành 1 file .exe (self-contained):
//    - Máy đích KHÔNG cần cài .NET, KHÔNG cần copy thư mục kèm theo.
//    - Lần chạy đầu: tạo %APPDATA%\Vos3000AgentCSharp, giải nén tài nguyên
//      nhúng (logo, icon, session mẫu), tạo settings.json mặc định,
//      kiểm tra kết nối tới VOS/DB, ghi log cài đặt.
//    - Các lần sau: chỉ kiểm tra nhanh (file đủ chưa), bỏ qua phần nặng.
// ============================================================
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Vos3000Agent
{
    public static class Bootstrap
    {
        /// Thư mục dữ liệu người dùng (ghi được, không cần quyền admin).
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vos3000AgentCSharp");

        public static string AssetsDir => Path.Combine(DataDir, FileKeys.AssetsDir);
        public static string LogPath => Path.Combine(DataDir, FileKeys.BootstrapLog);

        /// Kết quả kiểm tra lần chạy đầu.
        public class Result
        {
            public bool FirstRun;
            public bool RestoredResources;
            public List<string> Notes = new();
            public Exception Error;
            public bool Ok => Error == null;
        }

        /// Chạy trước khi mở form. Không ném exception ra ngoài.
        public static Result Prepare()
        {
            var r = new Result();
            try
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(AssetsDir);

                r.FirstRun = !File.Exists(Path.Combine(DataDir, FileKeys.InstalledStamp));

                // ---- 1) Giải nén tài nguyên nhúng (logo, icon, session mẫu) ----
                r.RestoredResources = ExtractResource(Resources.Logo, Path.Combine(AssetsDir, FileKeys.LogoFile));
                ExtractResource(Resources.Icon, Path.Combine(AssetsDir, FileKeys.IconFile));
                ExtractResource(Resources.SessionCapture, Path.Combine(DataDir, FileKeys.SessionFile));
                ExtractResource(Resources.RoutingCapture, Path.Combine(DataDir, "vos_routing_c2s.bin"));
                if (r.RestoredResources) r.Notes.Add(Msg.Bootstrap.NoteResources);

                // ---- 2) Cấu hình mặc định (chỉ tạo nếu chưa có — KHÔNG ghi đè) ----
                var settingsPath = Path.Combine(DataDir, FileKeys.Settings);
                if (!File.Exists(settingsPath))
                {
                    var s = new Settings();
                    try { s.Normalize(); } catch { }
                    File.WriteAllText(settingsPath, JsonSerializer.Serialize(s, Config.JsonOpts), new UTF8Encoding(false));
                    r.Notes.Add(Msg.Bootstrap.NoteDefaultConfig);
                }

                // ---- 3) Kiểm tra quyền ghi ----
                var probe = Path.Combine(DataDir, FileKeys.WriteProbe);
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                // ---- 4) Đánh dấu đã cài ----
                var stamp = Path.Combine(DataDir, FileKeys.InstalledStamp);
                if (!File.Exists(stamp))
                    File.WriteAllText(stamp,
                        $"installed_at={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"version={Assembly.GetExecutingAssembly().GetName().Version}\n" +
                        $"machine={Environment.MachineName}\n" +
                        $"user={Environment.UserName}\n" +
                        $"os={Environment.OSVersion}\n" +
                        $"runtime={Environment.Version}\n");

                if (r.FirstRun) r.Notes.Add(Msg.Bootstrap.NoteFirstRun);
                Log(r.FirstRun ? Msg.Bootstrap.LogFirstRun : Msg.Bootstrap.LogRun, r.Notes);
            }
            catch (Exception e)
            {
                r.Error = e;
                try { Log(string.Format(Msg.Bootstrap.LogError, e), null); } catch { }
            }
            return r;
        }

        /// Ghi 1 tài nguyên nhúng ra file (bỏ qua nếu file đã có cùng nội dung).
        private static bool ExtractResource(string logicalName, string targetPath)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var src = asm.GetManifestResourceStream(logicalName);
                if (src == null) return false;   // bản build không nhúng (bản Debug thường)

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length == src.Length)
                    return false;                // đã có, cùng kích thước → giữ nguyên

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
                using var dst = File.Create(targetPath);
                src.CopyTo(dst);
                return true;
            }
            catch { return false; }
        }

        private static void Log(string header, List<string> notes)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {header}");
                sb.AppendLine($"  exe      : {Environment.ProcessPath}");
                sb.AppendLine($"  datadir  : {DataDir}");
                sb.AppendLine($"  runtime  : {Environment.Version} / {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                sb.AppendLine($"  os       : {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
                foreach (var n in notes ?? new List<string>()) sb.AppendLine("  - " + n);
                File.AppendAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// Đường dẫn file session VOS — ưu tiên bản trong %APPDATA%, fallback cạnh exe, fallback source.
        public static string ResolveSessionFile(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            var inData = Path.Combine(DataDir, FileKeys.SessionFile);
            if (File.Exists(inData)) return inData;

            var beside = Path.Combine(AppContext.BaseDirectory, FileKeys.DataDir, FileKeys.SessionFile);
            if (File.Exists(beside)) return beside;

            return configured;
        }

        /// <summary>
        /// Session da doi tai khoan login - tach rieng NGoAI %APPDATA% de bootstrap khong ghi de.
        /// </summary>
        public static string LoginSessionPath =>
            Path.Combine(Path.GetTempPath(), "Vos3000AgentCSharp", FileKeys.LoginSessionFile);

        /// <summary>
        /// Session th?c s? d�ng: n?u da c?u h�nh user (t�i kho?n kh�c template) th� uu ti�n
        /// b?n session da d?i login; chua c� th� tr? v? session g?c.
        /// </summary>
        public static string ResolveSessionFile(Settings s)
        {
            if (s == null) return ResolveSessionFile((string)null);

            var loginFile = s.vos_session_login_file;
            if (!string.IsNullOrWhiteSpace(loginFile) && File.Exists(loginFile)) return loginFile;

            return ResolveSessionFile(s.vos_session_file);
        }

        /// Đường dẫn logo: %APPDATA% trước, rồi tới thư mục Assets cạnh exe.
        public static string ResolveLogo()
        {
            var inData = Path.Combine(AssetsDir, FileKeys.LogoFile);
            if (File.Exists(inData)) return inData;
            var beside = Path.Combine(AppContext.BaseDirectory, FileKeys.AssetsDir, FileKeys.LogoFile);
            return File.Exists(beside) ? beside : null;
        }

        public static string ResolveIcon()
        {
            var inData = Path.Combine(AssetsDir, FileKeys.IconFile);
            if (File.Exists(inData)) return inData;
            var beside = Path.Combine(AppContext.BaseDirectory, FileKeys.AssetsDir, FileKeys.IconFile);
            return File.Exists(beside) ? beside : null;
        }
    }
}
