// ============================================================
//  TestCustomer - thử đọc danh sách customer từ VOS3000 (TCP 1202)
//
//  Mục đích: xác định packet FilterCustomer nào cho server trả về
//  danh sách customer thật. Gửi nhiều biến thể filter, in kết quả.
//
//  Dùng:
//    dotnet run --project TestCustomer -- [ip] [port] [agentAccount]
//
//  Mặc định: 172.29.101.32 1202 "" (rỗng = lấy tất cả)
// ============================================================
using System.Text;

var host = args.Length > 0 ? args[0] : "172.29.101.32";
var port = args.Length > 1 ? int.Parse(args[1]) : 1202;
var agentOverride = args.Length > 2 ? args[2] : null;

var sessionFile = ResolveSession();
Console.WriteLine($"Host   : {host}:{port}");
Console.WriteLine($"Session: {sessionFile}");
Console.WriteLine();

var pk = VosProto.SplitPackets(File.ReadAllBytes(sessionFile));
int helloIdx = -1, loginIdx = -1, filterIdx = -1;
for (int i = 0; i < pk.Count; i++)
{
    if (helloIdx < 0 && !VosProto.IsGzip(pk[i])) { helloIdx = i; continue; }
    if (!VosProto.TryDecompressFrom(pk[i], VosProto.HeaderLen, out var o)) continue;
    var t = Encoding.UTF8.GetString(o);
    if (loginIdx < 0 && t.Contains("loginName")) loginIdx = i;
    if (filterIdx < 0 && t.Contains("FilterCustomer") && t.Contains("InfoCustomer")) filterIdx = i;
}
Console.WriteLine($"packet: hello#{helloIdx} login#{loginIdx} FilterCustomer#{filterIdx}\n");

VosProto.TryDecompressFrom(pk[filterIdx], VosProto.HeaderLen, out var filterObj);

// ---- vị trí agentAccount trong FilterCustomer (chuỗi "cts") ----
int agentPos = FindAgentAccountSlot(filterObj);
Console.WriteLine($"agentAccount slot = {agentPos} " +
    (agentPos >= 0 ? $"(hiện tại '{Encoding.UTF8.GetString(filterObj, agentPos + 3, (filterObj[agentPos + 1] << 8) | filterObj[agentPos + 2])}')" : "(không tìm thấy)"));
Console.WriteLine();

var variants = new List<(string Name, byte[] Obj)>
{
    ("A. giữ nguyên (agent cts)", filterObj),
};

if (agentPos >= 0)
{
    // B: agentAccount = null (0x70). Dựng lại object, KHÔNG giữ reference cũ —
    //    cách an toàn: thay 6 byte "74 00 03 cts" bằng "70 70 70 70 70 70" rồi thử;
    //    nếu hỏng, server sẽ trả rỗng/lỗi chứ không treo.
    var b = (byte[])filterObj.Clone();
    b[agentPos] = 0x70;
    for (int i = agentPos + 1; i < agentPos + 6; i++) b[i] = 0x70;
    variants.Add(("B. agentAccount=null (thay 6 byte)", b));

    // C: agentAccount = "" (chuỗi rỗng) — cùng độ dài ngắn hơn, giữ 4 byte đệm
    var c = (byte[])filterObj.Clone();
    c[agentPos] = 0x74; c[agentPos + 1] = 0x00; c[agentPos + 2] = 0x00;
    for (int i = agentPos + 3; i < agentPos + 6; i++) c[i] = 0x70;
    variants.Add(("C. agentAccount=\"\" (rỗng)", c));
}

if (!string.IsNullOrEmpty(agentOverride))
{
    var d = MakeAgent(filterObj, agentPos, agentOverride);
    if (d != null) variants.Add(($"D. agentAccount='{agentOverride}'", d));
}

foreach (var (name, obj) in variants)
{
    Console.WriteLine(new string('=', 70));
    Console.WriteLine($"BIẾN THỂ {name}  (object {obj.Length} B)");
    Console.WriteLine(new string('=', 70));
    RunProbe(host, port, pk, helloIdx, loginIdx, obj);
    Console.WriteLine();
}

