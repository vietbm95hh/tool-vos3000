using System.Text;

namespace Vos3000Agent
{
    /// 1 dòng customer đọc được từ VOS3000 (qua TCP 1202).
    public class VosCustomerRow
    {
        public int Id;
        public string Account = "";
        public string Name = "";
        public string Comment = "";
        public int Status;
        public int LockType;
        public int Type;
        public double Money;
        public double LimitMoney;
        public double TodayConsumption;
        public long StartTime;
        public long ValidTime;

        public static DateTime? FromVosMs(long v)
        {
            if (v <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(v).LocalDateTime; }
            catch { return null; }
        }

        public DateTime? Start => FromVosMs(StartTime);
        public DateTime? Valid => FromVosMs(ValidTime);
    }

    /// Đọc danh sách customer trực tiếp từ VOS3000 qua TCP 1202 (CHỈ ĐỌC).
    ///
    /// Dùng packet FilterCustomer trong session template (lọc theo agentAccount "cts"
    /// hoặc để rỗng) — server trả về danh sách object InfoCustomer đầy đủ.
    /// Kết quả khớp đúng với những gì client VOS3000 đang hiển thị (RAM), không phải DB.
    public class VosCustomerReader
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _sessionFile;
        private readonly Action<string> _log;

        private List<byte[]> _pk = new();
        public int HelloIdx = -1, LoginIdx = -1, FilterCustomerIdx = -1;

        /// Lọc theo agentAccount. null = lấy TẤT CẢ customer.
        /// (Packet template capture sẵn lọc "cts" — nếu server không có agent đó sẽ trả rỗng.)
        public string AgentAccount;

        public VosCustomerReader(string host, int port, string sessionFile, Action<string> log, string agentAccount = null)
        {
            _host = host; _port = port; _sessionFile = sessionFile; _log = log ?? (_ => { });
            AgentAccount = agentAccount;
        }

        public bool LoadSession()
        {
            if (string.IsNullOrWhiteSpace(_sessionFile) || !File.Exists(_sessionFile))
            {
                _log($"!! Không tìm thấy file session: {_sessionFile}");
                return false;
            }
            try { _pk = VosProto.SplitPackets(File.ReadAllBytes(_sessionFile)); }
            catch (Exception e) { _log($"!! Lỗi đọc session: {e.Message}"); return false; }

            HelloIdx = LoginIdx = FilterCustomerIdx = -1;
            for (int i = 0; i < _pk.Count; i++)
            {
                var pkt = _pk[i];
                if (HelloIdx < 0 && !VosProto.IsGzip(pkt)) { HelloIdx = i; continue; }
                if (!VosProto.TryDecompressFrom(pkt, VosProto.HeaderLen, out var obj)) continue;
                var txt = Encoding.UTF8.GetString(obj);
                if (LoginIdx < 0 && txt.Contains("loginName")) LoginIdx = i;
                if (FilterCustomerIdx < 0 && txt.Contains("FilterCustomer") && txt.Contains("InfoCustomer"))
                    FilterCustomerIdx = i;
            }

            _log($"Session: {_pk.Count} packet (hello#{HelloIdx} login#{LoginIdx} FilterCustomer#{FilterCustomerIdx})");
            if (HelloIdx < 0 || LoginIdx < 0)
            {
                _log("!! Session thiếu packet hello/login.");
                return false;
            }
            return true;
        }

