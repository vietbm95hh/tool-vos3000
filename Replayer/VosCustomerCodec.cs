// ============================================================
//  VosCustomerCodec - doc / sua InfoCustomer qua TCP 1202
//
//  Format VOS3000 (da kiem chung byte-by-byte tren server that 172.29.101.32):
//
//    AC ED 00 05 73 72 <len> "com.kunshi.interfacef.vos2000.InfoCustomer" <4 meta>
//    <flags> <nf=0x001B=27> <27 field desc> ... x p                  <- classdesc InfoCustomer
//    x r <len> "com.kunshi.interfacef.common.Info" <4 meta>
//       <flags> <nf=3> I id | J mask | I operationType ... x p        <- classdesc CHA (Info)
//    ==== DATA bat dau ngay sau "x p" cua classdesc cha ====
//      I id (4)   J mask (8)   I operationType (4)
//      -- InfoCustomer (18 truong so) --
//      I activePhoneCardCount, category, currentSuitesCount, customer_id, gatewayCount
//      J lastPayTime,  D limitMoney, I lockType, D money, I operatorUserPrivielge,
//      I phoneCount, I shortE164Count, J startTime, I status, I suitesCount,
//      D todayConsumption, J type, J validTime
//      -- chuoi: t account, t additionalInfo..., t agentAccount, t comment ... --
//
//  Tat ca so deu BIG-ENDIAN. Double dung IEEE 754 big-endian.
// ============================================================
using System.Text;

public static class VosCustomerCodec
{
    const byte TC_STRING = 0x74;
    const byte TC_ENDBLOCK = 0x78;

    const string InfoCustomerClass = "com.kunshi.interfacef.vos2000.InfoCustomer";
    const string InfoParentClass = "com.kunshi.interfacef.common.Info";

    /// 18 truong so cua InfoCustomer theo dung thu tu ghi trong stream.
    /// (da kiem chung byte-by-byte: type la I 4-byte, validTime la J 8-byte)
    public static readonly (string Name, char Type)[] NumberFields =
    {
        ("activePhoneCardCount", 'I'),   // 1013
        ("category",             'I'),   // 1017
        ("currentSuitesCount",   'I'),   // 1021
        ("customer_id",          'I'),   // 1025
        ("gatewayCount",         'I'),   // 1029
        ("lastPayTime",          'J'),   // 1033
        ("limitMoney",           'D'),   // 1041
        ("lockType",             'I'),   // 1049
        ("money",                'D'),   // 1053
        ("operatorUserPrivielge",'I'),   // 1061
        ("phoneCount",           'I'),   // 1065
        ("shortE164Count",       'I'),   // 1069
        ("startTime",            'J'),   // 1073
        ("status",               'I'),   // 1081
        ("suitesCount",          'I'),   // 1085
        ("todayConsumption",     'D'),   // 1089
        ("type",                 'I'),   // 1097  <-- I (khong phai J)
        ("validTime",            'J'),   // 1101
    };

    /// 4 chuỗi dung dau (sau cac truong so), theo thu tu stream.
    static readonly string[] HeadStringFields = { "account", "(additionalInfo)", "agentAccount", "comment" };

    // ---------- phan tich cau truc ----------

    public static bool IsCustomerObject(byte[] obj)
        => obj != null && IndexOf(obj, Encoding.ASCII.GetBytes(InfoCustomerClass)) >= 0
                      && IndexOf(obj, Encoding.ASCII.GetBytes("activePhoneCardCount")) >= 0;

    /// Vi tri bat dau data (ngay sau "x p" cua classdesc cha Info). -1 neu khong tim thay.
    public static int FindDataStart(byte[] obj)
    {
        int cust = IndexOf(obj, Encoding.ASCII.GetBytes(InfoCustomerClass));
        if (cust < 0) return -1;
        int parent = IndexOf(obj, Encoding.ASCII.GetBytes(InfoParentClass), cust);
        if (parent < 0) return -1;

        
        for (int i = parent + InfoParentClass.Length; i < Math.Min(obj.Length - 1, parent + 200); i++)
        {
            if (obj[i] == TC_ENDBLOCK && obj[i + 1] == 0x70) return i + 2;
        }
        return -1;
    }

