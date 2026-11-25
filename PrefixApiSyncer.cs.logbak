// ============================================================
//  PrefixApiSyncer - Lay danh sach so tu API (header api-key) va
//  dong bo vao TAT CA account customer tren VOS3000:
//
//    OUT (call-out): calloutCallerPrefixes cua moi gateway mapping
//                    (ghi qua TCP 1202, object GET live cua chinh account)
//    IN  (call-in) : prefix cua e_gatewayrouting
//                    (ghi qua TCP 1202 truoc -> RAM VOS cap nhat ngay,
//                     sau do UPDATE MySQL de luu ben vung)
//
//  Che do: "Add only" - bao gio khong xoa so tren server.
//  Moi so API co ma account chua co -> ADD; trang thai khac -> khoa/mo.
//  Ket qua duoc tong hop theo account_id, tach 2 mang in/out.
// ============================================================
using System.Text;
using System.Text.Json;

namespace Vos3000Agent
{
    public class PrefixSyncResult
    {
        public DateTime StartedAt { get; set; } = DateTime.Now;   // lúc bắt đầu gọi API
        public DateTime Time { get; set; } = DateTime.Now;        // lúc hoàn thành (đặt ở finally)
        public bool Ok { get; set; }
        public int Fetched { get; set; }          // so luong dedupe tu API
        public int Added { get; set; }            // so luong da add thuc su
        public string FinalPrefix { get; set; }   // gia tri tren server sau khi chay
        public string Message { get; set; } = "";

        // ---- đồng bộ nhiều account (mapping) ----
        public int AccountsTotal { get; set; }     // số account (customer) có dữ liệu in/out
        public int AccountsChanged { get; set; }   // số account được ghi (có thay đổi)
        public int AccountsLocked { get; set; }    // số lần khóa số theo API
        public int AccountsOpened { get; set; }    // số lần mở số theo API
        public int AccountsFailed { get; set; }    // số account ghi lỗi

        public int OutMappingsTotal { get; set; }
        public int OutMappingsUpdated { get; set; }
        public int InRoutingsTotal { get; set; }
        public int InRoutingsUpdated { get; set; }

        /// <summary>Số API đã có trên VOS3000 (chỉ được update khóa/mở).</summary>
        public int ExistingOnServer { get; set; }
        /// <summary>Số API CHƯA có trên VOS3000 — chỉ lưu bảng t_number_missing, không add.</summary>
        public int MissingOnServer { get; set; }
        /// <summary>Số dòng cũ trong t_number_missing nay đã có trên server (đã xoá).</summary>
        public int MissingRemoved { get; set; }

        /// <summary>Số từ API đã có trên VOS3000 và đã được cập nhật khóa/mở trong vòng này.</summary>
        public List<string> ProcessedNumbers { get; set; } = new();
        /// <summary>Số từ API CHƯA có trên VOS3000 (chỉ lưu t_number_missing, không add).</summary>
        public List<string> MissingNumbers { get; set; } = new();

        /// <summary>account_id -> { account, in[], out[] } (số trên server sau sync).</summary>
        public Dictionary<long, AccountNumberGroup> ByAccount { get; set; } = new();
    }

    /// <summary>Số của 1 account, tách theo hướng in/out.</summary>
    public class AccountNumberGroup
    {
        public long AccountId { get; set; }
        public string Account { get; set; } = "";
        public List<string> In { get; set; } = new();
        public List<string> Out { get; set; } = new();

        public int InCount => In?.Count ?? 0;
        public int OutCount => Out?.Count ?? 0;

        public int InLocked => In?.Count(x => !NumberLockUtil.IsValid(x)) ?? 0;
        public int OutLocked => Out?.Count(x => !NumberLockUtil.IsValid(x)) ?? 0;

        public string ToJson() => JsonSerializer.Serialize(new
        {
            account = Account,
            @in = In,
            @out = Out,
            in_count = InCount,
            out_count = OutCount,
            in_locked = InLocked,
            out_locked = OutLocked
        });
    }

    /// <summary>Kết quả merge 1 danh sách số (1 account) theo lệnh API.</summary>
    internal class MergeOutcome
    {
        public string Head = "1;";
        public List<string> List = new();
        public int Added;
        public int Locked;
        public int Opened;
        public int RemovedDup;
        public bool Changed => Added > 0 || Locked > 0 || Opened > 0 || RemovedDup > 0;

        public string Build() => NumberLockUtil.Join(Head, List);
    }

    public class PrefixApiSyncer
    {
        private Settings _settings;
        private Action<string> _log;
        private System.Threading.Timer _timer;
        private volatile bool _busy;

        public bool Running { get; private set; }
        public int IntervalSeconds { get; private set; } = 60;

        public event Action<PrefixSyncResult> Result;

        public void Configure(Settings s, Action<string> log)
        {
            _settings = s;
            _log = log;
            s.Normalize();
            CustomerDb.Configure(s);
        }

        // ---------- scheduler ----------

        public void Start(int intervalSeconds)
        {
            IntervalSeconds = Math.Clamp(intervalSeconds, 10, 86400);
            Stop();                       // doi lai timer cu neu co
            _timer = new System.Threading.Timer(_ => RunOnce(), null,
                TimeSpan.FromSeconds(2),                     // chay ngay sau 2s sau khi start
                TimeSpan.FromSeconds(IntervalSeconds));
            Running = true;
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            Running = false;
        }

        // ---------- chay 1 vong ----------

        public void RunOnce() => RunOnce(false);

