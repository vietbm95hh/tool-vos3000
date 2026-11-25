// ============================================================
//  VosSession - Quan ly ket noi TCP 1202, gui/nhan packet
//
//  Wire format (xem PROTOCOL.md):
//    - Client -> server: [4 byte 0][36 byte header][gzip payload]
//    - Server -> client: [36 byte header][gzip payload]  (KHONG co 4 byte 0)
//      Header: 20 byte dau la session/seq/timestamp (KHONG phai 0),
//              magic tai offset 20..27, len(payload) tai 32..35 (big-endian)
//    - Ngay sau hello, server con ban greeting dang TEXT (khong phai packet)
//
//  Doc response theo DUNG KHUNG DO DAI (khong doan gzip):
//    - giu buffer nhan (_rx) giua cac lan doc -> khong mat frame khi timeout
//    - tra ve TAT CA frame trong 1 lan doc (truoc day chi lay frame dau)
//    - "settleMs" = thoi gian cho BYTE DAU TIEN; sau khi co du lieu chi
//      cho them 1 khoang IM LANG (idle) -> server cham / danh sach dai
//      van doc du, khong bi cat giua frame
//    - chi tach khung 1 lan khi da doc xong (buffer luc do la "chot"),
//      nen header bi chia nho giua 2 lan doc van duoc ghep lai
// ============================================================
using System.Net.Sockets;
using System.Text;

public class VosSession : IDisposable
{
    public const int MaxPayload = 64 * 1024 * 1024;

    public readonly string Host;
    public readonly int Port;
    readonly Action<string> _log;
    TcpClient? _cli;
    NetworkStream? _net;
    bool _dead;
    public string LastError = "";

    // Buffer nhan tich luy: co the con 1 phan frame khi het thoi gian cho
    readonly MemoryStream _rx = new();

    // Thong ke lan doc gan nhat (dung cho log chan doan)
    public int LastFrameCount;
    public int LastBadFrameCount;
    public long LastByteCount;
    public readonly List<byte[]> LastRawChunks = new();   // greeting / du lieu khong phai packet

    public int PendingBytes => (int)_rx.Length;

    public VosSession(string host, int port, Action<string>? log = null)
    {
        Host = host; Port = port;
        _log = log ?? (_ => { });
    }

    public bool Connect()
    {
        try
        {
            _cli = new TcpClient();
            _cli.Connect(Host, Port);
            _net = _cli.GetStream();
            _net.ReadTimeout = 500;
            _dead = false;
            _rx.SetLength(0);
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _cli = null;
            return false;
        }
    }

    public bool IsAlive => _cli is { Connected: true } && _net != null && !_dead;

    /// <summary>
    /// Doc toan bo phan hoi. Tra ve TAT CA object gzip da giai nen (moi frame = 1 object).
    /// settleMs = thoi gian cho byte dau tien; sau do chi cho them idle (600..1500ms).
    /// Frame dang do cuoi cung duoc GIU LAI trong _rx cho lan doc sau.
    /// </summary>
    public List<byte[]> ReadAvailable(int settleMs)
    {
        var got = new List<byte[]>();
        LastFrameCount = 0;
        LastBadFrameCount = 0;
        LastByteCount = 0;
        LastRawChunks.Clear();

        if (_net == null) return got;

        int firstWait = Math.Max(settleMs, 300);
        int idleWait = Math.Clamp(settleMs / 2, 600, 1500);
        int partialWait = Math.Max(settleMs, 3000);
        int hardCap = Math.Clamp(settleMs * 8, 15000, 120000);

        var startAt = DateTime.UtcNow;
        var hardStop = startAt.AddMilliseconds(hardCap);
        var lastData = startAt;
        bool gotAny = false;

        var buf = new byte[64 * 1024];

        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= hardStop) break;

            bool partial = HasIncompleteFrame();
            var sinceData = now - lastData;

            if (!gotAny)
            {
                if (sinceData >= TimeSpan.FromMilliseconds(firstWait)) break;
            }
            else if (partial)
            {
                // co nua frame: doi them de ghep cho du (khong doi mai)
                if (sinceData >= TimeSpan.FromMilliseconds(partialWait)) break;
            }
            else if (sinceData >= TimeSpan.FromMilliseconds(idleWait)) break;

