using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vos3000Agent
{
    /// <summary>
    /// Quản lý cấu hình: đọc/ghi settings.json và gui_profiles.json.
    /// Dùng thư mục riêng %APPDATA%\Vos3000AgentCSharp (không đụng dữ liệu app Python cũ).
    /// Cấu trúc JSON tương thích với app gốc.
    /// </summary>
    public static class Config
    {
        public const string AppName = "Vos3000AgentCSharp";

        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);

        public static string SettingsPath => Path.Combine(DataDir, FileKeys.Settings);
        public static string ProfilesPath => Path.Combine(DataDir, FileKeys.Profiles);
        public static string LogPath => Path.Combine(DataDir, FileKeys.AgentLog);
        public static string HistoryPath => Path.Combine(DataDir, FileKeys.History);

        public static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); }
            catch { /* không chặn khởi động */ }
        }

        // ---------- Settings ----------

        public static Settings LoadSettings()
        {
            EnsureDataDir();
            Settings s = ReadJson<Settings>(SettingsPath) ?? new Settings();
            s.Normalize();
            return s;
        }

        public static void SaveSettings(Settings s)
        {
            EnsureDataDir();
            s.Normalize();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, JsonOpts), Encoding.UTF8);
        }

        // ---------- GUI Profiles ----------

        public static Dictionary<string, GuiProfile> LoadProfiles()
        {
            EnsureDataDir();
            var p = ReadJson<Dictionary<string, GuiProfile>>(ProfilesPath);
            if (p == null || p.Count == 0)
                p = DefaultProfiles();
            return p;
        }

        public static void EnsureProfiles()
        {
            EnsureDataDir();
            if (!File.Exists(ProfilesPath))
            {
                File.WriteAllText(ProfilesPath,
                    JsonSerializer.Serialize(DefaultProfiles(), JsonOpts), Encoding.UTF8);
            }
        }

        public static Dictionary<string, GuiProfile> DefaultProfiles() => new()
        {
            [JobType.AddNumberSection] = new GuiProfile
            {
                WindowTitleRegex = "VOS3000",
                Backend = "uia",
                Steps = new List<GuiStep>
                {
                    new GuiStep { Action = "click",    Selector = new GuiSelector { Title = "Number management" } },
                    new GuiStep { Action = "set_text", Selector = new GuiSelector { AutoId = "EDIT_BEGIN" }, Value = "$job.number_start" },
                    new GuiStep { Action = "set_text", Selector = new GuiSelector { AutoId = "EDIT_END" },   Value = "$job.number_end" },
                    new GuiStep { Action = "set_text", Selector = new GuiSelector { AutoId = "EDIT_ACCOUNT" }, Value = "$job.account_id" },
                    new GuiStep { Action = "click",    Selector = new GuiSelector { Title = "Add" } },
                    new GuiStep { Action = "click",    Selector = new GuiSelector { Title = "Apply" } }
                },
                VerifySteps = new List<GuiStep>
                {
                    new GuiStep { Assert = "text_contains", Selector = new GuiSelector { AutoId = "STATUS_BAR" }, Value = "success", Timeout = 3 }
                }
            },
            [JobType.LockNumber] = new GuiProfile { WindowTitleRegex = "VOS3000", Backend = "uia", Steps = new(), VerifySteps = new() },
            [JobType.OpenLimit] = new GuiProfile { WindowTitleRegex = "VOS3000", Backend = "uia", Steps = new(), VerifySteps = new() },
            [JobType.LockLimit] = new GuiProfile { WindowTitleRegex = "VOS3000", Backend = "uia", Steps = new(), VerifySteps = new() }
        };

        // ---------- Helpers ----------

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOpts);
            }
            catch { return null; }
        }

        public static void AppendHistory(string line)
        {
            try { File.AppendAllText(HistoryPath, line + "\n", Encoding.UTF8); }
            catch { /* bỏ qua lỗi ghi log */ }
        }
    }

    public class Settings
    {
        public string api_base_url { get; set; } = "";
        public string agent_id { get; set; } = "";
        public string agent_token { get; set; } = "";
        public int poll_interval_seconds { get; set; } = 5;
        public bool verify_tls { get; set; } = true;
        public bool auto_poll { get; set; } = false;
        public bool simulation_mode { get; set; } = true;
        public bool live_gui_enabled { get; set; } = false;
        public ApiPaths api_paths { get; set; } = new ApiPaths();

        // ---------- Healthcheck API nguồn ----------
        public string health_domain { get; set; } = "";
        public string health_endpoint { get; set; } = "/health";
        public int health_interval_seconds { get; set; } = 5;
        public bool health_auto_start { get; set; } = true;
        public int health_timeout_seconds { get; set; } = 5;

        // ---------- Mapping Prefix (VOS3000 TCP 1202) ----------
        public string vos_server { get; set; } = "172.29.101.32";
        public int vos_port { get; set; } = 1202;
        public string vos_session_file { get; set; } = "";   // mặc định: <app>\Data\vos_session_capture.bin

        // Tài khoản đăng nhập VOS3000 (TCP 1202) dùng cho session file ở trên.
        // Rỗng = không sửa gì, dùng nguyên user/pass nằm sẵn trong template.
        public string vos_user { get; set; } = "";
        public string vos_pass { get; set; } = "";
        // Session đã đổi user/pass (tách riêng, để bootstrap không ghi đè bản gốc)
        public string vos_session_login_file { get; set; } = "";

        // ---------- API danh sách số (sync prefix) ----------
        public string prefix_api_url { get; set; } = "";
        public string prefix_api_key { get; set; } = "";      // gửi qua header "api-key"
        public string prefix_api_field { get; set; } = ApiField.Phone;  // path JSON tới mảng số
        public int prefix_interval_seconds { get; set; } = 60;
        public bool prefix_auto_start { get; set; } = false;

        // Dọn số rác (không phải SĐT hợp lệ) khỏi danh sách trên server, ví dụ:
        // "08445685324" (đầu 0), "842999999920", "84900000001". Mặc định TẮT để an toàn.
        public bool prefix_remove_invalid { get; set; } = false;

        // ---------- Monitor Telegram (báo cáo sau mỗi vòng đồng bộ) ----------
        public bool telegram_enabled { get; set; } = false;
        public string telegram_bot_token { get; set; } = "";       // token bot (BotFather)
        public string telegram_chat_id { get; set; } = "";         // id group, dạng -1001234567890
        public bool telegram_include_numbers { get; set; } = true;  // kèm danh sách số đã xử lý
        public int telegram_max_numbers { get; set; } = 200;        // tối đa số liệt kê cho mỗi danh sách
        public bool telegram_only_on_change { get; set; } = false;  // chỉ bắn khi có thay đổi

        // ---------- DB của tool (MySQL) — log/nghiệp vụ/thống kê ----------
        public string db_host { get; set; } = "172.29.101.32";
        public int db_port { get; set; } = 3306;
        public string db_name { get; set; } = "vos_tool";
        public string db_user { get; set; } = "toolEtl";
        public string db_pass { get; set; } = "Cts@12345";

        // ---------- DB của VOS3000 (cùng server MySQL, khác database) ----------
        // Dùng cho tab "Customer (VOS)": đọc/sửa bảng e_customer. Không đụng DB của tool.
        public string vos_db_name { get; set; } = "vos3000db";

        public void Normalize()
        {
            api_paths ??= new ApiPaths();
            api_paths.Normalize();
            if (poll_interval_seconds < 3) poll_interval_seconds = 3;
            if (poll_interval_seconds > 300) poll_interval_seconds = 300;
            api_base_url = (api_base_url ?? "").Trim().TrimEnd('/');

            health_domain = (health_domain ?? "").Trim().TrimEnd('/');
            health_endpoint = (health_endpoint ?? "").Trim();
            if (health_endpoint.Length > 0 && !health_endpoint.StartsWith("/")) health_endpoint = "/" + health_endpoint;
            if (health_interval_seconds < 1) health_interval_seconds = 5;
            if (health_interval_seconds > 3600) health_interval_seconds = 3600;
            if (health_timeout_seconds < 1) health_timeout_seconds = 5;
            if (health_timeout_seconds > 120) health_timeout_seconds = 120;

            vos_server = (vos_server ?? "").Trim();
            if (string.IsNullOrEmpty(vos_server)) vos_server = "172.29.101.32";
            if (vos_port < 1 || vos_port > 65535) vos_port = 1202;
            vos_session_file = (vos_session_file ?? "").Trim();
            vos_user = (vos_user ?? "").Trim();
            vos_pass = vos_pass ?? "";
            vos_session_login_file = (vos_session_login_file ?? "").Trim();

            prefix_api_url = (prefix_api_url ?? "").Trim().TrimEnd('/');
            prefix_api_key = (prefix_api_key ?? "").Trim();
            prefix_api_field = (prefix_api_field ?? "").Trim();
            if (string.IsNullOrEmpty(prefix_api_field)) prefix_api_field = ApiField.Phone;
            if (prefix_interval_seconds < 10) prefix_interval_seconds = 60;
            if (prefix_interval_seconds > 86400) prefix_interval_seconds = 86400;

            // Monitor Telegram
            telegram_bot_token = (telegram_bot_token ?? "").Trim();
            telegram_chat_id = (telegram_chat_id ?? "").Trim();
            if (telegram_max_numbers <= 0) telegram_max_numbers = 200;
            if (telegram_max_numbers > 2000) telegram_max_numbers = 2000;

            // DB của tool
            db_host = (db_host ?? "").Trim();
            if (string.IsNullOrEmpty(db_host)) db_host = "172.29.101.32";
            if (db_port < 1 || db_port > 65535) db_port = 3306;
            db_name = (db_name ?? "").Trim();
            if (string.IsNullOrEmpty(db_name)) db_name = "vos_tool";
            db_user = (db_user ?? "").Trim();
            if (string.IsNullOrEmpty(db_user)) db_user = "toolEtl";

            // DB của VOS3000 (bảng e_customer...)
            vos_db_name = (vos_db_name ?? "").Trim();
            if (string.IsNullOrEmpty(vos_db_name)) vos_db_name = "vos3000db";
        }
    }

    public class ApiPaths
    {
        public string next_job { get; set; } = "/api/v1/vos-agent/jobs/next";
        public string claim { get; set; } = "/api/v1/vos-agent/jobs/{job_id}/claim";
        public string result { get; set; } = "/api/v1/vos-agent/jobs/{job_id}/result";
        public string heartbeat { get; set; } = "/api/v1/vos-agent/heartbeat";

        public void Normalize()
        {
            next_job ??= "/api/v1/vos-agent/jobs/next";
            claim ??= "/api/v1/vos-agent/jobs/{job_id}/claim";
            result ??= "/api/v1/vos-agent/jobs/{job_id}/result";
            heartbeat ??= "/api/v1/vos-agent/heartbeat";
        }    }

    public class GuiProfile
    {
        [JsonPropertyName("window_title_regex")]
        public string window_title_regex { get; set; } = "VOS3000";

        [JsonPropertyName("backend")]
        public string backend { get; set; } = "uia";

        [JsonPropertyName("steps")]
        public List<GuiStep> steps { get; set; } = new();

        [JsonPropertyName("verify_steps")]
        public List<GuiStep> verify_steps { get; set; } = new();

        // Alias tiện dụng cho code C#
        [JsonIgnore] public string WindowTitleRegex { get => window_title_regex; set => window_title_regex = value; }
        [JsonIgnore] public string Backend { get => backend; set => backend = value; }
        [JsonIgnore] public List<GuiStep> Steps { get => steps; set => steps = value; }
        [JsonIgnore] public List<GuiStep> VerifySteps { get => verify_steps; set => verify_steps = value; }
    }

    public class GuiStep
    {
        [JsonPropertyName("action")]
        public string action { get; set; }

        [JsonPropertyName("selector")]
        public GuiSelector selector { get; set; }

        [JsonPropertyName("value")]
        public string value { get; set; }

        [JsonPropertyName("keys")]
        public string keys { get; set; }

        [JsonPropertyName("seconds")]
        public double seconds { get; set; } = 1;

        [JsonPropertyName("assert")]
        public string assert { get; set; }

        [JsonPropertyName("timeout")]
        public double timeout { get; set; } = 2;

        // Alias tiện dụng cho code C#
        [JsonIgnore] public string Action { get => action; set => action = value; }
        [JsonIgnore] public GuiSelector Selector { get => selector; set => selector = value; }
        [JsonIgnore] public string Value { get => value; set => this.value = value; }
        [JsonIgnore] public string Keys { get => keys; set => keys = value; }
        [JsonIgnore] public string Assert { get => assert; set => assert = value; }
        [JsonIgnore] public double Timeout { get => timeout; set => timeout = value; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (action != null) parts.Add(action);
            if (selector != null) parts.Add(JsonSerializer.Serialize(selector));
            if (value != null) parts.Add("value=" + value);
            if (keys != null) parts.Add("keys=" + keys);
            if (assert != null) parts.Add("assert=" + assert);
            return string.Join(" ", parts);
        }
    }

    public class GuiSelector
    {
        [JsonPropertyName("title")]
        public string title { get; set; }

        [JsonPropertyName("auto_id")]
        public string auto_id { get; set; }

        [JsonPropertyName("control_type")]
        public string control_type { get; set; }

        [JsonPropertyName("found_index")]
        public int? found_index { get; set; }

        // Alias tiện dụng cho code C#
        [JsonIgnore] public string Title { get => title; set => title = value; }
        [JsonIgnore] public string AutoId { get => auto_id; set => auto_id = value; }
        [JsonIgnore] public string ControlType { get => control_type; set => control_type = value; }
        [JsonIgnore] public int? FoundIndex { get => found_index; set => found_index = value; }

        public override string ToString()
            => JsonSerializer.Serialize(this);
    }
}