    /// Offset cac truong so: tra ve Dictionary ten -> (offset, size, type)
    public static Dictionary<string, (int Offset, int Size, char Type)> MapNumbers(byte[] obj)
    {
        var res = new Dictionary<string, (int, int, char)>(StringComparer.Ordinal);
        int data = FindDataStart(obj);
        if (data < 0) return res;

        // id, mask, operationType cua lop cha
        res["id"] = (data, 4, 'I');
        res["mask"] = (data + 4, 8, 'J');
        res["operationType"] = (data + 12, 4, 'I');
        int p = data + 16;

        foreach (var f in NumberFields)
        {
            int size = f.Type == 'I' ? 4 : 8;
            if (p + size > obj.Length) break;
            res[f.Name] = (p, size, f.Type);
            p += size;
        }
        res["__strings"] = (p, 0, 'S');
        return res;
    }

    // ---------- doc ----------

    public static long? ReadNumber(byte[] obj, string field)
    {
        var m = MapNumbers(obj);
        if (!m.TryGetValue(field, out var loc)) return null;
        if (loc.Type == 'I') return VosProto.ReadInt32BE(obj, loc.Offset);
        return ReadInt64(obj, loc.Offset);
    }

    public static double? ReadDouble(byte[] obj, string field)
    {
        var m = MapNumbers(obj);
        if (!m.TryGetValue(field, out var loc) || loc.Type != 'D') return null;
        return BitConverter.Int64BitsToDouble(ReadInt64(obj, loc.Offset));
    }

    public static string ReadString(byte[] obj, string field)
    {
        var strings = ReadHeadStrings(obj);
        return strings.TryGetValue(field, out var v) ? v : null;
    }

