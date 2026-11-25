// ============================================================
//  TelegramNotifier - Monitor báo cáo ra group Telegram.
//
//  Sau MỖI vòng gọi API danh sách phone nguồn + update VOS3000
//  (PrefixApiSyncer.RunOnce) sẽ gửi 1 tin nhắn vào group:
//    - trạng thái (OK / LỖI)
//    - danh sách số đã xử lý (khóa/mở) + số chưa có trên VOS
//    - thời gian hoàn thành + thời lượng
//
//  Nguyên tắc an toàn:
//    - Gửi bất đồng bộ (không chặn / không làm hỏng vòng sync).
//    - Mọi lỗi mạng / token sai đều bị bỏ qua (chỉ trả chuỗi lỗi về log).
//    - Chỉ gửi, KHÔNG nhận lệnh từ Telegram.
// ============================================================
using System.Net.Http;
using System.Text;

namespace Vos3000Agent
{
    public static class TelegramNotifier
    {
        /// <summary>Telegram giới hạn 4096 ký tự/tin — chừa biên an toàn.</summary>
        private const int ChunkChars = 3600;
        private const int NumbersPerLine = 12;
        private const string ApiBase = "https://api.telegram.org";

        /// <summary>Đã bật và đủ token + chat id chưa.</summary>
        public static bool Enabled(Settings s) =>
            s != null && s.telegram_enabled
            && !string.IsNullOrWhiteSpace(s.telegram_bot_token)
            && !string.IsNullOrWhiteSpace(s.telegram_chat_id);

        // ==================== gửi tin ====================

        /// <summary>Gửi 1 tin HTML (tự chia nhiều tin nếu dài). Trả null nếu OK, ngược lại là thông báo lỗi.</summary>
        public static string SendHtml(Settings s, IEnumerable<string> lines, Action<string> log = null)
        {
            if (s == null) return "Thiếu cấu hình";
            if (string.IsNullOrWhiteSpace(s.telegram_bot_token)) return "Chưa có bot token";
            if (string.IsNullOrWhiteSpace(s.telegram_chat_id)) return "Chưa có chat id";

            string firstError = null;
            int sent = 0;
            foreach (var chunk in Chunk(lines))
            {
                var err = SendOne(s, chunk);
                if (err != null)
                {
                    log?.Invoke($"  !! Telegram: {err}");
                    firstError ??= err;
                }
                else sent++;
            }
            if (sent > 0 && firstError == null) log?.Invoke($"  Telegram: đã gửi {sent} tin.");
            return firstError;
        }

        public static string SendHtml(Settings s, string text, Action<string> log = null) =>
            SendHtml(s, new[] { text ?? "" }, log);

        /// <summary>Gửi 1 tin (không chia). Trả null nếu OK.</summary>
        private static string SendOne(Settings s, string html)
        {
            try
            {
                var token = (s.telegram_bot_token ?? "").Trim();
                var url = $"{ApiBase}/bot{token}/sendMessage";

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        s.verify_tls ? null : (sender, cert, chain, errors) => true
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = (s.telegram_chat_id ?? "").Trim(),
                    ["text"] = html,
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = "true"
                });
                var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (resp.IsSuccessStatusCode) return null;