        /// <summary>
        /// Chạy 1 vòng. <paramref name="manual"/> = người dùng bấm tay → LUÔN gửi báo cáo Telegram
        /// (kể cả khi cấu hình "chỉ bắn khi có số thay đổi"), để thao tác có phản hồi rõ ràng.
        /// </summary>
        public void RunOnce(bool manual)
        {
            if (_busy)
            {
                _log?.Invoke("  !! Đang có một vòng đồng bộ khác chạy — bỏ qua lần bấm này.");
                return;
            }
            if (_settings == null) return;
            if (string.IsNullOrWhiteSpace(_settings.prefix_api_url))
            {
                _log?.Invoke(Msg.Prefix.ApiNotConfigured);
                return;
            }

            _busy = true;
            var res = new PrefixSyncResult { StartedAt = DateTime.Now };
            try
            {
                _log?.Invoke(string.Format(Msg.Prefix.FetchStart, DateTime.Now.ToString("HH:mm:ss"), _settings.prefix_api_url));

                var nums = FetchFromApi(res);
                if (nums == null) return;          // res da gan thong bao loi
                // dedupe (payload mau co trung lap) + chuan hoa ve dang server (#84... / 84...)
                nums = nums.Select(NumberLockUtil.NormalizeFromApi)
                           .Where(x => NumberLockUtil.TailKey(x).Length > 0)
                           .Distinct().ToList();
                res.Fetched = nums.Count;
                if (nums.Count == 0)
                {
                    res.Ok = false;
                    res.Message = Msg.Prefix.EmptyList;
                    _log?.Invoke(Msg.Prefix.EmptyListLog);
                    return;
                }
                _log?.Invoke(string.Format(Msg.Prefix.FetchedLog, nums.Count,
                    $"{string.Join(",", nums.Take(12))}{(nums.Count > 12 ? ",..." : "")}"));

                // map trạng thái API: root -> muốn khóa hay mở (trạng thái đến sau thắng)
                var lastByRoot = new Dictionary<string, string>();
                foreach (var x in nums)
                    lastByRoot[NumberLockUtil.TailKey(x)] = x;

                // ẢNH CHỤP toàn bộ số đang có trên VOS3000 của vòng này.
                // Dùng làm "cửa sổ" cho bảng t_number_missing: số cũ rơi khỏi ảnh chụp mà do vòng
                // này tự đọc được => không còn trên server => coi như đã xử lý (giữ bảng = số còn thiếu).
                // Nếu vòng này KHÔNG đọc đủ server thì giữ null => lần này không xoá số cũ (an toàn).
                HashSet<string> serverKeys = null;

                _log?.Invoke(Msg.Prefix.ReadingCurrent);
                var client = new PrefixClient(_settings.vos_server, _settings.vos_port, Bootstrap.ResolveSessionFile(_settings),
                    m => _log?.Invoke(m));
                using var s = OpenVosSession(client);
                if (s == null) { res.Ok = false; res.Message = Msg.Prefix.CannotConnectVos; return; }
                client.LoadRoutingTemplates(LoadTraffic("vos_routing_c2s.bin"), LoadTraffic("vos_routing_c2s.bin"));
                if (!client.HasRoutingTemplates)
                    _log?.Invoke("  !! Thiếu template call-in (FilterGatewayRouting/ReqSetInfoList) — " +
                                 "hãy bắt lại traffic qua proxy và chép vào Data\\vos_routing_c2s.bin.");

                // 0b) dọn sạch số rác từ server (chỉ khi cấu hình cho phép)
                bool cleanedOk = false;
                if (_settings.prefix_remove_invalid)
                {
                    cleanedOk = CleanInvalidNumbers(client, s, lastByRoot);
                    if (cleanedOk) _log?.Invoke("  (dọn số rác OK — danh sách dưới đây đọc lại sau khi dọn)");
                    else _log?.Invoke("  (dọn số rác KHÔNG xác nhận được — chỉ chốt số mới, KHÔNG xoá dòng cũ của bảng)");
                }
                else _log?.Invoke("  (dọn số rác: tắt — danh sách đọc thẳng từ server)");

                // 1) OUT: đọc toàn bộ gateway mapping (call-out) trên VOS
                var mappings = client.ReadAllMappings(s);
                res.OutMappingsTotal = mappings.Count;
                _log?.Invoke(string.Format(Msg.Prefix.AccountsFound, mappings.Count));

                // 1b) IN: đọc toàn bộ gateway routing (call-in) trên VOS — RAM, không đọc DB
                var liveRoutings = client.ReadAllRoutings(s);
                res.InRoutingsTotal = liveRoutings.Count;

                // 2) DB: chủ sở hữu mapping; DB routing chỉ dùng để đối chiếu id
                var owners = LoadOwners(res);
                var routings = MapDbRoutings(liveRoutings);
                var livePairs = ToLivePairs(liveRoutings);

                // 2b) CHỈ UPDATE SỐ ĐÃ CÓ TRÊN VOS3000:
                //     - số API có mà server đã có (bất kỳ account in/out) -> chỉ khóa/mở
                //     - số API chưa có ở bất kỳ đâu trên server -> lưu t_number_missing, KHÔNG add
                var serverKeysLocal = CollectServerKeys(mappings, liveRoutings);
                serverKeys = serverKeysLocal;
                var existingApiByRoot = new Dictionary<string, string>();
                var missingApi = new List<(string Num, string Status)>();
                foreach (var kv in lastByRoot)
                {
                    if (serverKeysLocal.Contains(kv.Key)) existingApiByRoot[kv.Key] = kv.Value;
                    else missingApi.Add((kv.Value, kv.Value.StartsWith("#") ? "locked" : "open"));
                }
                res.ExistingOnServer = existingApiByRoot.Count;
                res.MissingOnServer = missingApi.Count;

                // danh sách số để báo cáo (Telegram / log): giữ thứ tự API trả về
                res.ProcessedNumbers = lastByRoot
                    .Where(kv => existingApiByRoot.ContainsKey(kv.Key))
                    .Select(kv => kv.Value)
                    .ToList();
                res.MissingNumbers = missingApi.Select(x => x.Num).ToList();

                // Nếu vòng này ĐỌC SERVER BỊ RỖNG (lỗi kết nối / template, không phải server thật rỗng)
                // thì mọi số đều "không thấy" -> KHÔNG được coi là số mới, tránh làm rác bảng.
                if (serverKeysLocal.Count == 0)
                {
                    serverKeys = null;
                    // Không đọc được server -> danh sách "chưa có" không đáng tin, đừng báo cáo sai
                    res.MissingNumbers.Clear();
                    res.MissingOnServer = 0;
                    _log?.Invoke("  !! Không đọc được danh sách số nào trên VOS3000 — bỏ qua phân loại 'chưa có' vòng này (tránh ghi sai).");
                }
                else if (missingApi.Count == 0)
                    _log?.Invoke(string.Format(Msg.Prefix.AllOnServerLog, existingApiByRoot.Count));
                else
                    _log?.Invoke(string.Format(Msg.Prefix.MissingOnServerLog, missingApi.Count, existingApiByRoot.Count,
                        string.Join(",", missingApi.Take(10).Select(x => x.Num)) + (missingApi.Count > 10 ? ",..." : "")));

                try
                {
                    var (saved, removedMissing) =
                        ToolDb.SaveMissingNumbersAsync(missingApi, serverKeysLocal, serverSnapshot: serverKeys)
                               .GetAwaiter().GetResult();
                    res.MissingRemoved = removedMissing;
                    if (saved > 0)
                        _log?.Invoke(string.Format(Msg.Prefix.MissingSavedLog, saved, Tables.MissingNumber));
                    if (removedMissing > 0)
                        _log?.Invoke(string.Format(Msg.Prefix.MissingRemovedLog, removedMissing));
                }
                catch (Exception e)
                {
                    _log?.Invoke("  !! " + string.Format(Msg.Prefix.MissingSaveError, e.Message));
                }

                // 3) OUT: merge từng mapping, gom theo account_id
                var outWrites = new List<(MappingEntry Entry, string NewPrefix, long AccountId, string Account)>();
                foreach (var e in mappings)
                {
                    if (string.IsNullOrEmpty(e.Prefix))
                        e.Prefix = GatewayMappingCodec.DefaultPrefix;

                    long accountId = owners.TryGetValue((int)e.MappingId, out var owner) ? owner.CustomerId : 0;
                    string account = !string.IsNullOrEmpty(owner?.Account) ? owner.Account : e.Display;
                    e.CustomerId = accountId;

                    var outcome = MergeAccount(e.Prefix, existingApiByRoot, addMissing: false);
                    res.Added += outcome.Added;
                    res.AccountsLocked += outcome.Locked;
                    res.AccountsOpened += outcome.Opened;

                    if (outcome.Changed)
                    {
                        outWrites.Add((e, outcome.Build(), accountId, account));
                        _log?.Invoke(string.Format(Msg.Prefix.OutMergeLog, account, e.MappingId,
                            outcome.Added, outcome.Locked, outcome.Opened, outcome.List.Count));
                    }
                }

                // 4) IN: merge từng gateway routing (call-in) đọc từ VOS, gom theo account_id
                var inWrites = new List<(CustomerDb.GatewayRoutingRow Row, string NewPrefix, long AccountId, string Account)>();
                foreach (var (r, livePrefix) in routings)
                {
                    var cur = string.IsNullOrEmpty(livePrefix) ? "" : livePrefix;
                    var outcome = MergeAccount(cur, existingApiByRoot, plainList: true, addMissing: false);
                    res.Added += outcome.Added;
                    res.AccountsLocked += outcome.Locked;
                    res.AccountsOpened += outcome.Opened;

                    if (outcome.Changed)
                    {
                        inWrites.Add((r, outcome.Build(), r.CustomerId, r.Account));
                        _log?.Invoke(string.Format(Msg.Prefix.InMergeLog, r.Account, r.Id,
                            outcome.Added, outcome.Locked, outcome.Opened, outcome.List.Count));
                    }
                }

                if (outWrites.Count == 0 && inWrites.Count == 0)
                {
                    res.Ok = true;
                    res.Added = 0;
                    BuildGroupsFromReadback(res, mappings, livePairs, mappings, livePairs);
                    res.AccountsTotal = res.ByAccount.Count;
                    res.FinalPrefix = mappings.FirstOrDefault()?.Prefix;
                    res.Message = string.Format(Msg.Prefix.NothingChangedAll, res.AccountsTotal, nums.Count);
                    _log?.Invoke("  " + res.Message);
                    SaveGroups(res);
                    return;
                }

                // 5) ghi OUT qua VOS (từng mapping, object live đầy đủ của account đó)
                if (outWrites.Count > 0)
                {
                    _log?.Invoke(string.Format(Msg.Prefix.WritingAccounts, outWrites.Count));
                    client.WriteMappings(s, outWrites.Select(x => (x.Entry, x.NewPrefix)).ToList());
                }

                // 6) ghi IN: RAM VOS trước (TCP 1202) rồi MySQL (lưu bền)
                if (inWrites.Count > 0)
                {
                    _log?.Invoke(string.Format(Msg.Prefix.WritingInRoutings, inWrites.Count));
                    var liveById = liveRoutings.ToDictionary(x => x.Id);
                    var ramWrites = inWrites
                        .Where(w => liveById.ContainsKey(w.Row.Id))
                        .Select(w => (liveById[w.Row.Id], w.NewPrefix)).ToList();
                    if (ramWrites.Count > 0) client.WriteRoutings(s, ramWrites);
                    foreach (var w in inWrites)
                        if (!liveById.ContainsKey(w.Row.Id))
                            _log?.Invoke($"  !! in routing id={w.Row.Id} ({w.Account}): không có trên VOS — chỉ ghi DB");

                    int dbOk = 0;
                    foreach (var w in inWrites)
                    {
                        var err = CustomerDb.UpdateGatewayRoutingPrefixAsync(w.Row.Id, w.NewPrefix)
                            .GetAwaiter().GetResult();
                        if (err != null)
                            _log?.Invoke($"  !! in routing id={w.Row.Id} ({w.Account}) MySQL: {err}");
                        else dbOk++;
                    }
                    _log?.Invoke($"  IN: RAM {ramWrites.Count}/{inWrites.Count}, MySQL {dbOk}/{inWrites.Count}");
                }

                // 7) đọc lại để đối chiếu
                var verifyMappings = client.ReadAllMappings(s);
                var verifyRoutings = client.ReadAllRoutings(s);

                var verifyById = verifyMappings.Where(v => v.MappingId > 0)
                                               .GroupBy(v => v.MappingId)
                                               .ToDictionary(g => g.Key, g => g.First());

                int outOk = 0;
                foreach (var w in outWrites)
                {
                    if (w.Entry.MappingId > 0 && verifyById.TryGetValue(w.Entry.MappingId, out var got)
                        && SameNumberSet(got.Prefix, w.NewPrefix))
                        outOk++;
                }

                var verifyRoutingById = verifyRoutings.ToDictionary(x => x.Id);
                int inOk = 0;
                foreach (var w in inWrites)
                {
                    if (verifyRoutingById.TryGetValue(w.Row.Id, out var got)
                        && SameNumberSet(got.Prefix, w.NewPrefix, plainList: true))
                        inOk++;
                }

                res.OutMappingsUpdated = outOk;
                res.InRoutingsUpdated = inOk;
                res.AccountsChanged = outOk + inOk;
                res.AccountsFailed = (outWrites.Count - outOk) + (inWrites.Count - inOk);
                res.FinalPrefix = outWrites.FirstOrDefault().NewPrefix
                                  ?? inWrites.FirstOrDefault().NewPrefix
                                  ?? verifyMappings.FirstOrDefault()?.Prefix;
                BuildGroupsFromReadback(res, verifyMappings, ToLivePairs(verifyRoutings), mappings, livePairs);
                res.AccountsTotal = res.ByAccount.Count;

                if (res.AccountsFailed == 0)
                {
                    res.Ok = true;
                    res.Message = string.Format(Msg.Prefix.AccountsSyncedOk,
                        res.AccountsTotal, res.AccountsChanged, res.Added,
                        res.AccountsLocked, res.AccountsOpened);
                }
                else
                {
                    res.Ok = false;
                    res.Message = string.Format(Msg.Prefix.AccountsSyncedPartial,
                        res.AccountsTotal, outWrites.Count + inWrites.Count, outOk + inOk, res.AccountsFailed);
                }
                _log?.Invoke("  " + res.Message);
                LogGroupSummary(res.ByAccount);
                SaveGroups(res);
            }
            catch (Exception e)
            {
                res.Ok = false;
                res.Message = string.Format(Msg.Prefix.ErrorLog, e.Message);
                _log?.Invoke("!! " + res.Message);
            }
            finally
            {
                _busy = false;
                res.Time = DateTime.Now;
                try { Result?.Invoke(res); } catch { }

                // Monitor: bắn báo cáo ra group Telegram (bất đồng bộ, lỗi không ảnh hưởng sync)
                try { TelegramNotifier.NotifySyncResult(_settings, res, _log, force: manual); } catch { }

                // DB của tool: lưu vong sync + log
                try
                {
                    ToolDb.RecordSyncRun(res.Fetched, res.Added, res.Ok, res.FinalPrefix, res.Message);
                    ToolDb.RecordOperation(_settings, OperationType.SyncPrefix, "", "", res.Ok ? Status.Success : Status.Failed,
                        string.Format(Msg.Prefix.RecordOperation, res.Fetched, res.MissingOnServer, res.Message));
                    if (res.Ok && !string.IsNullOrEmpty(res.FinalPrefix))
                        ToolDb.UpdateMappedNumbers(res.FinalPrefix, MappedSource.Api);
                }
                catch { }
            }
        }