    public static Dictionary<string, string> ReadHeadStrings(byte[] obj)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        var m = MapNumbers(obj);
        if (!m.TryGetValue("__strings", out var at)) return res;
        int p = at.Offset;
        foreach (var name in HeadStringFields)
        {
            if (!TryReadStringValue(obj, ref p, out var v)) return res;
            res[name] = v;
        }
        return res;
    }

    static bool TryReadStringValue(byte[] obj, ref int p, out string value)
    {
        value = null;
        if (p >= obj.Length) return false;
        if (obj[p] == TC_STRING)
        {
            if (p + 3 > obj.Length) return false;
            int len = (obj[p + 1] << 8) | obj[p + 2];
            if (p + 3 + len > obj.Length) return false;
            value = Encoding.UTF8.GetString(obj, p + 3, len);
            p += 3 + len;
            return true;
        }
        if (obj[p] == 0x71 && p + 5 <= obj.Length) { p += 5; return true; }   
        if (obj[p] == 0x70) { p++; return true; }                             
        return false;
    }

    /// Doc tat ca truong co the doc duoc (de hien thi / doi chieu).
    public static (Dictionary<string, long?> nums, Dictionary<string, string> strs, Dictionary<string, double?> dbls) ReadAll(byte[] obj)
    {
        var nums = new Dictionary<string, long?>();
        var dbls = new Dictionary<string, double?>();
        foreach (var f in NumberFields)
        {
            if (f.Type == 'D') dbls[f.Name] = ReadDouble(obj, f.Name);
            else nums[f.Name] = ReadNumber(obj, f.Name);
        }
        nums["id"] = ReadNumber(obj, "id");
        return (nums, ReadHeadStrings(obj), dbls);
    }

    // ---------- patch ----------

    /// Ghi 1 truong so. Tra ve object moi.
    public static byte[] PatchNumber(byte[] obj, string field, long value)
    {
        var m = MapNumbers(obj);
        if (!m.TryGetValue(field, out var loc)) return null;
        var res = (byte[])obj.Clone();
        if (loc.Type == 'I') VosProto.WriteInt32BE(res, loc.Offset, unchecked((int)value));
        else WriteInt64(res, loc.Offset, value);
        return res;
    }

    public static byte[] PatchDouble(byte[] obj, string field, double value)
    {
        var m = MapNumbers(obj);
        if (!m.TryGetValue(field, out var loc) || loc.Type != 'D') return null;
        var res = (byte[])obj.Clone();
        WriteInt64(res, loc.Offset, BitConverter.DoubleToInt64Bits(value));
        return res;
    }

    /// Ghi 1 truong chuoi (account/agentAccount/comment). So khop gia tri cu de an toan.
    public static byte[] PatchString(byte[] obj, string field, string oldValue, string newValue)
    {
        if (oldValue == newValue) return obj;
        var m = MapNumbers(obj);
        if (!m.TryGetValue("__strings", out var at)) return null;

        int p = at.Offset;
        foreach (var name in HeadStringFields)
        {
            int startPos = p;
            if (!TryReadStringValue(obj, ref p, out var v)) return null;
            if (name != field) continue;
            if (v != oldValue) return null;                 // khong khop => khong ghi
            if (obj[startPos] != TC_STRING) return null;    // la reference => khong patch duoc
            int len = (obj[startPos + 1] << 8) | obj[startPos + 2];
            return ReplaceTcString(obj, startPos, len, newValue);
        }
        return null;
    }

    /// Patch nhieu truong mot lan. values: ten -> gia tri (string/long/double).
    public static byte[] PatchAll(byte[] obj, IReadOnlyDictionary<string, object> oldValues,
                                  IReadOnlyDictionary<string, object> newValues, Action<string> log = null)
    {
        var cur = obj;
        foreach (var kv in newValues)
        {
            if (kv.Value == null) continue;
            string name = kv.Key;
            if (cur == null) { log?.Invoke($"!! mat object khi patch {name}"); return null; }

            bool isStr = kv.Value is string;
            object oldV = oldValues != null && oldValues.TryGetValue(name, out var ov) ? ov : null;

            if (isStr)
            {
                var next = PatchString(cur, name, oldV as string, (string)kv.Value);
                if (next == null) { log?.Invoke($"  !! khong patch duoc chuoi '{name}' (cu='{oldV}')"); continue; }
                cur = next;
                log?.Invoke($"  patch {name}: '{oldV}' -> '{kv.Value}'");
            }
            else if (kv.Value is double d)
            {
                var next = PatchDouble(cur, name, d);
                if (next == null) { log?.Invoke($"  !! khong patch duoc double '{name}'"); continue; }
                cur = next;
                log?.Invoke($"  patch {name} = {d}");
            }
            else
            {
                long lv = Convert.ToInt64(kv.Value);
                var next = PatchNumber(cur, name, lv);
                if (next == null) { log?.Invoke($"  !! khong patch duoc so '{name}'"); continue; }
                cur = next;
                log?.Invoke($"  patch {name} = {lv}");
            }
        }
        return cur;
    }

    // ---------- helpers ----------

    /// Dung packet SET (ReqSetInfoList) cho 1 customer tu template SET cua gateway.
    ///
    /// Lay tu template:
    ///   - ReqSetInfoList classdesc + fatherId + 3 vector rong
    ///   - ReqSetInfo classdesc + rowIndex
    /// roi thay object InfoGatewayMapping (classdesc + data) bang object InfoCustomer tu GET.
    public static byte[] BuildSetPacket(byte[] templateSetPacket, byte[] customerObj, Action<string> log = null)
    {
        if (templateSetPacket == null || customerObj == null) { log?.Invoke("  thieu du lieu"); return null; }
        if (!VosProto.TryDecompressFrom(templateSetPacket, VosProto.HeaderLen, out var tpl))
        { log?.Invoke("  khong doc duoc template SET"); return null; }

        // ---- moc trong TEMPLATE ----
        int tplInfoName = IndexOf(tpl, Encoding.ASCII.GetBytes(InfoParentClass));
        if (tplInfoName < 0) { log?.Invoke("  template thieu classdesc cha Info"); return null; }
        int tplGwName = IndexOf(tpl, Encoding.ASCII.GetBytes("com.kunshi.interfacef.vos2000.InfoGatewayMapping"));
        if (tplGwName < 0) { log?.Invoke("  template khong phai ReqSetInfoList gateway"); return null; }
        int tplCdStart = tplGwName - 4;                       // '73 72 00 xx'

        // ---- moc trong CUSTOMER (GET) ----
        int cName = IndexOf(customerObj, Encoding.ASCII.GetBytes(InfoCustomerClass));
        if (cName < 0) { log?.Invoke("  object khong phai InfoCustomer"); return null; }
        int cCdStart = cName - 4;
        int cParent = IndexOf(customerObj, Encoding.ASCII.GetBytes(InfoParentClass), cName);
        if (cParent < 0) { log?.Invoke("  customer thieu classdesc cha Info"); return null; }
        int cXp = -1;
        for (int i = cParent; i < Math.Min(customerObj.Length - 1, cParent + 200); i++)
            if (customerObj[i] == TC_ENDBLOCK && customerObj[i + 1] == 0x70) { cXp = i + 2; break; }
        if (cXp < 0) { log?.Invoke("  khong tim thay data customer"); return null; }

        // ---- ghep ----
        // [tpl 0..tplCdStart)                          ReqSetInfoList + fatherId + 3 vector rong + ReqSetInfo + rowIndex
        // [customer cCdStart..cXp)                     classdesc InfoCustomer + 'x r <Info>' + 'x p'
        // [customer cXp..end)                          data + cac object con
        var ms = new MemoryStream(tpl.Length - tplCdStart + customerObj.Length);
        ms.Write(tpl, 0, tplCdStart);
        ms.Write(customerObj, cCdStart, cXp - cCdStart);
        ms.Write(customerObj, cXp, customerObj.Length - cXp);
        var obj = ms.ToArray();

        log?.Invoke($"  SET object: {obj.Length} bytes (template {tpl.Length}, customer {customerObj.Length})");

        var hdr = new byte[VosProto.HeaderLen];
        Array.Copy(templateSetPacket, hdr, VosProto.HeaderLen);
        return VosProto.BuildPacket(hdr, VosProto.CompressGzip(obj));
    }

    // ---------- helpers ----------

    static byte[] ReplaceTcString(byte[] obj, int pos, int oldLen, string newValue)
    {
        var nb = Encoding.UTF8.GetBytes(newValue ?? "");
        int tail = pos + 3 + oldLen;
        var res = new byte[pos + 3 + nb.Length + (obj.Length - tail)];
        Array.Copy(obj, res, pos);
        res[pos] = TC_STRING;
        res[pos + 1] = (byte)(nb.Length >> 8);
        res[pos + 2] = (byte)(nb.Length & 0xFF);
        Array.Copy(nb, 0, res, pos + 3, nb.Length);
        Array.Copy(obj, tail, res, pos + 3 + nb.Length, obj.Length - tail);
        return res;
    }

    static int IndexOf(byte[] hay, byte[] needle, int from = 0)
    {
        for (int i = Math.Max(0, from); i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static long ReadInt64(byte[] b, int o) => unchecked(
        ((long)b[o] << 56) | ((long)b[o + 1] << 48) | ((long)b[o + 2] << 40) | ((long)b[o + 3] << 32) |
        ((long)b[o + 4] << 24) | ((long)b[o + 5] << 16) | ((long)b[o + 6] << 8) | b[o + 7]);

    static void WriteInt64(byte[] b, int o, long v)
    {
        unchecked
        {
            b[o] = (byte)(v >> 56); b[o + 1] = (byte)(v >> 48); b[o + 2] = (byte)(v >> 40); b[o + 3] = (byte)(v >> 32);
            b[o + 4] = (byte)(v >> 24); b[o + 5] = (byte)(v >> 16); b[o + 6] = (byte)(v >> 8); b[o + 7] = (byte)v;
        }
    }
}
