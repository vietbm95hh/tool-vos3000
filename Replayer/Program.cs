// ============================================================
//  VosReplayer v2 - Tool thao tac Mapping Caller Prefix VOS3000
//  Qua giao thuc TCP port 1202 (khong DB, khong UI, khong proxy).
//
//  Lenh:
//    VosReplayer pentest [--host H] [--port P] [--session F] [--quick]
//                          Chay bo test chuan, tu dong khoi phuc baseline.
//    VosReplayer get    [--flow fast|full]
//                          Doc prefix hien tai cua mapping.
//    VosReplayer set    --prefix "1;0901,0912"
//                          Ghi prefix + verify bang GET.
//    VosReplayer add    --items 0988,0999  Them so moi vao cuoi danh sach (tu doc gia tri hien tai)
//    VosReplayer remove --items 0999      Xoa so khoi danh sach
//    VosReplayer replay --session F [--apply] [--prefix X] [--delay N]
//                          Che do replay toan bo session cu (v1).
// ============================================================
using System.Text;

var defaultSession = @"C:\Users\PC\Documents\Default Project\Vos3000AgentCSharp\ProxyCapture\bin\Debug\net8.0\capture\s01_c2s.bin";

string host = "172.29.101.32";
int port = 1202;
string sessionPath = defaultSession;
string? userArg = null;
string? passArg = null;
string? prefixArg = null;
string? itemsArg = null;
string flow = "fast";
bool quick = false;
bool apply = false;
int delayMs = 700;

string cmd = "help";
{
    var rest = new List<string>(args);
    int ci = rest.FindIndex(a => !a.StartsWith("--"));
    if (ci >= 0) { cmd = rest[ci]; rest.RemoveAt(ci); }
    apply = rest.Contains("--apply");
    quick = rest.Contains("--quick");
    for (int i = 0; i < rest.Count - 1; i++)
    {
        switch (rest[i])
        {
            case "--host": host = rest[i + 1]; break;
            case "--port": port = int.Parse(rest[i + 1]); break;
            case "--session": sessionPath = rest[i + 1]; break;
            case "--prefix": prefixArg = rest[i + 1]; break;
            case "--items": itemsArg = rest[i + 1]; break;
            case "--flow": flow = rest[i + 1]; break;
            case "--delay": delayMs = int.Parse(rest[i + 1]); break;
            case "--user": userArg = rest[i + 1]; break;
            case "--pass": passArg = rest[i + 1]; break;
        }
    }
}

void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

static string Help() =>
"""
Cach dung: VosReplayer <get|set|pentest|replay> [tuy chon]
  get      --flow fast|full
  set      --prefix "1;0901,0912"
  pentest  [--quick]
  replay   --session <file> [--apply] [--prefix X]
  login    --user U --pass P [--out <file>] [--test]
Chung: --host 172.29.101.32 --port 1202
""";

switch (cmd)
{
    case "get": await RunGet(); break;
    case "set": await RunSet(); break;
    case "add": await RunAddRemove(true); break;
    case "remove": await RunAddRemove(false); break;
    case "pentest": await RunPentest(); break;
    case "locktest": await RunLockTest(); break;
    case "replay": await RunLegacyReplay(); break;
    case "login": await RunLoginSet(); break;
    default: Log(Help()); break;
}
return;

// ================== flows ==================

List<byte[]> LoadPackets()
{
    var raw = File.ReadAllBytes(sessionPath);
    var pk = VosProto.SplitPackets(raw);
    Log($"Session: {Path.GetFileName(sessionPath)} -> {pk.Count} packet");
    return pk;
}

(int Hello, int Login, int MapGet, int Set) FindIndices(List<byte[]> pk)
{
    int hello = -1, login = -1, mapget = -1, set = -1;
    for (int i = 0; i < pk.Count; i++)
    {
        var p = pk[i];
        if (hello < 0 && !VosProto.IsGzip(p)) { hello = i; continue; }
        if (!VosProto.TryDecompressFrom(p, VosProto.HeaderLen, out var obj)) continue;
        var txt = Encoding.UTF8.GetString(obj);
        if (login < 0 && txt.Contains("loginName")) login = i;
        if (mapget < 0 && txt.Contains("FilterGatewayMapping")) mapget = i;
        if (set < 0 && txt.Contains("ReqSetInfoList")) set = i;
    }
    Log($"Vi tri packet: hello=#{hello} login=#{login} GET-mapping=#{mapget} SET=#{set}");
    return (hello, login, mapget, set);
}

// Do prefix hien tai. mode: 0=fast (hello->login->GET), 1=full (replay 0..mapget)
PrefixRead GetOnSession(VosSession s, List<byte[]> pk, (int Hello, int Login, int MapGet, int Set) ix, int mode)
{
    var objs = new List<byte[]>();
    if (mode == 0)
    {
        foreach (var o in s.SendPacket(pk[ix.Hello], 900)) objs.Add(o);
        foreach (var o in s.SendPacket(pk[ix.Login], 1200)) objs.Add(o);
        foreach (var o in s.SendPacket(pk[ix.MapGet], 1500)) objs.Add(o);
    }
    else
    {
        for (int i = 0; i <= ix.MapGet; i++)
            foreach (var o in s.SendPacket(pk[i], 700)) objs.Add(o);
    }
    var r = new PrefixRead();
    foreach (var o in objs) r.All.AddRange(VosProto.AllPrefixStrings(o));
    r.Value = r.All.Count > 0 ? r.All.OrderByDescending(x => x.Length).First() : null;
    return r;
}

// Gui SET prefix roi GET lai trong cung session. Tra (guiOK, giaTriDocLai)
// ---- doi tai khoan dang nhap trong session template ----
async Task RunLoginSet()
{
    if (string.IsNullOrEmpty(userArg) || passArg == null)
    {
        Log("Thieu --user/--pass. Vi du: VosReplayer login --user admin --pass admin --test");
        return;
    }

    var pk = VosProto.SplitPackets(File.ReadAllBytes(sessionPath));
    int li = Replayer.VosLoginTemplate.FindLoginIndex(pk);
    if (li < 0) { Log("Khong tim thay packet login trong session: " + sessionPath); return; }

    var cur = Replayer.VosLoginTemplate.Read(pk[li]);
    Log($"Session: {sessionPath}");
    Log($"Login hien tai: user={cur.LoginName} pass len={cur.Password.Length}");

    string outFile = Path.Combine(Path.GetTempPath(), "Vos3000AgentCSharp", "vos_session_login.bin");
    var saved = Replayer.VosLoginTemplate.SaveWithLogin(sessionPath, outFile, userArg, passArg,
        out string err, out int idx, s => Log("  " + s));
    if (saved == null) { Log("LOI: " + err); return; }
    Log($"Da ghi session moi: {saved} (packet login #{idx})");

    if (Environment.GetCommandLineArgs().Contains("--test"))
    {
        var npk = VosProto.SplitPackets(File.ReadAllBytes(saved));
        var (ok, msg) = Replayer.VosLoginTemplate.TestLogin(host, port, npk[0], npk[idx]);
        Log($"TEST LOGIN -> {(ok ? "OK" : "FAIL")}: {msg}");
    }
    await Task.CompletedTask;
}