        private VosSession OpenVosSession(PrefixClient client)
        {
            return client.OpenSession();
        }

        /// <summary>
        /// Nạp packet GET/SET call-in (InfoGatewayRouting) từ traffic thật đã bắt qua proxy.
        /// Thứ tự: file cấu hình -> cạnh exe -> %APPDATA% -> thư mục capture của project (khi dev).
        /// </summary>
        internal static byte[] LoadTraffic(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var candidates = new List<string>
            {
                fileName,
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Data", fileName),
                Path.Combine(Bootstrap.DataDir, fileName),
                Path.Combine(Bootstrap.DataDir, "Data", fileName),
                // thư mục nguồn project khi chạy từ build output (dev)
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Data", fileName),
                Path.Combine(AppContext.BaseDirectory, "ProxyCapture", "bin", "Debug", "net8.0", "capture", fileName),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return File.ReadAllBytes(c); } catch { }
            }
            return null;
        }

        // ---------- dọn số rác ----------

        /// <summary>
        /// Dọn các số KHÔNG hợp lệ (rác thủ công, số giả lập) khỏi CẢ call-out (RAM) và call-in (RAM + DB).
        /// Chỉ chạy khi prefix_remove_invalid = true và luôn chừa số mà API đang có.
        /// </summary>
        /// <summary>
        /// Dọn các số KHÔNG hợp lệ (rác thủ công, số giả lập) khỏi CẢ call-out (RAM) và call-in (RAM + DB).
        /// Chỉ chạy khi prefix_remove_invalid = true và luôn chừa số mà API đang có.
        /// Trả true nếu đã đọc + ghi xong cả 2 mảng (khi đó cần ĐỌC LẠI server trước khi phân loại số mới).
        /// </summary>
        private bool CleanInvalidNumbers(PrefixClient client, VosSession s, IReadOnlyDictionary<string, string> apiByRoot)
        {
            if (apiByRoot == null || apiByRoot.Count == 0)
            {
                _log?.Invoke("  [dọn] chưa có danh sách API — BỎ QUA dọn số rác (an toàn).");
                return false;   // không đọc/ghi gì -> danh sách từ server vẫn đúng
            }

            // Không xoá số mà API đang khai báo (kể cả số "trông giả"): chỉ dọn rác thủ công.
            var apiClean = new HashSet<string>(
                apiByRoot.Values.Select(v => NumberLockUtil.Clean(v).TrimStart('#')));
            bool InApi(string n) => apiClean.Contains(NumberLockUtil.Clean(n).TrimStart('#'));

            List<string> Garbage(List<string> list) =>
                list.Where(x => NumberLockUtil.IsGarbage(x) && !InApi(x)).ToList();

            int removedOut = 0, removedIn = 0;
            bool okOut = false, okIn = false;

            // OUT
            try
            {
                var maps = client.ReadAllMappings(s);
                _log?.Invoke($"  [dọn] kiểm tra {maps.Count} mapping call-out...");
                var writes = new List<(MappingEntry, string)>();
                foreach (var m in maps)
                {
                    var (head, nums) = NumberLockUtil.Split(m.Prefix ?? "");
                    var bad = Garbage(nums);
                    _log?.Invoke($"  [dọn] {m.Display}: {nums.Count} số, rác {bad.Count}");
                    if (bad.Count == 0) continue;
                    var kept = nums.Where(x => !bad.Contains(x)).ToList();
                    writes.Add((m, NumberLockUtil.Join(head, kept)));
                    removedOut += bad.Count;
                    _log?.Invoke($"  dọn OUT {m.Display}: -{bad.Count} ({string.Join(",", bad.Take(5))})");
                }
                if (writes.Count > 0) client.WriteMappings(s, writes);
                okOut = true;
            }
            catch (Exception e) { _log?.Invoke("  !! dọn OUT lỗi: " + e.Message); }

            // IN
            try
            {
                var live = client.ReadAllRoutings(s);
                var routes = live.Select(x => (x, bad: Garbage(NumberLockUtil.SplitPlain(x.Prefix))))
                        .Where(t => t.bad.Count > 0).ToList();
                _log?.Invoke($"  [dọn] routing cần dọn: {routes.Count}");
                foreach (var t in routes) _log?.Invoke($"  [dọn] routing {t.x.Id}: rác={string.Join(",", t.bad)}");
                var writes = new List<(RoutingEntry, string)>();
                var dbUpd = new List<(int Id, string Prefix)>();
                foreach (var (r, bad) in routes)
                {
                    var kept = NumberLockUtil.SplitPlain(r.Prefix).Where(x => !bad.Contains(x)).ToList();
                    var joined = string.Join(",", kept);
                    // cập nhật lại object live để nếu ghi tiếp (sync) không đẩy số rác trở lại
                    RoutingCodec.SetPrefix(r.Obj, joined);
                    writes.Add((r, joined));
                    dbUpd.Add(((int)r.Id, joined));
                    removedIn += bad.Count;
                    _log?.Invoke($"  dọn IN {r.Display}: -{bad.Count} ({string.Join(",", bad.Take(5))})");
                }
                if (writes.Count > 0)
                {
                    client.WriteRoutings(s, writes);
                    foreach (var (id, p) in dbUpd)
                        CustomerDb.UpdateGatewayRoutingPrefixAsync(id, p).GetAwaiter().GetResult();
                }
                okIn = true;
            }
            catch (Exception e) { _log?.Invoke("  !! dọn IN lỗi: " + e.Message); }

            if (removedOut + removedIn > 0)
                _log?.Invoke($"  DỌN SỐ RÁC: out -{removedOut}, in -{removedIn} (RAM + MySQL đã cập nhật)");

            return okOut && okIn;
        }