        /// Đọc toàn bộ customer. Trả về danh sách (có thể rỗng) + lỗi nếu không đọc được.
        public (List<VosCustomerRow> Rows, string Error) ReadAll()
        {
            var rows = new List<VosCustomerRow>();
            if (!LoadSession()) return (rows, "Không đọc được file session template.");
            if (FilterCustomerIdx < 0)
                return (rows, "Session template không có packet FilterCustomer.");

            using var s = new VosSession(_host, _port, m => _log("  " + m));
            if (!s.Connect())
                return (rows, $"Không kết nối được VOS3000 {_host}:{_port} — {s.LastError}");

            foreach (var _ in s.SendPacket(_pk[HelloIdx], 900)) { }
            foreach (var _ in s.SendPacket(_pk[LoginIdx], 1200)) { }

            var objects = s.SendPacket(BuildFilterPacket(), 2500);
            // đọc thêm nếu server gửi tiếp (danh sách dài trải qua nhiều gói)
            foreach (var _ in s.ReadAvailable(800)) { }

            _log($"Server trả về {objects.Count} object/chunk.");
            if (objects.Count == 0)
                return (rows, "Server không trả dữ liệu cho FilterCustomer (không nhận packet phản hồi).");

            var schema = new InfoCustomerSchema();
            int parsed = 0;
            foreach (var obj in objects)
            {
                bool hasCustomerClass = ContainsAscii(obj, "InfoCustomer");
                int n = 0;
                foreach (var row in schema.ParseAll(obj))
                {
                    n++;
                    parsed++;
                    if (row != null && IsMeaningful(row)) rows.Add(row);
                }
                _log($"  chunk {obj.Length,6} B: InfoCustomer={(hasCustomerClass ? "có" : "KHÔNG")}, parse được {n} customer.");
            }
            _log($"Tổng: parse {parsed} object, dùng được {rows.Count}.");

            // khử trùng theo id (id > 0), giữ bản đầu
            var seen = new HashSet<int>();
            var unique = new List<VosCustomerRow>(rows.Count);
            foreach (var r in rows)
            {
                if (r.Id > 0 && !seen.Add(r.Id)) continue;
                unique.Add(r);
            }
            unique.Sort((a, b) => string.Compare(a.Account, b.Account, StringComparison.OrdinalIgnoreCase));

            if (unique.Count == 0)
                return (unique, "Không đọc được customer nào từ server (xem log).");
            return (unique, null);
        }

        private static bool IsMeaningful(VosCustomerRow r)
            => !string.IsNullOrWhiteSpace(r.Account) || r.Id > 0;

        private static bool ContainsAscii(byte[] data, string needle)
            => VosSession.ContainsAscii(data, needle);

        /// Dựng packet FilterCustomer theo AgentAccount (null = lấy tất cả).
        /// Chỉ ghi lại chuỗi agentAccount trong data; các byte khác giữ nguyên.
        private byte[] BuildFilterPacket()
        {
            var tpl = _pk[FilterCustomerIdx];
            if (AgentAccount == null) return tpl;   // giữ nguyên filter trong template

            if (!VosProto.TryDecompressFrom(tpl, VosProto.HeaderLen, out var obj))
                return tpl;

            int slot = FindAgentAccountSlot(obj, out int curLen);
            if (slot < 0)
            {
                _log("!! Không tìm thấy agentAccount trong template — dùng nguyên bản.");
                return tpl;
            }
            _log($"Đổi agentAccount: '{Encoding.UTF8.GetString(obj, slot + 3, curLen)}' -> '{AgentAccount}'");

            var nb = Encoding.UTF8.GetBytes(AgentAccount);
            if (nb.Length > 0xFFFF) return tpl;

            int tail = slot + 3 + curLen;
            var res = new byte[slot + 3 + nb.Length + (obj.Length - tail)];
            Array.Copy(obj, res, slot);
            res[slot] = 0x74;                                    // TC_STRING
            res[slot + 1] = (byte)(nb.Length >> 8);
            res[slot + 2] = (byte)(nb.Length & 0xFF);
            Array.Copy(nb, 0, res, slot + 3, nb.Length);
            Array.Copy(obj, tail, res, slot + 3 + nb.Length, obj.Length - tail);

            var hdr = new byte[VosProto.HeaderLen];
            Array.Copy(tpl, hdr, VosProto.HeaderLen);
            return VosProto.BuildPacket(hdr, VosProto.CompressGzip(res));
        }

