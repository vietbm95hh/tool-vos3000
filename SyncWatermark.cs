// ============================================================
//  SyncWatermark - Mốc last_created_at của API nguồn danh sách số.
//
//  API trả "data.last_created_at" sau lần gọi đầu tiên. Kể từ lần thứ 2
//  luôn gọi kèm ?created_at=<mốc cũ> để server chỉ trả số MỚI.
//
//  Nơi lưu:
//    1) MySQL  — bảng t_config (key 'sync_watermark:<url-api>')  [mặc định]
//    2) File   — %APPDATA%\Vos3000AgentCSharp\sync_watermark.json
//                (chỉ dùng khi chưa cấu hình DB / DB lỗi)
//
//  Khóa gắn theo prefix_api_url để đổi nguồn thì không dùng nhầm mốc cũ.
// ============================================================
using System.Text.Json;

namespace Vos3000Agent
{
    public static class SyncWatermark
    {
        /// <summary>Khóa lưu mốc: theo URL API (bỏ query) để đổi nguồn là reset.</summary>
        public static string KeyOf(string apiUrl)
        {
            var u = (apiUrl ?? "").Trim();
            int q = u.IndexOf('?');
            if (q >= 0) u = u.Substring(0, q);
            return u.TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>Đọc mốc đã lưu; null = chưa có (phải gọi full, không kèm created_at).</summary>
        public static long? Read(Settings s) => Read(s, out _);

        /// <summary>Như Read, kèm tầng đã đọc được ("DB"/"file"/null).</summary>
        public static long? Read(Settings s, out string from)
        {
            from = null;
            var key = KeyOf(s?.prefix_api_url);
            if (string.IsNullOrEmpty(key)) return null;

            if (ToolDb.HasConfig())
            {
                var v = ToolDb.ReadSyncWatermark(key);
                if (v.HasValue) { from = "DB"; return v; }
            }

            var f = ReadFile(key);
            if (f.HasValue) from = "file";
            return f;
        }

        /// <summary>Ghi mốc; bỏ qua nếu giá trị mới không lớn hơn mốc hiện tại.</summary>
        public static void Write(Settings s, long value, Action<string> log = null)
        {
            if (value <= 0) return;
            var key = KeyOf(s?.prefix_api_url);
            if (string.IsNullOrEmpty(key)) return;

            var cur = Read(s);
            if (cur.HasValue && cur.Value >= value) return;

            var stores = new List<string>();
            if (ToolDb.HasConfig() && ToolDb.SaveSyncWatermark(key, value)) stores.Add("DB");
            if (WriteFile(key, value)) stores.Add("file");

            log?.Invoke(stores.Count > 0
                ? string.Format(Msg.Prefix.WatermarkSaved, value, string.Join(" + ", stores))
                : string.Format(Msg.Prefix.WatermarkSaveFail, value));
        }

        /// <summary>Xóa mốc của URL hiện tại (buộc lần sau gọi full).</summary>
        public static void Reset(Settings s)
        {
            var key = KeyOf(s?.prefix_api_url);
            if (string.IsNullOrEmpty(key)) return;
            if (ToolDb.HasConfig()) ToolDb.DeleteSyncWatermark(key);
            DeleteFile(key);
        }

        /// <summary>Dựng URL gọi API: có mốc → thêm ?created_at=&lt;mốc&gt;.</summary>
        public static string BuildUrl(string apiUrl, long? watermark)
        {
            var u = (apiUrl ?? "").Trim();
            if (string.IsNullOrEmpty(u) || !watermark.HasValue || watermark.Value <= 0) return u;
            var sep = u.Contains('?') ? (u.EndsWith("?") || u.EndsWith("&") ? "" : "&") : "?";
            return u + sep + "created_at=" + watermark.Value;
        }

        // ---------- File fallback ----------

        private static string FilePath() => Path.Combine(Config.DataDir, FileKeys.SyncWatermark);

        static long? ReadFile(string key)
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return null;
                var map = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(p));
                if (map != null && map.TryGetValue(key, out var v) && v > 0) return v;
            }
            catch { }
            return null;
        }

        static bool WriteFile(string key, long value)
        {
            try
            {
                Config.EnsureDataDir();
                var p = FilePath();
                Dictionary<string, long> map = null;
                if (File.Exists(p))
                {
                    try { map = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(p)); } catch { }
                }
                map ??= new Dictionary<string, long>();
                map[key] = value;
                File.WriteAllText(p, JsonSerializer.Serialize(map, Config.JsonOpts));
                return true;
            }
            catch { return false; }
        }

        static void DeleteFile(string key)
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return;
                var map = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(p));
                if (map == null || !map.Remove(key)) return;
                File.WriteAllText(p, JsonSerializer.Serialize(map, Config.JsonOpts));
            }
            catch { }
        }
    }
}