            int wait = !gotAny ? firstWait : (partial ? Math.Min(idleWait, 1000) : idleWait);
            var remain = (int)Math.Max(1, (hardStop - now).TotalMilliseconds);
            try { _net.ReadTimeout = Math.Max(1, Math.Min(wait, remain)); } catch { }

            int n;
            try { n = _net.Read(buf, 0, buf.Length); }
            catch (IOException e)
            {
                // ReadTimeout chi la "chua co byte moi", KHONG phai ket thuc response.
                // Truoc day break o day lam mat frame khi server nghi > 1s giua 2 dot.
                if (IsSocketTimeout(e)) continue;
                _dead = true;
                break;
            }
            catch (ObjectDisposedException) { _dead = true; break; }
            catch { _dead = true; break; }

            if (n <= 0) { _dead = true; break; }

            _rx.Write(buf, 0, n);
            LastByteCount += n;
            lastData = DateTime.UtcNow;
            gotAny = true;
        }

        // Da doc xong -> tach khung 1 lan tren buffer "chot"
        DrainFrames(got);
        return got;
    }

    /// <summary>Gui packet dung protocol (4 zero + frame). Tra ve cac object nhan duoc.</summary>
    public List<byte[]> SendPacket(byte[] pkt, int settleMs = 1200)
    {
        if (_net == null) return new List<byte[]>();

        // Neu response truoc con nua frame thi bo (khong tron voi response moi)
        if (_rx.Length > 0)
        {
            _log($"  (bo {_rx.Length} byte du cua response truoc)");
            _rx.SetLength(0);
        }

        try
        {
            var wire = VosProto.FrameWire(pkt);
            _net.Write(wire, 0, wire.Length);
            _net.Flush();
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _dead = true;
            return new List<byte[]>();
        }
        return ReadAvailable(settleMs);
    }

    /// <summary>Gui du lieu RAW bat ky (khong them 4 zero) - dung cho test xa cong.</summary>
    public List<byte[]> SendRaw(byte[] bytes, int settleMs = 1200)
    {
        if (_net == null) return new List<byte[]>();
        if (_rx.Length > 0) _rx.SetLength(0);
        try
        {
            _net.Write(bytes, 0, bytes.Length);
            _net.Flush();
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _dead = true;
            return new List<byte[]>();
        }
        return ReadAvailable(settleMs);
    }

    // ---------- tach khung packet ----------

    /// <summary>Cat cac frame hoan chinh ra khoi _rx. Junk (greeting) vao LastRawChunks.</summary>
    void DrainFrames(List<byte[]> got)
    {
        int total = (int)_rx.Length;
        if (total == 0) return;

        var data = _rx.ToArray();
        int pos = 0;

        while (true)
        {
            int start = FindHeaderStart(data, data.Length, pos);
            if (start < 0)
            {
                int keep = PartialHeaderTail(data, data.Length, pos);
                int junkEnd = data.Length - keep;
                // Ghi ca phan giu lai vao LastRawChunks de chan doan (greeting co the
                // ket thuc bang "MBX3000" nam trong 27 byte cuoi), nhung _rx van giu
                // phan cuoi de ghep tiep neu do la dau header bi cat.
                if (data.Length > pos) LastRawChunks.Add(Slice(data, pos, data.Length - pos));
                pos = junkEnd;
                break;
            }

            if (start > pos)
            {
                LastRawChunks.Add(Slice(data, pos, start - pos));
                pos = start;
            }

            if (data.Length - pos < VosProto.HeaderLen) break;     // thieu header

            int len = VosProto.ReadInt32BE(data, pos + 32);
            if (len < 0 || len > MaxPayload) { pos++; continue; }  // header rac
            if (len == 0) { pos += VosProto.HeaderLen; continue; } // khong co payload

            if (data.Length - pos < VosProto.HeaderLen + 2) break;
            // payload cua packet that luon la gzip -> loai magic gia nam trong du lieu
            if (data[pos + VosProto.HeaderLen] != 0x1F || data[pos + VosProto.HeaderLen + 1] != 0x8B)
            { pos++; continue; }

            if (data.Length - pos < VosProto.HeaderLen + len) break;   // thieu payload

            var pkt = Slice(data, pos, VosProto.HeaderLen + len);
            if (VosProto.TryDecompressFrom(pkt, VosProto.HeaderLen, out var obj))
            {
                got.Add(obj);
                LastFrameCount++;
            }
            else
            {
                LastBadFrameCount++;
            }
            pos += pkt.Length;
        }

        int remain = data.Length - pos;
        _rx.SetLength(0);
        if (remain > 0) _rx.Write(data, pos, remain);
    }

    /// <summary>
    /// Buffer co frame CUOI chua du (header/payload) khong?
    /// Phai di qua cac frame da du o dau buffer: truoc day chi xet frame dau tien nen
    /// khi buffer = [frame1 da du][frame2 moi co 23 byte] no tra ve false -> cat som.
    /// </summary>
    bool HasIncompleteFrame()
    {
        int len = (int)_rx.Length;
        if (len == 0) return false;
        var data = _rx.GetBuffer();

        int pos = 0;
        while (true)
        {
            int start = FindHeaderStart(data, len, pos);
            if (start < 0) return PartialHeaderTail(data, len, pos) > 0;

            if (len - start < VosProto.HeaderLen) return true;

            int plen = VosProto.ReadInt32BE(data, start + 32);
            if (plen < 0 || plen > MaxPayload) { pos = start + 1; continue; }  // magic gia
            if (plen == 0) { pos = start + VosProto.HeaderLen; continue; }

            if (len - start < VosProto.HeaderLen + plen) return true;         // payload chua du

            pos = start + VosProto.HeaderLen + plen;                          // frame da du
        }
    }

    static bool IsSocketTimeout(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
        {
            if (x is TimeoutException) return true;
            if (x is SocketException se &&
                (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock))
                return true;
        }
        return false;
    }

    /// <summary>Tra ve offset bat dau header packet (magic nam o header+20). -1 neu khong co.</summary>
    static int FindHeaderStart(byte[] data, int len, int from)
    {
        int i0 = Math.Max(0, from) + 20;
        for (int i = i0; i + 8 <= len; i++)
            if (VosProto.HasMagic(data, i)) return i - 20;
        return -1;
    }

    /// <summary>
    /// So byte cuoi buffer co the la PHAN DAU cua 1 header.
    ///
    /// Header server -> client KHONG bat dau bang 20 byte 0 (khac header client -> server):
    ///   [0..19]  = session / sequence / timestamp (gia tri bat ky)
    ///   [20..27] = magic
    ///   [28..31] = tick
    ///   [32..35] = len(payload)
    /// Vi vay khong the nhan dien dau header bang cac byte 0.
    ///
    /// Neu magic da du 8 byte thi FindHeaderStart da tim ra header. Khi khong tim thay
    /// magic, header chi co the bi cat trong 20 byte truoc magic + toi da 7 byte magic
    /// => giu lai toi da 27 byte cuoi, phan truoc do moi la junk that su.
    /// </summary>
    static int PartialHeaderTail(byte[] data, int len, int pos)
    {
        int n = len - pos;
        if (n <= 0) return 0;
        return Math.Min(n, 20 + VosProto.Magic.Length - 1);
    }

    static byte[] Slice(byte[] b, int off, int len)
    {
        var r = new byte[len];
        Array.Copy(b, off, r, 0, len);
        return r;
    }

    public void Close()
    {
        try { _cli?.Close(); } catch { }
        _cli = null; _net = null;
        _rx.SetLength(0);
    }

    public void Dispose() => Close();

    /// <summary>Tim trong chunk raw (khong gzip) xem co chuoi ASCII con tro.</summary>
    public static bool ContainsAscii(byte[] data, string needle)
    {
        if (data == null) return false;
        var nb = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + nb.Length <= data.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < nb.Length; j++)
                if (data[i + j] != nb[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}