async Task<(bool Sent, string? ReadBack)> SetOnSession(VosSession s, List<byte[]> pk, (int Hello, int Login, int MapGet, int Set) ix, string value)
{
    foreach (var _ in s.SendPacket(pk[ix.Hello], 900)) { }
    foreach (var _ in s.SendPacket(pk[ix.Login], 1200)) { }
    // GET live TRUOC khi SET: de patch id (mapping co the bi xoa/tao lai tren server)
    PrefixRead live = ReadNow(s, pk, ix);
    byte[]? liveObj = null;
    if (live != null)
    {
        for (int i = live.Objects.Count - 1; i >= 0; i--)
        {
            var txt = Encoding.UTF8.GetString(live.Objects[i]);
            if (txt.Contains("InfoGatewayMapping")) { liveObj = live.Objects[i]; break; }
        }
    }
    if (liveObj == null) Log("    (khong doc duoc object live truoc SET -> SET dung id template cu)");
    var setPkt = VosProto.BuildSetPacket(pk[ix.Set], value, liveObj, m => Log(m));
    if (setPkt == null) return (false, null);
    Log($"    SET prefix='{value}' ({setPkt.Length + 4} bytes lenh nay) ...");
    var resp = s.SendPacket(setPkt, 1500);
    if (resp.Count == 0) Log($"    (khong nhan lai phan hoi tu SET, alive={s.IsAlive})");
    await Task.Delay(300);
    foreach (var _ in s.ReadAvailable(600)) { }   // xa su kien server push
    // GET verify
    string? rb = null;
    foreach (var o in s.SendPacket(pk[ix.MapGet], 1500))
    {
        var v = VosProto.FindCallerPrefix(o);
        if (v != null) rb = v;
    }
    return (resp.Count > 0 || s.IsAlive, rb);
}

// Chi gui GET (packet MapGet) va doc gia tri prefix hien tai — session da login
PrefixRead ReadNow(VosSession s, List<byte[]> pk, (int Hello, int Login, int MapGet, int Set) ix)
{
    var r = new PrefixRead();
    foreach (var o in s.SendPacket(pk[ix.MapGet], 1500)) r.Objects.Add(o);
    var strs = new List<string>();
    foreach (var o in r.Objects) strs.AddRange(VosProto.AllPrefixStrings(o));
    if (strs.Count > 0) r.Value = strs.OrderByDescending(x => x.Length).First();
    return r;
}

VosSession NewSession() => new VosSession(host, port, m => Log("      " + m));

PrefixRead ReadBaseline(List<byte[]> pk, (int Hello, int Login, int MapGet, int Set) ix)
{
    using var s = NewSession();
    if (!s.Connect()) { Log("Khong ket noi duoc: " + s.LastError); return PrefixRead.Empty(); }
    var r = GetOnSession(s, pk, ix, 0);
    if (r.Value == null)
    {
        Log("Flow fast khong doc duoc prefix -> thu flow full...");
        s.Close();
        using var s2 = NewSession();
        if (s2.Connect())
            r = GetOnSession(s2, pk, ix, 1);
    }
    return r;
}

// ================== commands ==================

async Task RunGet()
{
    Log($"GET prefix tu {host}:{port}");
    var pk = LoadPackets();
    var ix = FindIndices(pk);
    var r = ReadBaseline(pk, ix);
    if (r.Value == null) { Log("KHONG DOC DUOC PREFIX."); return; }
    Log("");
    Log("========== KET QUA ==========");
    Log("calloutCallerPrefixes: " + r.Value);
    Log("Cac chuoi prefix khac tim thay: " + string.Join(" ; ", r.All));
}

async Task RunSet()
{
    if (string.IsNullOrWhiteSpace(prefixArg)) { Log("--prefix la bat buoc, vi du: --prefix \"1;0901,0912\""); return; }
    Log($"SET prefix '{prefixArg}' vao {host}:{port}");
    var pk = LoadPackets();
    var ix = FindIndices(pk);
    using var s = NewSession();
    if (!s.Connect()) { Log("Khong ket noi duoc: " + s.LastError); return; }
    var (sent, rb) = await SetOnSession(s, pk, ix, prefixArg);
    Log("");
    Log("========== KET QUA ==========");
    Log("SET gui:    " + (sent ? "OK" : "THAT BAI"));
    Log("GET readback: " + (rb ?? "?"));
    Log("Gia tri khop: " + (rb == prefixArg ? "KHOP ✓" : "KHONG KHOP ✗"));
}

