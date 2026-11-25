// ============================================================
//  VosSession - Quan ly ket noi TCP 1202, gui/nhan packet
// ============================================================
using System.Net.Sockets;
using System.Text;

// global namespace de top-level trong Program.cs su dung truc tiep
public class VosSession : IDisposable
{
    public readonly string Host;
    public readonly int Port;
    readonly Action<string> _log;
    TcpClient? _cli;
    NetworkStream? _net;
    bool _dead;
    public string LastError = "";

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

    /// Doc tat ca phan hoi trong cua so settleMs.
    /// Tra ve: cac object decompress duoc (gzip), va/hoac 1 chunk raw.
    public List<byte[]> ReadAvailable(int settleMs)
    {
        var got = new List<byte[]>();
        if (_net == null) return got;
        var pend = new MemoryStream();
        var chunk = new byte[65536];
        var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            try { n = _net.Read(chunk, 0, chunk.Length); }
            catch { break; }
            if (n <= 0) { _dead = true; break; }
            pend.Write(chunk, 0, n);
            var arr = pend.ToArray();
            if (VosProto.TryFindDecompress(arr, out var obj))
            {
                got.Add(obj);
                pend.SetLength(0);
            }
        }
        var rest = pend.ToArray();
        if (rest.Length > 0)
        {
            if (VosProto.TryFindDecompress(rest, out var o2)) got.Add(o2);
            else got.Add(rest);
        }
        return got;
    }

    /// Gui packet dung protocol (4 zero + frame). Tra ve cac object/chunk nhan duoc.
    public List<byte[]> SendPacket(byte[] pkt, int settleMs = 1200)
    {
        if (_net == null) return new List<byte[]>();
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

    /// Gui du lieu RAW bat ky (khong them 4 zero) - dung cho test xa cong.
    public List<byte[]> SendRaw(byte[] bytes, int settleMs = 1200)
    {
        if (_net == null) return new List<byte[]>();
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

    public void Close()
    {
        try { _cli?.Close(); } catch { }
        _cli = null; _net = null;
    }

    public void Dispose() => Close();

    /// Tim trong chunk raw (khong gzip) xem co chuoi ASCII con tro.
    public static bool ContainsAscii(byte[] data, string needle)
    {
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