        private Dictionary<int, CustomerDb.GatewayMappingOwner> LoadOwners(PrefixSyncResult res)
        {
            var dict = new Dictionary<int, CustomerDb.GatewayMappingOwner>();
            try
            {
                if (!CustomerDb.HasConfig())
                {
                    _log?.Invoke("  " + Msg.Prefix.DbNotConfigured);
                    return dict;
                }
                var list = CustomerDb.LoadGatewayMappingOwnersAsync().GetAwaiter().GetResult();
                foreach (var x in list) dict[x.MappingId] = x;
                _log?.Invoke(string.Format(Msg.Prefix.OwnersLoaded, list.Count));
            }
            catch (Exception e)
            {
                _log?.Invoke("  !! " + string.Format(Msg.Prefix.DbReadError, e.Message));
            }
            return dict;
        }

        /// <summary>
        /// Ghép routing đọc từ VOS (RAM, object live) với thông tin chủ sở hữu trong DB
        /// (clearingcustomer_id/account chỉ có ở e_gatewayrouting). Trả (row, prefixLive).
        /// </summary>
        private List<(CustomerDb.GatewayRoutingRow Row, string Prefix)> MapDbRoutings(List<RoutingEntry> liveRoutings)
        {
            var res = new List<(CustomerDb.GatewayRoutingRow, string)>();
            Dictionary<int, CustomerDb.GatewayRoutingRow> dbById = new();
            try
            {
                if (CustomerDb.HasConfig())
                    dbById = CustomerDb.LoadGatewayRoutingsAsync().GetAwaiter().GetResult()
                                      .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception e)
            {
                _log?.Invoke("  !! " + string.Format(Msg.Prefix.DbReadError, e.Message));
            }

            foreach (var e in liveRoutings)
            {
                // ưu tiên khớp theo id routing; nếu DB chưa có thì thử khớp theo account
                CustomerDb.GatewayRoutingRow row = null;
                bool byId = dbById.TryGetValue((int)e.Id, out row);
                if (!byId && !string.IsNullOrEmpty(e.Account))
                    row = dbById.Values.FirstOrDefault(x =>
                        string.Equals(x.Account, e.Account, StringComparison.OrdinalIgnoreCase));

                _log?.Invoke($"    routing {e.Id} acc='{e.Account}': dbById={byId} customer={(row?.CustomerId ?? 0)}");

                if (row != null)
                {
                    // lấy prefix live (RAM) nhưng giữ clearingcustomer_id của DB
                    row = new CustomerDb.GatewayRoutingRow
                    {
                        Id = row.Id,
                        CustomerId = row.CustomerId,
                        Account = string.IsNullOrEmpty(row.Account) ? e.Account : row.Account,
                        Name = row.Name,
                        Prefix = e.Prefix ?? ""
                    };
                }
                else
                {
                    row = new CustomerDb.GatewayRoutingRow
                    {
                        Id = (int)e.Id,
                        CustomerId = (int)e.ClearingCustomerId,
                        Account = e.Account,
                        Name = e.Name,
                        Prefix = e.Prefix ?? ""
                    };
                }

                if (row.CustomerId <= 0)
                    _log?.Invoke($"  !! routing id={row.Id} ({row.Account}): không xác định được account trong DB " +
                                 "(e_gatewayrouting.clearingcustomer_id) — bỏ khỏi tổng hợp theo account.");

                res.Add((row, e.Prefix ?? ""));
            }

            foreach (var kv in dbById)
                if (!liveRoutings.Any(x => (int)x.Id == kv.Key))
                    _log?.Invoke($"  (routing id={kv.Key} ({kv.Value.Account}) không có trên VOS — bỏ qua)");

            _log?.Invoke($"  IN: {res.Count} routing trên VOS (RAM).");
            return res;
        }