        /// Tìm chuỗi TC_STRING của agentAccount trong data FilterCustomer.
        /// Template hiện có giá trị "cts" — ta dò mẫu 0x74 00 03 'cts'.
        private static int FindAgentAccountSlot(byte[] obj, out int curLen)
        {
            curLen = 0;
            for (int i = 0; i + 6 <= obj.Length; i++)
            {
                if (obj[i] != 0x74) continue;
                int len = (obj[i + 1] << 8) | obj[i + 2];
                if (len != 3) continue;
                if (obj[i + 3] == 'c' && obj[i + 4] == 't' && obj[i + 5] == 's')
                {
                    curLen = 3;
                    return i;
                }
            }
            return -1;
        }
    }

    /// Bộ đọc InfoCustomer theo schema Java serialization (chỉ đọc, không sửa).
    internal class InfoCustomerSchema
    {
        const byte TC_STRING = 0x74;
        const byte TC_ENDBLOCK = 0x78;
        const byte TC_NULL = 0x70;
        const byte TC_REFERENCE = 0x71;
        const byte TC_CLASSDESC = 0x72;
        const byte TC_OBJECT = 0x73;

        // Kiểu field trong classdesc
        const byte F_BYTE = 0x42, F_CHAR = 0x43, F_DOUBLE = 0x44, F_FLOAT = 0x46,
                   F_INT = 0x49, F_LONG = 0x4A, F_SHORT = 0x53, F_BOOL = 0x5A,
                   F_ARRAY = 0x5B, F_OBJECT = 0x4C;

        /// Parse tất cả object InfoCustomer trong 1 chunk (byte[] decompressed).
        public List<VosCustomerRow> ParseAll(byte[] buf)
        {
            var res = new List<VosCustomerRow>();
            int scan = 0;
            while (scan < buf.Length)
            {
                int at = FindCustomerClassdesc(buf, scan);
                if (at < 0) break;
                try
                {
                    int p = at;
                    var row = ReadCustomerObject(buf, ref p);
                    if (row != null) res.Add(row);
                }
                catch (Exception ex)
                {
                    // object hỏng — bỏ qua, tiếp tục tìm object kế tiếp
                    _ = ex;
                }
                scan = at + 1;
            }
            return res;
        }

        private static int FindCustomerClassdesc(byte[] buf, int from)
        {
            var needle = Encoding.ASCII.GetBytes("com.kunshi.interfacef.vos2000.InfoCustomer");
            for (int i = from; i + needle.Length + 4 <= buf.Length; i++)
            {
                // classdesc hợp lệ: "x72 00 <len> com.kunshi...InfoCustomer"
                if (buf[i] != TC_CLASSDESC) continue;
                int nl = (buf[i + 1] << 8) | buf[i + 2];
                if (nl != needle.Length) continue;
                bool ok = true;
                for (int j = 0; j < nl; j++) if (buf[i + 3 + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        // ---------- classdesc ----------

        private sealed class FieldDef
        {
            public string Name = "";
            public byte Tc;
            public string TypeName;   // cho object/array
        }

        /// Đọc 1 classdesc (kể cả chuỗi lớp cha). Trả về toàn bộ field theo đúng thứ tự data.
        private List<FieldDef>? ReadClassdesc(byte[] buf, ref int p)
        {
            var fields = new List<FieldDef>();
            bool any = false;

            while (true)
            {
                if (p >= buf.Length) return any ? fields : null;
                if (buf[p] == TC_ENDBLOCK) p++;
                if (p >= buf.Length || buf[p] != TC_CLASSDESC) break;

                any = true;
                p++;                                  // TC_CLASSDESC
                if (p + 2 > buf.Length) return null;
                int nl = (buf[p] << 8) | buf[p + 1]; p += 2;
                p += nl;                              // tên lớp
                p += 8;                               // serialVersionUID
                if (p >= buf.Length) return null;
                p++;                                  // flags
                if (p + 2 > buf.Length) return null;
                int nf = (buf[p] << 8) | buf[p + 1]; p += 2;

                for (int k = 0; k < nf; k++)
                {
                    if (p + 3 > buf.Length) return null;
                    byte tc = buf[p];
                    int fl = (buf[p + 1] << 8) | buf[p + 2];
                    if (p + 3 + fl > buf.Length) return null;
                    string fname = Encoding.UTF8.GetString(buf, p + 3, fl);
                    p += 3 + fl;

                    var fd = new FieldDef { Name = fname, Tc = tc };
                    if (tc == F_OBJECT || tc == F_ARRAY)
                    {
                        if (p >= buf.Length) return null;
                        if (buf[p] == TC_STRING)
                        {
                            if (p + 3 > buf.Length) return null;
                            int tl = (buf[p + 1] << 8) | buf[p + 2];
                            if (p + 3 + tl > buf.Length) return null;
                            fd.TypeName = Encoding.UTF8.GetString(buf, p + 3, tl);
                            p += 3 + tl;
                        }
                        else if (buf[p] == TC_REFERENCE) p += 5;
                        else return null;
                    }
                    fields.Add(fd);
                }

                // hết field của lớp này: bỏ qua ENDBLOCK cuối classdesc
                if (p < buf.Length && buf[p] == TC_ENDBLOCK) p++;

                // còn lớp cha? (0x72 ngay tiếp) — KHÔNG tiêu thụ 0x78 ở đây
                if (p < buf.Length && buf[p] == TC_CLASSDESC) continue;
                break;
            }
            return any ? fields : null;
        }

        // ---------- object ----------

        private VosCustomerRow? ReadCustomerObject(byte[] buf, ref int p)
        {
            // p đang trỏ tới TC_CLASSDESC của InfoCustomer, nhưng ta cần classdesc CỦA OBJECT.
            // Trong stream TC_OBJECT: 0x73 -> (classdesc mới | 0x71 ref) -> 0x78 -> data.
            // Nếu trước classdesc có TC_OBJECT ngay trước (b -2 hoặc -3), lùi lại để parse cả object.
            int objStart = p;
            if (objStart > 0 && buf[objStart - 1] == TC_OBJECT) objStart -= 1;
            else
            {
                // tìm TC_OBJECT gần nhất trong 6 byte trước classdesc
                for (int i = objStart - 1; i >= Math.Max(0, objStart - 6); i--)
                    if (buf[i] == TC_OBJECT) { objStart = i; break; }
            }

            int q = objStart;
            if (q >= buf.Length || buf[q] != TC_OBJECT) return null;
            q++;

            var fields = ReadClassdesc(buf, ref q);
            if (fields == null) return null;

            // sau classdesc: 0x78 [0x71 ref nếu lớp cha đã xuất hiện] rồi data
            if (q < buf.Length && buf[q] == TC_ENDBLOCK)
            {
                q++;
                if (q < buf.Length && buf[q] == TC_REFERENCE) q += 5;
            }

            var row = new VosCustomerRow();
            foreach (var f in fields)
            {
                if (!ReadField(buf, ref q, f, row)) break;
            }
            p = q;
            return row;
        }

        private static bool ReadField(byte[] buf, ref int p, FieldDef f, VosCustomerRow row)
        {
            if (p >= buf.Length) return false;
            switch (f.Tc)
            {
                case F_INT:
                    if (p + 4 > buf.Length) return false;
                    AssignInt(f.Name, ReadInt32BE(buf, p), row); p += 4; return true;
                case F_LONG:
                    if (p + 8 > buf.Length) return false;
                    AssignLong(f.Name, ReadInt64(buf, p), row); p += 8; return true;
                case F_DOUBLE:
                    if (p + 8 > buf.Length) return false;
                    AssignDouble(f.Name, BitConverter.Int64BitsToDouble(ReadInt64(buf, p)), row); p += 8; return true;
                case F_BYTE: case F_BOOL: p += 1; return true;
                case F_CHAR: case F_SHORT: p += 2; return true;
                case F_FLOAT: p += 4; return true;
                default:
                    // object/array: chuỗi thì lấy value, object khác thì bỏ qua theo schema
                    return ReadAnyValue(buf, ref p, f, row);
            }
        }

        private static bool ReadAnyValue(byte[] buf, ref int p, FieldDef f, VosCustomerRow row)
        {
            if (p >= buf.Length) return false;
            switch (buf[p])
            {
                case TC_STRING:
                    {
                        if (p + 3 > buf.Length) return false;
                        int len = (buf[p + 1] << 8) | buf[p + 2];
                        if (p + 3 + len > buf.Length) return false;
                        string val = Encoding.UTF8.GetString(buf, p + 3, len);
                        AssignString(f.Name, val, row);
                        p += 3 + len;
                        return true;
                    }
                case TC_NULL: p++; return true;
                case TC_REFERENCE: p += 5; return true;
                case TC_OBJECT: return SkipObject(buf, ref p);
                case TC_CLASSDESC: return SkipClassdescOnly(buf, ref p);
                default: return false;
            }
        }

        /// Bỏ qua 1 TC_OBJECT: đọc classdesc -> schema -> data.
        private static bool SkipObject(byte[] buf, ref int p)
        {
            var schema = new InfoCustomerSchema();
            p++;                                   // TC_OBJECT
            if (p >= buf.Length) return false;
            if (buf[p] == TC_REFERENCE) { p += 5; return SkipUnknownObjectData(buf, ref p); }

            var fields = schema.ReadClassdesc(buf, ref p);
            if (fields == null) return false;
            if (p < buf.Length && buf[p] == TC_ENDBLOCK)
            {
                p++;
                if (p < buf.Length && buf[p] == TC_REFERENCE) p += 5;
            }
            var dummy = new VosCustomerRow();
            foreach (var f in fields)
                if (!ReadField(buf, ref p, f, dummy)) return false;
            return true;
        }

        private static bool SkipUnknownObjectData(byte[] buf, ref int p) => false;

        private static bool SkipClassdescOnly(byte[] buf, ref int p)
        {
            var schema = new InfoCustomerSchema();
            return schema.ReadClassdesc(buf, ref p) != null;
        }

        private static void AssignInt(string name, int v, VosCustomerRow row)
        {
            switch (name)
            {
                case "id": row.Id = v; break;
                case "status": row.Status = v; break;
                case "lockType": row.LockType = v; break;
                case "type": row.Type = v; break;
            }
        }

        private static void AssignLong(string name, long v, VosCustomerRow row)
        {
            switch (name)
            {
                case "startTime": row.StartTime = v; break;
                case "validTime": row.ValidTime = v; break;
                case "status": row.Status = (int)v; break;
                case "lockType": row.LockType = (int)v; break;
                case "type": row.Type = (int)v; break;
            }
        }

        private static void AssignDouble(string name, double v, VosCustomerRow row)
        {
            switch (name)
            {
                case "money": row.Money = v; break;
                case "limitMoney": row.LimitMoney = v; break;
                case "todayConsumption": row.TodayConsumption = v; break;
            }
        }

        private static void AssignString(string name, string v, VosCustomerRow row)
        {
            switch (name)
            {
                case "account": row.Account = v; break;
                case "name": row.Name = v; break;
                case "comment": row.Comment = v; break;
            }
        }

        private static int ReadInt32BE(byte[] b, int o)
            => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static long ReadInt64(byte[] b, int o) => unchecked(
            ((long)b[o] << 56) | ((long)b[o + 1] << 48) | ((long)b[o + 2] << 40) | ((long)b[o + 3] << 32) |
            ((long)b[o + 4] << 24) | ((long)b[o + 5] << 16) | ((long)b[o + 6] << 8) | b[o + 7]);
    }
}
