
using System.IO.Compression;
using System.Text;

// global namespace de top-level trong Program.cs su dung truc tiep
public class VosProto
{
    public const int HeaderLen = 36;
    static readonly byte[] Magic = { 0x00, 0x00, 0x03, 0xFC, 0xFF, 0xFF, 0xFC, 0x03 };

    public static bool HasMagic(byte[] b, int pos)
    {
        if (pos + 8 > b.Length) return false;
        for (int i = 0; i < 8; i++) if (b[pos + i] != Magic[i]) return false;
        return true;
    }

    public static int ReadInt32BE(byte[] b, int off) => (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

    public static void WriteInt32BE(byte[] b, int off, int v)
    {
        unchecked
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }
    }

    /// Tach chuoi packet tu file raw: tra ve danh sach (36 hdr + payload).
    public static List<byte[]> SplitPackets(byte[] raw)
    {
        var list = new List<byte[]>();
        int pos = 0;
        while (pos + HeaderLen <= raw.Length)
        {
            if (!HasMagic(raw, pos + 20)) { pos++; continue; }
            int len = ReadInt32BE(raw, pos + 32);
            if (len < 0 || pos + HeaderLen + len > raw.Length) { pos++; continue; }
            var pkt = new byte[HeaderLen + len];
            Array.Copy(raw, pos, pkt, 0, HeaderLen + len);
            list.Add(pkt);
            pos += HeaderLen + len;
        }
        return list;
    }

    /// Tra ve packet (36 hdr + payload) tai vi tri bat dau khop magic. null neu khong hop le.
    public static byte[] PacketAt(byte[] raw, int i)
    {
        if (raw == null || i < 0 || i + HeaderLen + 3 > raw.Length) return null;
        if (!HasMagic(raw, i + 20)) return null;
        int len = ReadInt32BE(raw, i + 32);
        if (len < 0 || i + HeaderLen + len > raw.Length) return null;
        var pkt = new byte[HeaderLen + len];
        Array.Copy(raw, i, pkt, 0, pkt.Length);
        return pkt;
    }

    public static bool IsGzip(byte[] pkt)
    {
        return pkt.Length > HeaderLen + 3 && pkt[HeaderLen] == 0x1F && pkt[HeaderLen + 1] == 0x8B && pkt[HeaderLen + 2] == 0x08;
    }