                // đọc "description" của Telegram cho dễ hiểu
                string desc = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("description", out var d)) desc = d.GetString();
                }
                catch { }
                return $"HTTP {(int)resp.StatusCode} {desc ?? PrefixApiSyncer.Trunc(body, 200)}";
            }
            catch (Exception e)
            {
                return e is TaskCanceledException ? "timeout 20s" : e.Message;
            }
        }

        /// <summary>Chia danh sách dòng thành nhiều tin, không cắt giữa dòng.</summary>
        private static IEnumerable<string> Chunk(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = raw ?? "";
                // dòng đơn lẻ quá dài (hiếm) -> cắt cứng
                while (line.Length > ChunkChars)
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    yield return line.Substring(0, ChunkChars);
                    line = line.Substring(ChunkChars);
                }
                if (sb.Length + line.Length + 1 > ChunkChars && sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                sb.AppendLine(line);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        // ==================== báo cáo sync ====================

        /// <summary>Có thay đổi gì trên VOS3000 trong vòng sync này không.</summary>
        public static bool HasChange(PrefixSyncResult r) =>
            r != null && (r.Added > 0 || r.AccountsLocked > 0 || r.AccountsOpened > 0
                          || r.AccountsChanged > 0 || r.MissingOnServer > 0 || r.MissingRemoved > 0);

        /// <summary>Báo kết quả gửi về UI: (ok, lỗi) — null lỗi = gửi thành công.</summary>
        public static event Action<bool, string> ReportDone;

        /// <summary>
        /// Bắn báo cáo 1 vòng sync (gọi trong PrefixApiSyncer, bất đồng bộ).
        /// Không bao giờ "im lặng": mọi trường hợp bỏ qua đều ghi log rõ lý do.
        /// <paramref name="force"/> = chạy tay → gửi dù không có thay đổi.
        /// </summary>
        public static void NotifySyncResult(Settings s, PrefixSyncResult res, Action<string> log = null, bool force = false)
        {
            if (res == null) return;

            if (!Enabled(s))
            {
                log?.Invoke("  Telegram: chưa bật / thiếu bot token hoặc chat id — không gửi báo cáo.");
                return;
            }

            // Có lỗi (hoặc chạy tay) thì LUÔN bắn, kể cả khi bật "chỉ bắn khi có thay đổi"
            if (!force && s.telegram_only_on_change && res.Ok && !HasChange(res))
            {
                log?.Invoke("  Telegram: bỏ qua — vòng này không có số nào đổi " +
                            "(đang bật 'Chỉ bắn khi có số thay đổi'; bỏ tick để nhận mọi vòng).");
                ReportDone?.Invoke(false, "skip-no-change");
                return;
            }

            var lines = BuildSyncMessage(s, res);
            log?.Invoke($"  Telegram: đang gửi báo cáo ({res.Fetched} số API, " +
                        $"{res.ProcessedNumbers?.Count ?? 0} số xử lý)…");

            _ = Task.Run(() =>
            {
                try
                {
                    var err = SendHtml(s, lines, log);
                    ReportDone?.Invoke(err == null, err);
                }
                catch (Exception e)
                {
                    log?.Invoke("  !! Telegram: " + e.Message);
                    ReportDone?.Invoke(false, e.Message);
                }
            });
        }

        /// <summary>Dựng nội dung tin báo cáo (HTML Telegram).</summary>
        public static List<string> BuildSyncMessage(Settings s, PrefixSyncResult res)
        {
            var lines = new List<string>();
            string icon = res.Ok ? "✅" : "❌";
            string status = res.Ok ? "THÀNH CÔNG" : "LỖI";
            int minutes = (int)(res.Time - res.StartedAt).TotalMinutes;

            lines.Add($"<b>{icon} ĐỒNG BỘ SỐ NGUỒN → VOS3000: {status}</b>");
            lines.Add($"🕒 Hoàn thành: <b>{res.Time:dd/MM/yyyy HH:mm:ss}</b> (mất {FormatDuration(minutes, res.Time - res.StartedAt)})");
            if (!string.IsNullOrWhiteSpace(s?.prefix_api_url))
                lines.Add($"🌐 Nguồn: <code>{E(s.prefix_api_url)}</code>");
            lines.Add("");

            lines.Add($"📥 API trả về: <b>{res.Fetched}</b> số");
            lines.Add($"🔁 Đã xử lý trên VOS: <b>{res.ProcessedNumbers?.Count ?? 0}</b> số"
                      + $" — 🔒 khóa {res.AccountsLocked}, 🔓 mở {res.AccountsOpened}");
            if (res.Added > 0) lines.Add($"➕ Số mới thêm: <b>{res.Added}</b>");
            lines.Add($"🏢 Account cập nhật: <b>{res.AccountsChanged}/{res.AccountsTotal}</b>"
                      + $" (out {res.OutMappingsUpdated}/{res.OutMappingsTotal}, in {res.InRoutingsUpdated}/{res.InRoutingsTotal})");
            lines.Add($"⚠️ Chưa có trên VOS (bỏ qua): <b>{res.MissingOnServer}</b> số");
            if (res.MissingRemoved > 0) lines.Add($"🧹 Đã xoá khỏi bảng 'chưa có': {res.MissingRemoved} số");

            if (!string.IsNullOrWhiteSpace(res.Message))
            {
                lines.Add("");
                lines.Add($"📝 <i>{E(PrefixApiSyncer.Trunc(res.Message, 400))}</i>");
            }

            if (s == null || s.telegram_include_numbers)
            {
                AppendNumberList(lines, "📋 <b>Danh sách số đã xử lý</b>", res.ProcessedNumbers,
                    s?.telegram_max_numbers ?? 200);
                AppendNumberList(lines, "⚠️ <b>Số chưa có trên VOS</b>", res.MissingNumbers,
                    s?.telegram_max_numbers ?? 200);
            }
            return lines;
        }

        /// <summary>Ghi danh sách số thành các dòng ngắn (mỗi dòng 12 số), có giới hạn số lượng.</summary>
        private static void AppendNumberList(List<string> lines, string title, List<string> nums, int max)
        {
            if (nums == null || nums.Count == 0) return;
            int limit = Math.Clamp(max <= 0 ? 200 : max, 1, 2000);
            lines.Add("");
            lines.Add($"{title} ({nums.Count}):");
            var shown = nums.Take(limit).ToList();
            for (int i = 0; i < shown.Count; i += NumbersPerLine)
            {
                var part = shown.Skip(i).Take(NumbersPerLine);
                lines.Add("<code>" + E(string.Join(", ", part)) + "</code>");
            }
            if (nums.Count > limit)
                lines.Add($"<i>… và {nums.Count - limit} số nữa (giới hạn {limit} — tăng ở Cấu hình)</i>");
        }

        private static string FormatDuration(int minutes, TimeSpan d) =>
            d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.0}s" : $"{minutes}p {d.Seconds}s";

        /// <summary>Escape ký tự HTML cho parse_mode=HTML.</summary>
        public static string E(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Tin nhắn kiểm tra cấu hình (nút "Gửi thử").</summary>
        public static List<string> BuildTestMessage(Settings s)
        {
            var lines = new List<string>
            {
                "<b>🔔 TEST THÔNG BÁO TELEGRAM</b>",
                $"🕒 {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                "Kênh monitor VOS3000 Agent đã sẵn sàng.",
                $"• Chỉ bắn khi có thay đổi: {(s.telegram_only_on_change ? "Có" : "Không")}",
                $"• Kèm danh sách số: {(s.telegram_include_numbers ? "Có" : "Không")} (tối đa {(s.telegram_max_numbers <= 0 ? 200 : s.telegram_max_numbers)} số)"
            };
            return lines;
        }
    }
}