static void RunProbe(string host, int port, List<byte[]> pk, int helloIdx, int loginIdx, byte[] filterObj)
{
    // dùng header của packet FilterCustomer gốc
    var filterPkt = pk.First(p =>
    {
        if (!VosProto.TryDecompressFrom(p, VosProto.HeaderLen, out var o)) return false;
        var t = Encoding.UTF8.GetString(o);
        return t.Contains("FilterCustomer") && t.Contains("InfoCustomer");
    });
    var hdr = new byte[VosProto.HeaderLen];
    Array.Copy(filterPkt, hdr, VosProto.HeaderLen);

    var pkt = VosProto.BuildPacket(hdr, VosProto.CompressGzip(filterObj));

    using var s = new VosSession(host, port, m => Console.WriteLine("  " + m));
    if (!s.Connect()) { Console.WriteLine($"  !! không kết nối được: {s.LastError}"); return; }

    var hello = s.SendPacket(pk[helloIdx], 900);
    Console.WriteLine($"  hello -> {hello.Count} gói");
    var login = s.SendPacket(pk[loginIdx], 1200);
    Console.WriteLine($"  login -> {login.Count} gói");

    var objs = s.SendPacket(pkt, 2500);
    objs.AddRange(s.ReadAvailable(1500));

    Console.WriteLine($"  FilterCustomer -> {objs.Count} gói từ server");
    int total = 0;
    foreach (var o in objs)
    {
        bool has = VosSession.ContainsAscii(o, "InfoCustomer");
        Console.WriteLine($"    {o.Length,7} B  InfoCustomer={(has ? "CÓ" : "không")}  head='{Head(o)}'");
        if (has) total += CountCustomerObjects(o);
    }
    Console.WriteLine($"  => ước tính {total} object customer");
}

static int CountCustomerObjects(byte[] buf)
{
    var needle = Encoding.ASCII.GetBytes("com.kunshi.interfacef.vos2000.InfoCustomer");
    int n = 0;
    for (int i = 0; i + 3 + needle.Length <= buf.Length; i++)
    {
        if (buf[i] != 0x72) continue;
        int l = (buf[i + 1] << 8) | buf[i + 2];
        if (l != needle.Length) continue;
        bool ok = true;
        for (int j = 0; j < l; j++) if (buf[i + 3 + j] != needle[j]) { ok = false; break; }
        if (ok) n++;
    }
    return n;
}

static string Head(byte[] o)
{
    var sb = new StringBuilder();
    var txt = Encoding.UTF8.GetString(o);
    foreach (var ch in txt)
    {
        if (ch >= 32 && ch < 127) sb.Append(ch);
        if (sb.Length >= 90) break;
    }
    return sb.ToString();
}

/// Tìm chuỗi "cts" (agentAccount) trong FilterCustomer: pattern 0x74 0x00 0x03 'cts'
static int FindAgentAccountSlot(byte[] obj)
{
    for (int i = 0; i + 6 <= obj.Length; i++)
        if (obj[i] == 0x74 && obj[i + 1] == 0x00 && obj[i + 2] == 0x03 &&
            obj[i + 3] == 'c' && obj[i + 4] == 't' && obj[i + 5] == 's')
            return i;
    return -1;
}

static byte[] MakeAgent(byte[] obj, int slot, string value)
{
    if (slot < 0) return null;
    var nb = Encoding.UTF8.GetBytes(value);
    if (nb.Length != 3) { Console.WriteLine($"  (bỏ qua D: chỉ hỗ trợ chuỗi 3 ký tự, bạn nhập {nb.Length})"); return null; }
    var r = (byte[])obj.Clone();
    r[slot] = 0x74; r[slot + 1] = 0x00; r[slot + 2] = 0x03;
    Array.Copy(nb, 0, r, slot + 3, 3);
    return r;
}

static string ResolveSession()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data", "vos_session_capture.bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vos3000AgentCSharp", "Data", "vos_session_capture.bin"),
        Path.Combine(AppContext.BaseDirectory, "Data", "vos_session_capture.bin"),
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (File.Exists(full)) return full;
    }
    throw new FileNotFoundException("Không tìm thấy vos_session_capture.bin. Chạy từ thư mục project.");
}