        private static List<(CustomerDb.GatewayRoutingRow Row, string Prefix)> ToLivePairs(List<RoutingEntry> live) => live
            .Select(x => (new CustomerDb.GatewayRoutingRow
            {
                Id = (int)x.Id,
                CustomerId = (int)x.ClearingCustomerId,
                Account = x.Account,
                Name = x.Name,
                Prefix = x.Prefix
            }, x.Prefix ?? ""))
            .ToList();

        /// <summary>
        /// Tập "gốc số" (TailKey) của MỌI số đang có trên VOS3000:
        /// call-out (gateway mapping) + call-in (gateway routing). Dùng để quyết định
        /// số nào từ API được phép update, số nào phải lưu vào bảng "chưa có".
        /// </summary>
        private static HashSet<string> CollectServerKeys(List<MappingEntry> mappings, List<RoutingEntry> routings)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in mappings ?? new List<MappingEntry>())
                foreach (var n in NumberLockUtil.Split(m.Prefix ?? "").Nums)
                {
                    var k = NumberLockUtil.TailKey(n);
                    if (k.Length > 0) set.Add(k);
                }
            foreach (var r in routings ?? new List<RoutingEntry>())
                foreach (var n in NumberLockUtil.SplitPlain(r.Prefix))
                {
                    var k = NumberLockUtil.TailKey(n);
                    if (k.Length > 0) set.Add(k);
                }
            return set;
        }

        // ---------- tổng hợp theo account + hướng ----------

        /// <summary>
        /// Dựng dictionary account_id -> {in[], out[]} từ danh sách ĐỌC LẠI.
        /// Nhiều mapping/routing cùng 1 account được gộp (khử trùng theo gốc số).
        /// </summary>
        private static void BuildGroupsFromReadback(
            PrefixSyncResult res,
            List<MappingEntry> mappings,
            List<(CustomerDb.GatewayRoutingRow Row, string Prefix)> routings,
            List<MappingEntry> fallbackMappings,
            List<(CustomerDb.GatewayRoutingRow Row, string Prefix)> fallbackRoutings)
        {
            var owners = new Dictionary<int, CustomerDb.GatewayMappingOwner>();
            try
            {
                if (CustomerDb.HasConfig())
                    foreach (var x in CustomerDb.LoadGatewayMappingOwnersAsync().GetAwaiter().GetResult())
                        owners[x.MappingId] = x;
            }
            catch { }

            var groups = new Dictionary<long, AccountNumberGroup>();

            AccountNumberGroup Get(long id, string account)
            {
                if (!groups.TryGetValue(id, out var g))
                    groups[id] = g = new AccountNumberGroup { AccountId = id, Account = account ?? "" };
                else if (string.IsNullOrEmpty(g.Account) && !string.IsNullOrEmpty(account))
                    g.Account = account;
                return g;
            }

            // OUT
            foreach (var m in mappings)
            {
                if (string.IsNullOrEmpty(m.Prefix)) continue;
                long id = owners.TryGetValue((int)m.MappingId, out var o) ? o.CustomerId : m.CustomerId;
                if (id <= 0) id = 0;
                string account = !string.IsNullOrEmpty(o?.Account) ? o.Account : m.Display;
                AddNumbers(Get(id, account).Out, m.Prefix);
            }

            // IN
            foreach (var (row, prefix) in routings)
            {
                if (string.IsNullOrEmpty(prefix)) continue;
                if (row.CustomerId <= 0) continue;          // không map được account -> bỏ
                AddNumbers(Get(row.CustomerId, row.Account).In, prefix, plainList: true);
            }

            // Nếu readback rỗng (server chưa trả), dùng dữ liệu đã đọc trước khi ghi
            if (groups.Count == 0)
            {
                foreach (var m in fallbackMappings)
                {
                    if (string.IsNullOrEmpty(m.Prefix)) continue;
                    long id = m.CustomerId <= 0 ? 0 : m.CustomerId;
                    AddNumbers(Get(id, m.Display).Out, m.Prefix);
                }
                foreach (var (row, prefix) in fallbackRoutings)
                {
                    if (row.CustomerId <= 0) continue;
                    AddNumbers(Get(row.CustomerId, row.Account).In, prefix, plainList: true);
                }
            }

            res.ByAccount = groups;
        }

        /// <summary>Thêm các số trong prefix vào list đích, khử trùng theo gốc số (entry sau thắng).</summary>
        internal static void AddNumbers(List<string> target, string prefix, bool plainList = false)
        {
            var idx = new Dictionary<string, int>();
            for (int i = 0; i < target.Count; i++)
            {
                var k = NumberLockUtil.TailKey(target[i]);
                if (k.Length > 0 && !idx.ContainsKey(k)) idx[k] = i;
            }

            IEnumerable<string> nums = plainList
                ? (prefix ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : NumberLockUtil.Split(prefix ?? "").Nums;

            foreach (var n in nums)
            {
                var k = NumberLockUtil.TailKey(n);
                if (k.Length == 0) continue;
                if (idx.TryGetValue(k, out var pos)) target[pos] = n;
                else { idx[k] = target.Count; target.Add(n); }
            }
        }

        private void LogGroupSummary(Dictionary<long, AccountNumberGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;
            _log?.Invoke(string.Format(Msg.Prefix.GroupSummary, groups.Count));
            foreach (var g in groups.OrderBy(x => x.Key).Take(20))
            {
                _log?.Invoke(string.Format(Msg.Prefix.GroupLine,
                    g.Key, string.IsNullOrEmpty(g.Value.Account) ? "?" : g.Value.Account,
                    g.Value.InCount, g.Value.InLocked, g.Value.OutCount, g.Value.OutLocked));
            }
        }

        private void SaveGroups(PrefixSyncResult res)
        {
            try
            {
                if (res.ByAccount == null || res.ByAccount.Count == 0) return;
                // Ghi đồng bộ để tab "Số theo account (VOS)" đọc lại ngay sau khi vòng sync kết thúc.
                // pruneMissing: chỉ dọn account cũ khi vòng này OK (ảnh chụp server đáng tin).
                ToolDb.SaveAccountNumberSetsAsync(res.ByAccount.Values, pruneMissing: res.Ok)
                       .GetAwaiter().GetResult();
            }
            catch { }
        }

        // ---------- merge 1 account theo lệnh API ----------

        /// <summary>
        /// Áp lệnh API lên danh sách số của MỘT account:
        ///   - addMissing = false (chế độ hiện tại): CHỈ số đã có trên account mới được khóa/mở.
        ///     Số API chưa có trên account KHÔNG được thêm (đã lưu riêng ở t_number_missing).
        ///   - Số có sẵn nhưng trạng thái khác API -> KHÓA (#) hoặc MỞ (84…).
        ///   - Số có trên account mà API không khai -> KHÔNG đụng (không xoá).
        ///   - Dọn entry trùng gốc số (giữ entry đến sau).
        ///
        /// plainList = true  : chuỗi chỉ là danh sách "84...,#84..." (e_gatewayrouting.prefix),
        ///                     không có phần đầu "x;" (mapping call-out mới có).
        /// </summary>
        internal static MergeOutcome MergeAccount(string currentPrefix, IReadOnlyDictionary<string, string> apiByRoot,
            bool plainList = false, bool addMissing = false)
        {
            // Chế độ hiện tại của tab "Add Số (VOS)": KHÔNG BAO GIỜ tự thêm số mới —
            // số chưa có trên server đã được lưu riêng ở bảng t_number_missing.
            addMissing = false;
            string head;
            List<string> nums;
            if (plainList)
            {
                head = "";
                nums = (currentPrefix ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            else
            {
                var (headRaw, parsed) = NumberLockUtil.Split(currentPrefix);
                head = string.IsNullOrEmpty(headRaw) || headRaw == ";" ? "1;" : headRaw;
                nums = new List<string>(parsed);
            }
            var list = nums;
            var outcome = new MergeOutcome { Head = head };

            // 1) số mới: API có, account chưa có — CHỈ khi addMissing (mặc định TẮT)
            if (addMissing)
            {
                var present = new HashSet<string>(list.Select(NumberLockUtil.TailKey));
                foreach (var kv in apiByRoot)
                {
                    if (present.Contains(kv.Key)) continue;
                    list.Add(kv.Value);
                    outcome.Added++;
                }
            }

            // 2) chuyển trạng thái số có sẵn theo API
            for (int i = 0; i < list.Count; i++)
            {
                var sr = list[i];
                var k = NumberLockUtil.TailKey(sr);
                if (k.Length == 0 || !apiByRoot.TryGetValue(k, out var tok)) continue;
                bool curLocked = !NumberLockUtil.IsValid(sr);
                bool wantLock = tok.StartsWith("#");
                if (curLocked == wantLock) continue;
                list[i] = wantLock ? NumberLockUtil.Lock(sr) : NumberLockUtil.Unlock(sr);
                if (wantLock) outcome.Locked++; else outcome.Opened++;
            }

            // 3) dọn trùng gốc (entry đến sau thắng, giữ vị trí đầu)
            int before = list.Count;
            var idxByKey = new Dictionary<string, int>();
            var outList = new List<string>(list.Count);
            foreach (var x in list)
            {
                var k = NumberLockUtil.TailKey(x);
                if (k.Length > 0 && idxByKey.TryGetValue(k, out var pos))
                    outList[pos] = x;
                else
                {
                    if (k.Length > 0) idxByKey[k] = outList.Count;
                    outList.Add(x);
                }
            }
            outcome.RemovedDup = before - outList.Count;
            outcome.List = outList;
            return outcome;
        }

        /// <summary>So sánh 2 prefix theo TẬP (gốc số -> trạng thái khóa/mở), bỏ qua thứ tự.</summary>
        internal static bool SameNumberSet(string a, string b, bool plainList = false)
        {
            static Dictionary<string, bool> Map(string p, bool plain)
            {
                var d = new Dictionary<string, bool>();
                IEnumerable<string> nums = plain
                    ? (p ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : NumberLockUtil.Split(p ?? "").Nums;
                foreach (var x in nums)
                {
                    var k = NumberLockUtil.TailKey(x);
                    if (k.Length > 0) d[k] = !NumberLockUtil.IsValid(x);
                }
                return d;
            }

            var ma = Map(a, plainList);
            var mb = Map(b, plainList);
            if (ma.Count != mb.Count) return false;
            foreach (var kv in ma)
                if (!mb.TryGetValue(kv.Key, out var locked) || locked != kv.Value) return false;
            return true;
        }

        // ---------- API ----------

        private List<string> FetchFromApi(PrefixSyncResult res)
        {
            long? watermark = null;
            bool isFirst = true;
            string watermarkFrom = null;
            try
            {
                // ---- Mốc last_created_at (luôn bật, không cần cấu hình) ----
                // Lần đầu (chưa có mốc) => gọi full URL.
                // Từ lần 2 => ?created_at=<mốc>, để server chỉ trả số mới.
                watermark = SyncWatermark.Read(_settings, out watermarkFrom);
                isFirst = !watermark.HasValue;
                _log?.Invoke(isFirst
                    ? string.Format(Msg.Prefix.WatermarkFirstRun, _settings.prefix_api_url)
                    : string.Format(Msg.Prefix.WatermarkIncremental, watermark.Value, watermarkFrom ?? "?"));

                var url = SyncWatermark.BuildUrl(_settings.prefix_api_url, watermark);

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        _settings.verify_tls ? null : (sender, cert, chain, errors) => true
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(_settings.prefix_api_key))
                    req.Headers.TryAddWithoutValidation(HttpKey.ApiKey, _settings.prefix_api_key);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = http.SendAsync(req).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    res.Ok = false;
                    res.Message = string.Format(Msg.Prefix.HttpFail, (int)resp.StatusCode, resp.ReasonPhrase, sw.ElapsedMilliseconds);
                    _log?.Invoke("  !! " + res.Message + " — body: " + Trunc(body, 300));
                    return null;
                }
                _log?.Invoke(string.Format(Msg.Prefix.ApiOkLog, sw.ElapsedMilliseconds, (int)resp.StatusCode, body.Length));

                var nums = ParseNumbers(body, _settings.prefix_api_field, m => _log?.Invoke("  " + m));
                if (nums == null)
                {
                    res.Ok = false;
                    res.Message = string.Format(Msg.Prefix.FieldNotFound, _settings.prefix_api_field);
                    _log?.Invoke("  !! " + res.Message);
                    return null;
                }

                // Server trả last_created_at -> lưu làm mốc cho các lần gọi sau.
                var next = ReadLastCreatedAt(body);
                if (next.HasValue)
                    SyncWatermark.Write(_settings, next.Value, m => _log?.Invoke("  " + m));
                else if (!isFirst)
                    _log?.Invoke("  " + Msg.Prefix.WatermarkMissingLog);
                return nums;
            }
            catch (Exception e)
            {
                res.Ok = false;
                res.Message = e is TaskCanceledException ? string.Format(Msg.Prefix.Timeout, 15) : e.Message;
                _log?.Invoke("  !! " + string.Format(Msg.Prefix.CallApiError, res.Message));
                return null;
            }
        }

        internal static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");

        /// Đọc mốc "last_created_at" trong JSON (trong "data" hoặc ở gốc).
        internal static long? ReadLastCreatedAt(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (TryReadAt(root, ApiField.LastCreatedAt, out var v)) return v;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(ApiField.Data, out var data)
                    && TryReadAt(data, ApiField.LastCreatedAt, out v)) return v;
            }
            catch { }
            return null;
        }

        static bool TryReadAt(JsonElement el, string name, out long value)
        {
            value = 0;
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return false;
            if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt64(out value) && value > 0;
            if (p.ValueKind == JsonValueKind.String)
                return long.TryParse(p.GetString(), out value) && value > 0;
            return false;
        }

        /// Parse JSON: uu tien theo field path (vd "data.phone"); khong duoc thi tu do tim
        /// mang dau tien toan chu so trong toan bo JSON.
        internal static List<string> ParseNumbers(string body, string fieldPath, Action<string> log)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                List<string> nums = null;
                if (!string.IsNullOrWhiteSpace(fieldPath))
                {
                    var cur = root;
                    bool found = true;
                    foreach (var part in fieldPath.Split('.'))
                    {
                        if (found && cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next))
                            cur = next;
                        else { found = false; break; }
                    }
                    if (found && cur.ValueKind == JsonValueKind.Array && AllDigitStrings(cur, log))
                    {
                        nums = ElementsToNumbers(cur, log);
                    }
                    else if (found && cur.ValueKind == JsonValueKind.Array)
                        log?.Invoke(string.Format(Msg.Prefix.FieldNotArrayLog, fieldPath));
                    else if (!found)
                        log?.Invoke(string.Format(Msg.Prefix.FieldMissingLog, fieldPath));
                }

                if (nums == null && FindArray(root, out var arr, log))
                    nums = ElementsToNumbers(arr, log);

                return nums;
            }
            catch (Exception e)
            {
                log?.Invoke(string.Format(Msg.Prefix.JsonErrorLog, e.Message));
                return null;
            }
        }

        static bool AllDigitStrings(JsonElement arr, Action<string> log)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) return false;
                var v = (el.GetString() ?? "").Trim();
                if (!IsNumberToken(v)) return false;
            }
            return true;
        }

        /// Một phần tử được coi là số nếu sau khi bỏ hết '#' còn toàn chữ số
        /// (API có thể trả dạng khóa "#2369995995").
        static bool IsNumberToken(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            foreach (var c in v)
                if (c != '#' && (c < '0' || c > '9')) return false;
            return v.Any(c => c >= '0' && c <= '9');
        }

        static List<string> ElementsToNumbers(JsonElement arr, Action<string> log)
        {
            var l = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var v = (el.GetString() ?? "").Trim();
                if (v.Length == 0) { log?.Invoke(Msg.Prefix.SkipEmptyItem); continue; }
                if (IsNumberToken(v)) l.Add(v);
                else log?.Invoke(string.Format(Msg.Prefix.SkipNonNumber, Trunc(v, 40)));
            }
            return l;
        }

        // tu do tim mang dau tien toan chu so beam trong toan bo JSON
        static bool FindArray(JsonElement el, out JsonElement found, Action<string> log)
        {
            found = default;
            switch (el.ValueKind)
            {
                case JsonValueKind.Array:
                    if (AllDigitStrings(el, log)) { found = el; return true; }
                    foreach (var sub in el.EnumerateArray())
                        if (FindArray(sub, out found, log)) return true;
                    break;

                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                        if (FindArray(p.Value, out found, log)) return true;
                    break;
            }
            return false;
        }
    }
}