// them/xoa so vao danh sach prefix hien tai (doc truoc, sua, ghi lai, verify)
async Task RunAddRemove(bool add)
{
    if (string.IsNullOrWhiteSpace(itemsArg)) { Log("--items la bat buoc, vi du: --items 0988,0999"); return; }
    var items = itemsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(x => x.Length > 0).ToArray();
    foreach (var it in items)
        if (it.Any(c => c < '0' || c > '9')) { Log($"So khong hop le: '{it}' (chi duoc chua 0-9)"); return; }
    if (items.Length == 0) { Log("Khong co so nao de xu ly."); return; }

    Log($"{(add ? "ADD" : "REMOVE")} [{string.Join(",", items)}] {(add ? "vao" : "khoi")} danh sach prefix tai {host}:{port}");
    var pk = LoadPackets();
    var ix = FindIndices(pk);

    using var s = NewSession();
    if (!s.Connect()) { Log("Khong ket noi duoc: " + s.LastError); return; }
    var r = GetOnSession(s, pk, ix, 0);
    if (r.Value == null) { Log("KHONG doc duoc prefix hien tai - dung de an toan."); return; }
    string cur = r.Value;
    Log($"  Hien tai: '{cur}'");

    string head = cur.Substring(0, 2);
    var list = cur.Substring(2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    foreach (var it in items)
    {
        if (add)
        {
            if (list.Contains(it)) { Log($"  '{it}' da ton tai - bo qua"); continue; }
            list.Add(it);
            Log($"  + them '{it}'");
        }
        else
        {
            if (!list.Remove(it)) Log($"  - '{it}' khong co trong danh sach (bo qua)");
            else Log($"  - da xoa '{it}'");
        }
    }
    string newVal = head + string.Join(",", list);
    if (newVal == cur) { Log("Gia tri khong doi - khong can ghi."); return; }
    Log($"  Gia tri moi: '{newVal}'");

    var (sent, rb) = await SetOnSession(s, pk, ix, newVal);
    Log("");
    Log("========== KET QUA ==========");
    Log("SET gui:      " + (sent ? "OK" : "THAT BAI"));
    Log("GET readback: " + (rb ?? "?"));
    Log("Ket qua:      " + (rb == newVal ? "THANH CONG ✓" : "KHONG KHOP ✗"));
}

// replay v1: gui toan bo packet trong session; tam dung truoc SET neu khong --apply
async Task RunLegacyReplay()
{
    if (!File.Exists(sessionPath)) { Log("File session khong ton tai: " + sessionPath); return; }
    Log($"REPLAY (mode: {(apply ? "INCLUDE-SET (thu tu SET cu" + (prefixArg != null ? ", patch prefix '" + prefixArg + "'" : "") + ")" : "DRY - tam dung truoc SET")})");
    var pk = LoadPackets();
    var ix = FindIndices(pk);
    byte[]? pktSet = pk[ix.Set];
    if (apply && prefixArg != null)
    {
        var patched = VosProto.BuildSetPacket(pktSet, prefixArg, m => Log("  " + m));
        if (patched == null) { Log("Khong patch duoc. Dung."); return; }
        pktSet = patched;
        Log($"Da patch packet #{ix.Set} -> prefix '{prefixArg}'");
    }
    using var s = NewSession();
    if (!s.Connect()) { Log("Khong ket noi duoc: " + s.LastError); return; }
    for (int i = 0; i < pk.Count; i++)
    {
        if (i == ix.Set && !apply) { Log($"#{i} -- TAM DUNG truoc SET (DRY)."); break; }
        var pkt = i == ix.Set ? pktSet : pk[i];
        Log($"GUI #{i} ({pkt.Length} bytes)");
        s.SendPacket(pkt, apply || i >= ix.MapGet ? 900 : 700);
        if (!s.IsAlive) { Log("  KET NOI BI DONG - dung."); break; }
    }
    await Task.Delay(1500);
    s.Close();
    Log("Hoan tat replay.");
}

// ================== LOCKTEST — 6 kịch bản khóa số (GUI vs API) ==================
//
// Chạy trên VOS THẬT nhưng chỉ đụng số rác, save/restore baseline:
//   RS = 840123456782 (có sẵn trong baseline), RN1..RN4 = 840123456700..703 (số rác)
// Logic sync mô phỏng Y HỆT PrefixApiSyncer: TailKey + dedupe ưu tiên # + auto-lock + safe-SET 3 vòng.
// LƯU Ý: cần tắt app (Vos3000Agent) khi chạy để lịch sync không ghi đè trong lúc test.
//
async Task RunLockTest()
{
    Console.OutputEncoding = Encoding.UTF8;
    string dir = Path.Combine(AppContext.BaseDirectory, "pentest");
    Directory.CreateDirectory(dir);
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    string rptPath = Path.Combine(dir, $"locktest_report_{stamp}.txt");
    var rpt = new StreamWriter(rptPath, append: false, Encoding.UTF8) { AutoFlush = true };
    int pass = 0, fail = 0, info = 0;
    void Rec(string id, string verdict, string name, string detail)
    {
        var line = $"[{id,-4}] {verdict,-4} | {name,-56} | {detail}";
        Console.ForegroundColor = verdict == "PASS" ? ConsoleColor.Green : verdict == "FAIL" ? ConsoleColor.Red : ConsoleColor.Yellow;
        Console.WriteLine(line);
        Console.ForegroundColor = ConsoleColor.Gray;
        rpt.WriteLine(line);
        if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else info++;
    }
    Log("==============================================");
    Log(" LOCKTEST - quan he 2 duong khoa so (GUI vs API)");
    Log($" Target: {host}:{port}");
    Log($" Report: {rptPath}");
    Log("==============================================");

    var pk = LoadPackets();
    var ix = FindIndices(pk);

    // ---- helpers ----
    static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");
    List<string> SplitNums(string prefix)
    {
        int idx = prefix.IndexOf(';');
        return idx < 0 ? new List<string>()
            : prefix.Substring(idx + 1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
    string TailKey(string n)   // ban copy dong bo voi NumberLockUtil.TailKey
    {
        var sb = new StringBuilder();
        foreach (var c in n) if (c == '#' || (c >= '0' && c <= '9')) sb.Append(c);
        var d = sb.ToString();
        int i = 0; while (i < d.Length && d[i] == '#') i++;
        d = d.Substring(i);
        if (d.StartsWith("84")) d = d.Substring(2);
        while (d.Length > 0 && d[0] == '0') d = d.Substring(1);
        return d;
    }

    string? Cur()
    {
        using var s = NewSession();
        if (!s.Connect()) { Log("  (khong ket noi GET): " + s.LastError); return null; }
        var r = GetOnSession(s, pk, ix, 0);
        return r.Value;
    }

    // Sync mô phỏng 1 vòng syncer — bao gồm toàn bộ: GET -> merge -> SET -> verify -> safe-SET retry
    async Task<(bool Ok, string? Value, int Tries, string Note, string? Final)> SyncLoop(IList<string> apiNums)
    {
        var nums = apiNums.Distinct().ToList();
        if (nums.Count == 0) return (false, null, 0, "api rong", null);
        HashSet<string>? baseKeys = null;
        for (int attempt = 1; ; attempt++)
        {
            var cur = Cur();
            if (cur == null || !cur.Contains(';')) return (false, null, attempt, "khong doc duoc prefix", null);
            baseKeys ??= new HashSet<string>(SplitNums(cur).Select(TailKey));

            // BA: trạng thái API chuẩn 2 chiều;状態 đến sau thắng
            var lastByRoot = new Dictionary<string, string>();
            foreach (var x in nums) lastByRoot[TailKey(x)] = x;

            var head = cur.Substring(0, cur.IndexOf(';') + 1);
            var list = SplitNums(cur);
            var presentKeys = new HashSet<string>(list.Select(TailKey));
            var missing = lastByRoot.Where(t => !presentKeys.Contains(t.Key)).Select(t => t.Value).ToList();

            var lockNow = new List<string>();   // API khóa, server mở
            var openNow = new List<string>();   // API mở, server khóa
            for (int i = 0; i < list.Count; i++)
            {
                var sr = list[i];
                var k = TailKey(sr);
                if (k.Length == 0 || !lastByRoot.TryGetValue(k, out var tok)) continue;
                bool curLocked = false;
                {   // IsValid copy: bắt đầu "84", digit-only, length > 2
                    curLocked = !(sr.Length > 2 && sr.StartsWith("84") && sr.Substring(2).All(c => c >= '0' && c <= '9'));
                }
                bool wantLock = tok.StartsWith("#");
                if (curLocked == wantLock) continue;
                list[i] = wantLock ? "#" + sr
                                   : (sr.StartsWith("#") ? sr.Substring(sr.IndexOf('#') + 1)
                                                         : ((sr.StartsWith("84") ? sr : "84" + sr)));
                var bucket = wantLock ? lockNow : openNow;
                if (bucket.Count < 12) bucket.Add(sr);
            }

            if (missing.Count == 0 && lockNow.Count == 0 && openNow.Count == 0)
            {
                int added = nums.Count(x => !baseKeys.Contains(TailKey(x)));
                return (true, cur, attempt, added > 0 ? $"ADD {added}" : "khong doi", cur);
            }

            foreach (var it in missing) list.Add(it);
            // dedupe chuỗi nguyên văn sau biến đổi
            if (lockNow.Count > 0 || openNow.Count > 0)
            {
                var seen2 = new HashSet<string>();
                list = list.Where(x => seen2.Add(x)).ToList();
            }
            string newVal = head + string.Join(",", list);

            // Ngay truoc SET: doc lai server — khac thi ai do ghi xen → khong SET stale (cai tien TOCTOU)
            var liveNow = Cur();
            if (liveNow != cur)
            {
                Log($"  ! vong {attempt}: server doi TRUOC khi SET — bo SET stale, thu lai tren gia tri moi...");
                if (attempt >= 3) return (false, null, attempt, "MISMATCH sau 3 vong", null);
                continue;
            }

            using (var s2 = NewSession())
            {
                if (!s2.Connect()) return (false, null, attempt, "khong ket noi khi SET", null);
                var (_, rb) = await SetOnSession(s2, pk, ix, newVal);
                if (rb == newVal)
                {
                    var chk = Cur();   // xác nhận kép bằng ĐỌC LẠI
                    if (chk == newVal)
                        return (true, chk, attempt,
                            $"ADD {missing.Count} KHOA {lockNow.Count} MO {openNow.Count}", chk);
                }
            }
            Log($"  ! vong {attempt}: readback lech - thu lai (safe-SET)...");
            if (attempt >= 3) return (false, null, attempt, "MISMATCH sau 3 vong", null);
        }
    }

    // Xóa MỌI biến thể của 1 gốc số khỏi server (dọn dẹp số rác)
    async Task<bool> EnsureGone(string num)
    {
        var key = TailKey(num);
        var cur = Cur();
        if (cur == null) return false;
        var list = SplitNums(cur);
        var before = list.Count;
        list.RemoveAll(x => TailKey(x) == key);
        if (list.Count == before) return true;
        var head = cur.Substring(0, cur.IndexOf(';') + 1);
        using var s = NewSession();
        if (!s.Connect()) return false;
        var (_, rb) = await SetOnSession(s, pk, ix, head + string.Join(",", list));
        return rb == head + string.Join(",", list);
    }

    // SET có verify + retry (fix lỗi fallback template id khi live GET lỗi)
    async Task<bool> EnsureSet(string val)
    {
        for (int i = 0; i < 3; i++)
        {
            using var s = NewSession();
            if (!s.Connect()) { await Task.Delay(500); continue; }
            var (_, rb) = await SetOnSession(s, pk, ix, val);
            if (rb == val) return true;
            // In ngon ngu de biet lech o dau (prefix rat dai) — in ca chuoi rut gon 120 ky tu
            static string Clip(string? s, int n = 120) => s == null ? "?" : (s.Length <= n ? s : s.Substring(0, n) + "...(" + s.Length + "b)");
            Log($"  EnsureSet thu {i + 1}/3: LECH\n      can    = '{Clip(val)}'\n      docLai = '{Clip(rb)}'");
        }
        return false;
    }

    const string RS  = "840123456782";   // số rác có sẵn trong baseline
    const string RN1 = "840123456700";
    const string RN2 = "840123456701";
    const string RN3 = "840123456702";
    const string RN4 = "840123456703";

    // ---- STAGE 0: baseline ----
    var baseVal = Cur();
    if (baseVal == null)
    { Rec("L0", "FAIL", "Doc baseline", "khong ket noi - dung, an toan"); rpt.Dispose(); return; }
    string B = baseVal;
    var baseList = SplitNums(B);
    var baseApi = baseList.ToList();     // "API nguồn" = danh sách baseline (ạng mở như API trả bình thường)
    Rec("L0", "PASS", "Baseline", $"prefix = '{Trunc(B, 90)}' ({baseList.Count} so)");

    try
    {
        // ---- A: khóa tay sống sót qua sync (API không báo #) ----
        Log("--- A: khoa tay → sync (API khong co #) → lock phai con nguyen ---");
        {
            await EnsureGone(RS);
            // mở lại RS dạng mở (EnsureSet có verify + retry)
            var curA = Cur();
            if (curA != null)
            {
                var head = curA.Substring(0, curA.IndexOf(';') + 1);
                var l = SplitNums(curA);
                if (!l.Contains(RS)) l.Add(RS);
                await EnsureSet(head + string.Join(",", l));
            }
            // KHOA.TAY: SET #RS trực tiếp (verify + retry)
            var cur2 = Cur();
            if (cur2 != null)
            {
                var head = cur2.Substring(0, cur2.IndexOf(';') + 1);
                var l = SplitNums(cur2);
                int i = l.FindIndex(x => TailKey(x) == TailKey(RS));
                if (i >= 0) l[i] = "#" + RS; else l.Add("#" + RS);   // baseline có thể không chứa RS → thêm mới dạng khóa
                var lockedVal = head + string.Join(",", l);
                if (!await EnsureSet(lockedVal)) { Rec("LA", "FAIL", "A: khoa tay (SET khong duoc)", "lockedVal SET failed sau retry"); }
            }
            var locked = Cur();
            bool lockApplied = locked != null && SplitNums(locked).Contains("#" + RS);
            if (!lockApplied) { Rec("LA", "FAIL", "A: khoa tay (kiem tra sau SET)", "server chua co #" + RS); }
            Log($"  da khoa tay RS -> #{RS}: {lockApplied}");
            var apiNoRs = baseApi.Where(x => TailKey(x) != TailKey(RS)).ToList();   // API KHÔNG khai báo RS
            var res = await SyncLoop(apiNoRs);
            var finA = res.Final;
            bool stillLocked = finA != null && SplitNums(finA).Contains("#" + RS) && !SplitNums(finA).Contains(RS);
            Rec("LA", stillLocked && res.Ok ? "PASS" : "FAIL",
                "A: khoa tay song sot qua sync (API KHONG khai bao so)",
                $"tries={res.Tries} note='{res.Note}' lock con=#{stillLocked} value='{Trunc(finA ?? "?", 70)}'");
        }

        // ---- B: khóa tay ĐANG API trả # cùng nguồn — không được tạo dòng nào khác ----
        Log("--- B: khoa tay + API tra # cung luc → khong tao ban sao ---");
        {
            var cur = Cur();
            bool open = cur != null && SplitNums(cur).Contains(RS);
            if (open)
            {
                var head = cur!.Substring(0, cur.IndexOf(';') + 1);
                var l = SplitNums(cur); int i = l.FindIndex(x => TailKey(x) == TailKey(RS));
                if (i >= 0) l[i] = "#" + RS;
                await EnsureSet(head + string.Join(",", l));
            }
            var api = new List<string>(baseApi) { RS, "#" + RS };   // mở trước, KHÓA SAU → den sau thang = khoa
            var res = await SyncLoop(api);
            var fin = res.Final;
            var variants = fin == null ? new List<string>() : SplitNums(fin).Where(x => TailKey(x) == TailKey(RS)).ToList();
            Rec("LB", res.Ok && variants.Count == 1 && variants[0] == "#" + RS ? "PASS" : "FAIL",
                "B: dung 1 ban duy nhat (khoa) khi API tra [MO, KHOA]",
                $"variants=[{string.Join(",", variants)}] tries={res.Tries} note='{res.Note}'");
        }

        // ---- C: MỞ tay mà API vẫn # → tự khóa lại (thiết kế A - API là chuẩn) ----
        Log("--- C: mo tay → API van tra # → tu khoa lai (thiet ke A) ---");
        {
            // mở tay (EnsureSet có verify + retry — fix lỗi fallback template id)
            var cur = Cur();
            if (cur != null)
            {
                var head = cur.Substring(0, cur.IndexOf(';') + 1);
                var l = SplitNums(cur);
                int i = l.FindIndex(x => TailKey(x) == TailKey(RS));
                if (i >= 0) l[i] = RS;    // bỏ # → mở
                await EnsureSet(head + string.Join(",", l));
            }
            var mo = Cur();
            bool isOpen = mo != null && SplitNums(mo).Contains(RS);
            var api = new List<string>(baseApi) { "#" + RS };
            var res = await SyncLoop(api);
            var fin = res.Final;
            bool relocked = fin != null && SplitNums(fin).Contains("#" + RS);
            Rec("LC", isOpen && relocked ? "PASS" : "FAIL",
                "C: mo tay bi tu khoa lai boi API (API khao bao # = chuan)",
                $"mo OK={isOpen}, tu khoa lai={relocked}, tries={res.Tries} note='{res.Note}'");
        }

        // ---- C2: API báo MỞ (# trước đó) → tự MỞ theo API (chiều ngược lại) ----
        Log("--- C2: server dang khoa, API bao MO → tu mo theo API ---");
        {
            // đảm bảo server đang khóa RS
            var cur = Cur();
            if (cur != null)
            {
                var head = cur.Substring(0, cur.IndexOf(';') + 1);
                var l = SplitNums(cur);
                int i = l.FindIndex(x => TailKey(x) == TailKey(RS));
                if (i >= 0) l[i] = "#" + RS; else l.Add("#" + RS);
                await EnsureSet(head + string.Join(",", l));
            }
            var lockedNowSrv = Cur();
            bool wasLocked = lockedNowSrv != null && SplitNums(lockedNowSrv).Contains("#" + RS);
            var api = new List<string>(baseApi) { RS };            // API báo MỞ
            var res = await SyncLoop(api);
            var fin = res.Final;
            var variants = fin == null ? new List<string>() : SplitNums(fin).Where(x => TailKey(x) == TailKey(RS)).ToList();
            bool nowOpen = variants.Count == 1 && variants[0].StartsWith("84") && !variants[0].StartsWith("#");
            Rec("LC2", wasLocked && nowOpen ? "PASS" : "FAIL",
                "C2: API bao MO → tu mo (du server dang #)",
                $"wasLocked={wasLocked}, after=[{string.Join(",", variants)}], tries={res.Tries} note='{res.Note}'");
        }

        // ---- C3: cùng gốc, 2 trạng thái trong 1 payload → TRẠNG THÁI ĐẾN SAU thắng ----
        Log("--- C3: API tra [ME, KHOA] → lay trang thai den sau = KHOA ---");
        {
            var linkLock = new List<string>(baseApi) { RS, "#" + RS };   // mở trước, khóa sau
            var r1 = await SyncLoop(linkLock);
            var f1 = r1.Final;
            var v1 = f1 == null ? new List<string>() : SplitNums(f1).Where(x => TailKey(x) == TailKey(RS)).ToList();
            // đặt về mở rồi test chiều ngược
            var cur = Cur();
            if (cur != null)
            {
                var head = cur.Substring(0, cur.IndexOf(';') + 1);
                var l = SplitNums(cur);
                int i = l.FindIndex(x => TailKey(x) == TailKey(RS));
                if (i >= 0) l[i] = RS; else l.Add(RS);
                await EnsureSet(head + string.Join(",", l));
            }
            var linkOpen = new List<string>(baseApi) { "#" + RS, RS };    // khóa trước, mở sau
            var r2 = await SyncLoop(linkOpen);
            var f2 = r2.Final;
            var v2 = f2 == null ? new List<string>() : SplitNums(f2).Where(x => TailKey(x) == TailKey(RS)).ToList();

            bool lockWins = v1.Count == 1 && v1[0] == "#" + RS;
            bool openWins = v2.Count == 1 && v2[0] == RS;
            Rec("LC3", lockWins && openWins ? "PASS" : "FAIL",
                "C3: 2 trang thai cung goc → den sau thang (ca 2 chieu)",
                $"#sau=[{string.Join(",", v1)}] ok={lockWins} | mosau=[{string.Join(",", v2)}] ok={openWins}");
        }

        // ---- D: API trả cả 84… + #84… cho số CHƯA CÓ → add đúng 1 bản dạng khóa ----
        Log("--- D: API tra ca 2 dang cho so MS → add 1 ban dang khoa ---");
        {
            await EnsureGone(RN1);
            var api = new List<string>(baseApi) { RN1, "#" + RN1 };
            var res = await SyncLoop(api);
            var fin = res.Final;
            var variants = fin == null ? new List<string>() : SplitNums(fin).Where(x => TailKey(x) == TailKey(RN1)).ToList();
            Rec("LD", res.Ok && variants.Count == 1 && variants[0] == "#" + RN1 ? "PASS" : "FAIL",
                "D: add so MS dang [MO, KHOA] → 1 ban den sau = khoa",
                $"variants=[{string.Join(",", variants)}] tries={res.Tries} note='{res.Note}'");
            await EnsureGone(RN1);
        }

        // ---- E: API trả #… cho số chưa có → add SẴN dạng khóa ----
        Log("--- E: API tra # cho so chua co → add da khoa ---");
        {
            await EnsureGone(RN2);
            var api = new List<string>(baseApi) { "#" + RN2 };
            var res = await SyncLoop(api);
            var fin = res.Final;
            var variants = fin == null ? new List<string>() : SplitNums(fin).Where(x => TailKey(x) == TailKey(RN2)).ToList();
            Rec("LE", res.Ok && variants.Count == 1 && variants[0] == "#" + RN2 ? "PASS" : "FAIL",
                "E: so MS → add san dang # (khoa)",
                $"variants=[{string.Join(",", variants)}] tries={res.Tries} note='{res.Note}'");
            await EnsureGone(RN2);
        }

        // ---- F: race — số khác ghi xen giữa GET và SET → safe-SET tự hồi phục, không mất số của người khác ----
        Log("--- F: race (nguoi khac ghi xen) → safe-SET tu hoi phuc ---");
        {
            await EnsureGone(RN3);
            await EnsureGone(RN4);
            var cur0 = Cur();
            if (cur0 != null)
            {
                // 1) syncer GET + merge: RN3 thiếu → chuẩn bị newVal0 (gồm RN3)
                var head = cur0.Substring(0, cur0.IndexOf(';') + 1);
                var l0 = SplitNums(cur0); l0.Add(RN3);
                string merged0 = head + string.Join(",", l0);
                // 2) người khác ghi xen giữa: thêm RN4 (verify + retry)
                string externalVal;
                {
                    var l1 = SplitNums(cur0); l1.Add(RN4);
                    externalVal = head + string.Join(",", l1);
                    await EnsureSet(externalVal);
                }
                Log($"  da ghi RN4 vao server (nguoi khac)");
                // 3) tiếng tới cơ chế FIX: đọc SLIVE ngay trước SET — khác cur0 → chặn SET stale
                string liveNow = Cur();
                bool preChangeDetected = liveNow != cur0 && liveNow != merged0;
                Log($"  live truoc SET: {(preChangeDetected ? "KHAC gia tri da merge → chăn SET stale (the app fix)" : "khong thay doi?")}");
                // 4) safe-SET loop: merge trên danh sách MỚI (live) rồi SET
                var res = await SyncLoop(new List<string>(baseApi) { RN3 });
                var fin = res.Final;
                bool hasRn3 = fin != null && SplitNums(fin).Contains(RN3);
                bool hasRn4 = fin != null && SplitNums(fin).Contains(RN4);
                Rec("LF", preChangeDetected && res.Ok && hasRn3 && hasRn4 ? "PASS" : "FAIL",
                    "F: race → chăn SET stale + merge trên gia tri moi",
                    $"preChange detect={preChangeDetected}, RN3(mine)={hasRn3}, RN4(other)={hasRn4}, tries={res.Tries}");
            }
            else Rec("LF", "FAIL", "F: race test", "khong doc duoc prefix");
            await EnsureGone(RN3);
            await EnsureGone(RN4);
        }
    }
    finally
    {
        // ---- restore baseline ----
        Log("--- KHOI PHUC BASELINE ---");
        using (var s = NewSession())
        {
            if (s.Connect())
            {
                var (_, rb) = await SetOnSession(s, pk, ix, B);
                var fin = Cur();
                Rec("LZ", fin == B ? "PASS" : "FAIL",
                    "Khoi phuc baseline (setup giong ban dau)",
                    fin == B ? $"prefix = '{Trunc(B, 70)}'" : $"prefix = '{Trunc(fin ?? "?", 70)}' — CAN SAMet!");
            }
            else Rec("LZ", "FAIL", "Khoi phuc baseline", "khong ket noi");
        }
    }

    Log("==============================================");
    Log($" TONG KET: {pass} PASS | {fail} FAIL | {info} INFO");
    Log($" Report: {rptPath}");
    Log("==============================================");
    rpt.WriteLine();
    rpt.WriteLine($"TONG KET LOCKTEST: {pass} PASS | {fail} FAIL | {info} INFO");
    rpt.Dispose();
}


async Task RunPentest()
{
    Console.OutputEncoding = Encoding.UTF8;
    string dir = Path.Combine(AppContext.BaseDirectory, "pentest");
    Directory.CreateDirectory(dir);
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    string rptPath = Path.Combine(dir, $"pentest_report_{stamp}.txt");
    var rpt = new StreamWriter(rptPath, append: false, Encoding.UTF8) { AutoFlush = true };
    int pass = 0, fail = 0, info = 0;
    var passSave = Console.Out;

    void Rec(string id, string verdict, string name, string detail)
    {
        var line = $"[{id,-5}] {verdict,-5} | {name,-52} | {detail}";
        Console.ForegroundColor = verdict == "PASS" ? ConsoleColor.Green : verdict == "FAIL" ? ConsoleColor.Red : ConsoleColor.Yellow;
        Console.WriteLine(line);
        Console.ForegroundColor = ConsoleColor.Gray;
        rpt.WriteLine(line);
        if (verdict == "PASS") pass++;
        else if (verdict == "FAIL") fail++;
        else info++;
    }

    Log("==============================================");
    Log(" PENTEST VOS3000 PREFIX MAPPING qua TCP 1202");
    Log($" Target: {host}:{port}   Mapping: 'cts'");
    Log($" Session: {sessionPath}");
    Log($" Mode: " + (quick ? "QUICK (bo qua test replay dai)" : "FULL"));
    Log($" Report: {rptPath}");
    Log("==============================================");

    var pk = LoadPackets();
    var ix = FindIndices(pk);
    if (ix.Hello < 0 || ix.Login < 0 || ix.MapGet < 0 || ix.Set < 0)
    {
        Rec("T0.0", "FAIL", "Load session/template", "thieu packet: hello login GET SET");
        rpt.Dispose(); return;
    }
    Rec("T0.0", "PASS", "Load session/template", $"hello#{ix.Hello} login#{ix.Login} GET#{ix.MapGet} SET#{ix.Set}");

    // STAGE A - baseline
    var baseR = ReadBaseline(pk, ix);
    string? B = baseR.Value;
    if (B == null)
    {
        Rec("T0.1", "FAIL", "Baseline: doc prefix tu server", "khong doc duoc - ket thuc pentest de an toan");
        rpt.Dispose(); return;
    }
    Rec("T0.1", "PASS", "Baseline: doc prefix tu server", $"calloutCallerPrefixes = '{B}' (strings: {string.Join(" ; ", baseR.All)})");

    // STAGE B - protocol guard (khong thay doi trang thai)
    Log("--- STAGE B: bao ve protocol ---");

    // B1: gui hello BAT 4 byte zero -> ky vong RST
    using (var s = NewSession())
    {
        bool conn = s.Connect();
        List<byte[]> resp = new();
        if (conn) resp = s.SendRaw(pk[ix.Hello], 1500);
        bool closed = !s.IsAlive;
        bool hasGreet = resp.Any(r => VosSession.ContainsAscii(r, "MBX3000"));
        Rec("PB1", closed || !hasGreet ? "PASS" : "FAIL", "Gui hello THIEU 4 byte zero",
            closed ? "server dong/RST ket noi - thiet ke dung"
                   : hasGreet ? "KHONG bao ve - van tra greeting!" : "khong phan hoi (co the an toan)");
    }

    // B2: gui rac 64 byte
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var rnd = new Random(42);
            var junk = new byte[64]; rnd.NextBytes(junk);
            var resp = s.SendRaw(junk, 1200);
            bool closed = !s.IsAlive;
            Rec("PB2", closed ? "PASS" : "INFO", "Gui 64 byte rac (khong co zero-prefix)",
                closed ? "server RST ngay - ro rang giao thuc bi bao ve" : "server lang nghe im lang (co the an toan)");
        }
        else Rec("PB2", "FAIL", "Gui 64 byte rac", "khong ket noi duoc: " + s.LastError);
    }

    // B3: hello dung protocol -> greeting MBX3000
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var resp = s.SendPacket(pk[ix.Hello], 1500);
            bool hasGreet = resp.Any(r => VosSession.ContainsAscii(r, "MBX3000"));
            Rec("PB3", hasGreet ? "PASS" : "FAIL", "Hello dung protocol -> greeting",
                hasGreet ? "server ban MBX3000 greeting" : "khong thay 'MBX3000' in greeting (resp=" + resp.Count + " chunk)");
        }
        else Rec("PB3", "FAIL", "Hello dung protocol", "khong ket noi duoc");
    }

    // B4: header magic hong -> doc dong
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var bad = (byte[])pk[ix.Hello].Clone();
            bad[22] ^= 0x03;                    // vibe byte trong magic
            var resp = s.SendPacket(bad, 1500);
            bool closed = !s.IsAlive;
            Rec("PB4", closed ? "PASS" : "INFO", "Header magic bi hong",
                closed ? "server RST - khong giai nen du lieu le" : "khong phan hoi (an toan)");
        }
        else Rec("PB4", "FAIL", "Header magic bi hong", "khong ket noi duoc");
    }
    // Sau B4 can xac nhan server van binh thuong
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var r = GetOnSession(s, pk, ix, 0);
            Rec("PB5", r.Value == B ? "PASS" : "FAIL", "Server tinh tao sau cac test bao ve", $"prefix = '{r.Value ?? "?"}' (mag baseline '{B}')");
        }
        else Rec("PB5", "FAIL", "Server tinh tao sau cac test bao ve", "khong ket noi: " + s.LastError);
    }

    // STAGE C - doc
    Log("--- STAGE C: kha nang doc ---");
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var r = GetOnSession(s, pk, ix, 1);
            Rec("PC1", r.Value == B ? "PASS" : "INFO", "Flow FULL (replay 0..GET) doc prefix",
                $"prefix = '{r.Value ?? "?"}', strings = {string.Join(" ; ", r.All)}");
        }
        else Rec("PC1", "FAIL", "Flow FULL doc prefix", "khong ket noi");
    }

    if (!quick)
    {
        // replay day du nhung TAM DUNG truoc SET, sau do confirm khong ghi
        Log("PC2: replay DRY day du (co dinh mai mot chut)...");
        using (var s = NewSession())
        {
            if (s.Connect())
            {
                bool ok = true;
                for (int i = 0; i < ix.Set; i++)
                {
                    s.SendPacket(pk[i], 700);
                    if (!s.IsAlive) { ok = false; break; }
                }
                s.Close();
            }
        }
        using (var s = NewSession())
        {
            PrefixRead r = PrefixRead.Empty();
            if (s.Connect()) r = GetOnSession(s, pk, ix, 0);
            Rec("PC2", r.Value == B ? "PASS" : "FAIL", "Replay DRY khong thay doi du lieu",
                r.Value == B ? $"prefix khong doi ('{B}')" : $"prefix = '{r.Value ?? "?"}' != baseline '{B}'");
        }
    }

    // STAGE D - viet + verify
    Log("--- STAGE D: viet prefix (den dong se khoi phuc baseline) ---");

    async Task<(bool Sent, string? Rb)> WriteAndVerifyAsync(string v)
    {
        using var s = NewSession();
        if (!s.Connect()) { Log("      ko connect: " + s.LastError); return (false, null); }
        var r = await SetOnSession(s, pk, ix, v);
        s.Close();
        return r;
    }
    void TestWrite(string id, string v, bool require)
    {
        var (sent, rb) = WriteAndVerifyAsync(v).GetAwaiter().GetResult();
        if (!require && rb != null && rb != v)
        {
            Rec(id, "INFO", $"SET '{(v.Length > 40 ? v.Substring(0, 40) + "..." : v)}'", $"server TU CHOI (an toan) - gia tri giu nguyen '{rb}'");
            return;
        }
        if (rb == v) Rec(id, "PASS", $"SET '{(v.Length > 40 ? v.Substring(0, 40) + "..." : v)}'", "ghi thanh cong, GET readback khop");
        else Rec(id, "FAIL", $"SET '{(v.Length > 40 ? v.Substring(0, 40) + "..." : v)}'", sent ? $"readback '{rb ?? "?"}' != gia tri ghi" : "khong gui duoc / khong phan hoi");
    }

    TestWrite("PD1", "1;0901", true);
    TestWrite("PD2", "1;0088,0889,0901,0912,0999,123456", true);
    TestWrite("PD3", "1;" + string.Join(",", Enumerable.Range(0, 10).Select(i => $"0{i}{i}123456789")), true);
    TestWrite("PD4", "1;0901,+86ABCtest*", false);
    TestWrite("PD5", "1;", false);

    // idempotence + lap nhanh
    Log("PD6/PD7: idempotence + lap nhanh...");
    string vA = "1;0901,0912", vB2 = "1;0999";
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var (s1, r1) = await SetOnSession(s, pk, ix, vA);
            var (s2, r2) = await SetOnSession(s, pk, ix, vA);
            Rec("PD6", s1 && s2 && r1 == vA && r2 == vA ? "PASS" : "FAIL", "SET cung gia tri 2 lan lien tiep (idempotence)",
                $"lan1='{r1}', lan2='{r2}'");
            var ok = true; var det = "";
            for (int k = 0; k < 3; k++)
            {
                var (_, ra) = await SetOnSession(s, pk, ix, vA);
                var (_, rb2) = await SetOnSession(s, pk, ix, vB2);
                if (ra != vA || rb2 != vB2) { ok = false; det += $" (vong {k + 1}: '{ra}'/'{rb2}')"; }
            }
            Rec("PD7", ok ? "PASS" : "FAIL", "3 vong lap nhanh SET A->B->A", ok ? "6 lan SET/GET deu dung" : "loi" + det);
        }
        else { Rec("PD6", "FAIL", "SET idempotence", "khong ket noi"); Rec("PD7", "FAIL", "Lap nhanh SET/GET", "khong ket noi"); }
    }

    // khoi phuc baseline
    var (sr, rbr) = await WriteAndVerifyAsync(B);
    Rec("PD8", rbr == B ? "PASS" : "FAIL", "Khoi phuc baseline", rbr == B ? $"da tra lai '{B}'" : $"readback '{rbr ?? "?"}' != '{B}' - YEU CAU KIEM TRA!");

    // STAGE E - negative (khong ghi du lieu)
    Log("--- STAGE E: test xam pha ---");

    // E1: SET bi cat dut 60% -> khong duoc coi la ghi thanh cong
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var setPkt = VosProto.BuildSetPacket(pk[ix.Set], "1;0901", null);
            if (setPkt != null)
            {
                var cut = new byte[setPkt.Length * 6 / 10];
                Array.Copy(setPkt, cut, cut.Length);
                var wire = VosProto.FrameWire(cut);
                s.SendRaw(wire, 1500);
            }
            bool closed = !s.IsAlive;
        }
    }
    using (var s = NewSession())
    {
        PrefixRead r = PrefixRead.Empty();
        if (s.Connect()) r = GetOnSession(s, pk, ix, 0);
        var expect = B;   // sau PD8, gia tri la baseline
        Rec("PE1", r.Value == expect ? "PASS" : "FAIL", "SET bi cat dut 60% khong ghi vao server",
            r.Value == expect ? $"prefix giu nguyen '{expect}'" : $"prefix = '{r.Value ?? "?"}' != mong doi '{expect}'");
    }

    // E2: SET voi chuoi prefix do dai 0 (fuzz)
    using (var s = NewSession())
    {
        if (s.Connect())
        {
            var setPkt = VosProto.BuildSetPacket(pk[ix.Set], "", null);
            if (setPkt != null)
            {
                var resp = s.SendPacket(setPkt, 1500);
                Log($"E2: SET prefix rong -> {resp.Count} chunk phan hoi, alive={s.IsAlive}");
            }
        }
    }
    // khoi phuc lai sau E2 va xac nhan cuoi cung
    var (srr, rbrr) = await WriteAndVerifyAsync(B);
    Rec("PE2", rbrr == B ? "PASS" : "FAIL", "Fuzz SET chuoi rong + khoi phuc",
        rbrr == B ? $"trang thai on dinh, baseline '{B}' duoc giu" : $"trang thai lech: '{rbrr ?? "?"}'");

    // STAGE F - so sanh ket qua Cuoi cung
    using (var s = NewSession())
    {
        PrefixRead r = PrefixRead.Empty();
        if (s.Connect()) r = GetOnSession(s, pk, ix, 0);
        Rec("PF1", r.Value == B ? "PASS" : "FAIL", "TRANG THAI CUOI: giong baseline",
            r.Value == B ? $"prefix = '{B}' - hoan toan khoi phuc" : $"prefix = '{r.Value ?? "?"}' - CAN SAMet!");
    }

    Log("==============================================");
    Log($" TONG KET: {pass} PASS | {fail} FAIL | {info} INFO");
    Log($" Report chi tiet: {rptPath}");
    Log("==============================================");
    rpt.WriteLine();
    rpt.WriteLine($"TONG KET: {pass} PASS | {fail} FAIL | {info} INFO");
    rpt.Dispose();
}

// Ket qua doc prefix tu server
class PrefixRead
{
    public string? Value;
    public List<string> All = new();
    public List<byte[]> Objects = new();
    public static PrefixRead Empty() => new();
}