    public static byte[] CompressGzip(byte[] obj)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
            gz.Write(obj, 0, obj.Length);
        return ms.ToArray();
    }

    public static byte[] BuildPacket(byte[] hdr36, byte[] gzip)
    {
        var pkt = new byte[HeaderLen + gzip.Length];
        Array.Copy(hdr36, 0, pkt, 0, HeaderLen);
        WriteInt32BE(pkt, 32, gzip.Length);
        Array.Copy(gzip, 0, pkt, HeaderLen, gzip.Length);
        return pkt;
    }

    /// Day wire: 4 byte zero + packet (yeu cau bat buoc cua server).
    public static byte[] FrameWire(byte[] pkt)
    {
        var wire = new byte[4 + pkt.Length];
        Array.Copy(pkt, 0, wire, 4, pkt.Length);
        return wire;
    }

    public static bool TryDecompressFrom(byte[] data, int off, out byte[] obj)
    {
        obj = Array.Empty<byte>();
        if (off + 3 > data.Length || data[off] != 0x1F || data[off + 1] != 0x8B || data[off + 2] != 0x08) return false;
        try
        {
            var slice = new byte[data.Length - off];
            Array.Copy(data, off, slice, 0, slice.Length);
            using var ms = new MemoryStream(slice);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var buf = new byte[8192];
            while (outMs.Length <= 16_000_000)
            {
                int n = gz.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                outMs.Write(buf, 0, n);
            }
            if (outMs.Length < 10) return false;
            obj = outMs.ToArray();
            return true;
        }
        catch { return false; }
    }

    /// Tim va decompress gzip dau tien trong buffer (load nhieu frame mot luc).
    public static bool TryFindDecompress(byte[] data, out byte[] obj)
    {
        obj = Array.Empty<byte>();
        for (int i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i] == 0x1F && data[i + 1] == 0x8B && data[i + 2] == 0x08)
            {
                if (TryDecompressFrom(data, i, out var o)) { obj = o; return true; }
            }
        }
        return false;
    }

    public record StrDef(int Pos, int Len, string Text);

    public static List<StrDef> ExtractTcStrings(byte[] obj)
    {
        var res = new List<StrDef>();
        int i = 0;
        while (i + 3 <= obj.Length)
        {
            if (obj[i] == 0x74)              // TC_SHORTSTRING/TC_STRING marker
            {
                int len = (obj[i + 1] << 8) | obj[i + 2];
                if (len == 0) { i += 3; continue; }
                if (len > 0 && i + 3 + len <= obj.Length)
                {
                    var s = Encoding.UTF8.GetString(obj, i + 3, len);
                    res.Add(new StrDef(i, len, s));
                    i += 3 + len;
                    continue;
                }
            }
            i++;
        }
        return res;
    }

    /// Chuoi co dang tien to: "1;0901,0912" hoac "1;" hoac "1;#0901,84902"
    /// (cho phep ky tu '#' — danh dau so bi khoa).
    public static bool IsPrefixLike(string s)
    {
        if (s.Length < 2) return false;
        if (s[0] != '0' && s[0] != '1') return false;
        if (s[1] != ';') return false;
        for (int i = 2; i < s.Length; i++)
        {
            char c = s[i];
            if (c != ',' && c != ';' && c != '#' && (c < '0' || c > '9')) return false;
        }
        return true;
    }

    /// Lay gia tri calloutCallerPrefixes tu object response (chuoi dai nhat — head co the la "1;" hoac "0;")
    public static string? FindCallerPrefix(byte[] obj)
    {
        string? best = null;
        foreach (var d in ExtractTcStrings(obj))
        {
            if (!IsPrefixLike(d.Text)) continue;
            // Chuoi chi co "<head>;" (vd "1;", "0;") la metadata rong — bo qua, uu tien chuoi co so
            if (d.Text.Length <= 2) continue;
            if (best == null || d.Text.Length > best.Length) best = d.Text;
        }
        // Neu server that su chua so nao (prefix rong) — tra chuoi ngan lam gia tri thuc
        if (best == null)
            foreach (var d in ExtractTcStrings(obj))
                if (IsPrefixLike(d.Text) && (best == null || d.Text.Length > best.Length)) best = d.Text;
        return best;
    }

    /// Tat ca chuoi co dang prefix (de report)
    public static List<string> AllPrefixStrings(byte[] obj)
    {
        var l = new List<string>();
        foreach (var d in ExtractTcStrings(obj))
            if (IsPrefixLike(d.Text)) l.Add(d.Text);
        return l;
    }

    /// Thay chuoi prefix (dai nhat) trong object bang chuoi moi.
    public static byte[]? PatchPrefix(byte[] obj, string newPrefix, Action<string>? log = null)
    {
        int target = -1, tlen = 0;
        string oldv = "";
        foreach (var d in ExtractTcStrings(obj))
        {
            if (!IsPrefixLike(d.Text) || d.Text.Length <= 2) continue;   // bo qua metadata rong "<head>;"
            if (d.Text.Length > tlen) { target = d.Pos; tlen = d.Text.Length; oldv = d.Text; }
        }
        // object chi co prefix rong → patch chuoi rong dai nhat
        if (target < 0)
            foreach (var d in ExtractTcStrings(obj))
                if (IsPrefixLike(d.Text) && d.Text.Length > tlen) { target = d.Pos; tlen = d.Text.Length; oldv = d.Text; }
        if (target < 0) { log?.Invoke("        khong tim thay chuoi prefix trong object"); return null; }

        var nb = Encoding.UTF8.GetBytes(newPrefix);
        var oldLenBytes = ((obj[target + 1] << 8) | obj[target + 2]);
        int tailOff = target + 3 + oldLenBytes;
        var newBlob = new byte[target + 3 + nb.Length + (obj.Length - tailOff)];
        Array.Copy(obj, newBlob, target);
        newBlob[target] = 0x74;
        newBlob[target + 1] = (byte)(nb.Length >> 8);
        newBlob[target + 2] = (byte)(nb.Length & 0xFF);
        Array.Copy(nb, 0, newBlob, target + 3, nb.Length);
        Array.Copy(obj, tailOff, newBlob, target + 3 + nb.Length, obj.Length - tailOff);
        //log?.Invoke($"        patch: '{oldv}' ({tlen} byte) -> '{newPrefix}' ({nb.Length} byte)");
        return newBlob;
    }

    /// Dung packet SET moi tu template SET capture, thay prefix.
    public static byte[]? BuildSetPacket(byte[] templatePkt, string newPrefix, Action<string>? log = null)
        => BuildSetPacket(templatePkt, newPrefix, null, log);

    /// <summary>
    /// Dung packet SET tu template + PATCH identity (id, rowIndex) tu object GET tra ve live,
    /// de tool van dung sau khi mapping bi xoa va tao lai tren server (id moi).
    /// </summary>
    public static byte[]? BuildSetPacket(byte[] templatePkt, string newPrefix, byte[]? liveGetObj, Action<string>? log = null)
    {
        if (!TryDecompressFrom(templatePkt, HeaderLen, out var obj)) { log?.Invoke("        decompress template SET that bai"); return null; }

        // Identity patch: id cua InfoGatewayMapping trong template co the da o so cu.
        if (liveGetObj != null)
        {
            long liveId = ReadMappingId(liveGetObj, out _, log);
            long tplId = ReadMappingId(obj, out var tplIdPos, log);
            long tplRowIndex = ReadRowIndex(obj, out var rowIndexPos, log);
            log?.Invoke($"        identity: template id={tplId}, rowIndex={tplRowIndex}; server id={liveId}");
            if (liveId > 0 && tplIdPos >= 0 && liveId != tplId)
            {
                WriteInt64BE(obj, tplIdPos, liveId);
                log?.Invoke($"        da patch id {tplId} -> {liveId}");
                // neu rowIndex la truong id theo dang cu thi patch theo
                if (rowIndexPos >= 0 && tplRowIndex == tplId)
                {
                    WriteInt32BE(obj, rowIndexPos, unchecked((int)liveId));
                    log?.Invoke($"        da patch rowIndex {tplRowIndex} -> {liveId}");
                }
            }
        }

        var patched = PatchPrefix(obj, newPrefix, log);
        if (patched == null) return null;
        var hdr = new byte[HeaderLen];
        Array.Copy(templatePkt, hdr, HeaderLen);
        return BuildPacket(hdr, CompressGzip(patched));
    }

    // ============ Java serialization identity helpers ============

    /// Tim classdesc cua tenclass trong obj (TC_OBJECT 'sr' + classdesc 'r' + len + name).
    /// Tra ve vi tri bat dau cua TEN trong stream. -1 neu khong co / khong phai classdesc.
    public static int FindClassDesc(byte[] obj, string name, int from = 0)
    {
        var nb = Encoding.ASCII.GetBytes(name);
        for (int i = from; i + nb.Length <= obj.Length; i++)
        {
            if (obj[i] != nb[0]) continue;
            bool ok = true;
            for (int j = 1; j < nb.Length; j++)
                if (obj[i + j] != nb[j]) { ok = false; break; }
            if (!ok || i < 3) continue;
            if (obj[i - 2] != 0x00 || obj[i - 1] != nb.Length) continue;   // len prefix
            if (obj[i - 3] != 0x72) continue;                             // TC_CLASSDESC marker
            return i;
        }
        return -1;
    }

    /// Tim '0x78 0x70' (end block + null super) gan nhat bat dau tu 'from' trong cua so window.
    static int FindEndBlockNull(byte[] obj, int from, int window)
    {
        for (int k = from; k < obj.Length - 1 && k < from + window; k++)
            if (obj[k] == 0x78 && obj[k + 1] == 0x70) return k;
        return -1;
    }

    /// Doc truong id (long) cua InfoGatewayMapping trong 1 Java stream.
    /// Instance data bat dau ngay sau khoi classdesc cua lop cha (Info) ket thuc bang 0x78 0x70.
    public static long ReadMappingId(byte[] obj, out int idPos, Action<string>? log = null)
    {
        idPos = -1;
        int mIdx = FindClassDesc(obj, "com.kunshi.interfacef.vos2000.InfoGatewayMapping");
        if (mIdx < 0) { log?.Invoke("        khong thay classdesc InfoGatewayMapping"); return -1; }
        int pIdx = FindClassDesc(obj, "com.kunshi.interfacef.common.Info", mIdx);
        if (pIdx < 0) { log?.Invoke("        khong thay classdesc cha Info"); return -1; }
        int nameLen = "com.kunshi.interfacef.common.Info".Length;
        int end = FindEndBlockNull(obj, pIdx + nameLen, 96);
        if (end < 0) { log?.Invoke("        khong thay ket thuc khoi class cha"); return -1; }
        idPos = end + 2;
        if (idPos + 8 > obj.Length) return -1;
        long v = ((long)obj[idPos] << 56) | ((long)obj[idPos + 1] << 48) | ((long)obj[idPos + 2] << 40)
               | ((long)obj[idPos + 3] << 32) | ((long)obj[idPos + 4] << 24) | ((long)obj[idPos + 5] << 16)
               | ((long)obj[idPos + 6] << 8) | obj[idPos + 7];
        return v;
    }

    /// Doc truong rowIndex (int) cua ReqSetInfo trong 1 Java stream SET.
    public static long ReadRowIndex(byte[] obj, out int rowIndexPos, Action<string>? log = null)
    {
        rowIndexPos = -1;
        int rIdx = FindClassDesc(obj, "com.kunshi.interfacef.common.ReqSetInfo");
        if (rIdx < 0) { log?.Invoke("        khong thay classdesc ReqSetInfo"); return -1; }
        // sau classdesc la cac field desc; tim chuoi class cua field 'info'
        int fIdx = FindAsciiAfter(obj, rIdx, "Lcom/kunshi/interfacef/common/Info;");
        if (fIdx < 0) { log?.Invoke("        khong thay field class Info;"); return -1; }
        int end = fIdx + "Lcom/kunshi/interfacef/common/Info;".Length;
        if (end + 2 > obj.Length || obj[end] != 0x78 || obj[end + 1] != 0x70)
        {
            end = FindEndBlockNull(obj, end, 6);
            if (end < 0) { log?.Invoke("        khong thay ket thuc ReqSetInfo classdesc"); return -1; }
        }
        rowIndexPos = end + 2;
        if (rowIndexPos + 4 > obj.Length) return -1;
        return ReadInt32BE(obj, rowIndexPos);
    }

    static int FindAsciiAfter(byte[] obj, int from, string ascii)
    {
        var nb = Encoding.ASCII.GetBytes(ascii);
        for (int i = from; i + nb.Length <= obj.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < nb.Length; j++)
                if (obj[i + j] != nb[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static void WriteInt64BE(byte[] b, int off, long v)
    {
        unchecked
        {
            b[off] = (byte)(v >> 56); b[off + 1] = (byte)(v >> 48); b[off + 2] = (byte)(v >> 40); b[off + 3] = (byte)(v >> 32);
            b[off + 4] = (byte)(v >> 24); b[off + 5] = (byte)(v >> 16); b[off + 6] = (byte)(v >> 8); b[off + 7] = (byte)v;
        }
    }

    // (customer helpers chuyen sang VosCustomer.cs)
}

