// ============================================================
//  GatewayMappingCodec - doc / ghi object InfoGatewayMapping
//  tren VOS3000 (TCP 1202) theo Java serialization.
//
//  Nguon API chi tra ve danh sach so. Tren VOS3000, danh sach so
//  ("calloutCallerPrefixes") nam RIENG cho tung gateway mapping =
//  tung account customer. Muon "mo / dong" dong bo cho MOI account
//  thi phai ghi vao tat ca mapping.
//
//  File nay KHONG con tim classdesc bang cach quet chuoi. No doc
//  toan bo Java object stream theo dung thu tu:
//    - TC_OBJECT / TC_CLASSDESC / TC_REFERENCE / TC_STRING / TC_ARRAY
//    - handle table (TC_REFERENCE) dung chung cho ca stream
//    - classdesc long nhau (InfoMbx -> InfoMo -> Info)
//    - data ghi theo thu tu lop CHA truoc, lop CON sau
//
//  Khi ghi, object InfoGatewayMapping duoc lay tu GET live (day du
//  68 field cua dung account) roi serialize lai thanh packet SET.
//  Nho vay khong dung lai template cua account khac (tranh ghi de
//  customerAccount / password / cac cau hinh rieng).
// ============================================================
using System.Text;

namespace Vos3000Agent
{
    // ------------------------------------------------------------
    //  Mo hinh Java serialization (chi phan can cho VOS)
    // ------------------------------------------------------------

    public sealed class JavaFieldDesc
    {
        public string Name = "";
        public char Tc;
        public string TypeSig = "";
        public bool IsString => Tc == 'L' || Tc == '[';
        public bool IsPrimitive => "BZCSIJFD".IndexOf(Tc) >= 0;
    }

    public sealed class JavaClassDesc : JavaValue
    {
        public string Name = "";
        public long Suid;
        public byte Flags;
        public List<JavaFieldDesc> Fields = new();
        public JavaClassDesc Super;
        public byte[] Annotations = Array.Empty<byte>();

        public bool IsMappingClass =>
            string.Equals(Name, GatewayMappingCodec.MappingClass, StringComparison.Ordinal);

        public bool IsRoutingClass =>
            string.Equals(Name, GatewayMappingCodec.RoutingClass, StringComparison.Ordinal);

        /// <summary>True neu chinh class nay hoac lop cha la InfoGatewayMapping.</summary>
        public bool IsMappingHierarchy =>
            IsMappingClass || (Super?.IsMappingHierarchy ?? false);

        /// <summary>True neu chinh class nay hoac lop cha la InfoGatewayRouting.</summary>
        public bool IsRoutingHierarchy =>
            IsRoutingClass || (Super?.IsRoutingHierarchy ?? false);

        /// <summary>Tra ve cac lop theo thu tu cha -> con (dung cho DATA).</summary>
        public IEnumerable<JavaClassDesc> SuperFirst()
        {
            if (Super != null)
                foreach (var c in Super.SuperFirst()) yield return c;
            yield return this;
        }

        public IEnumerable<JavaFieldDesc> AllFieldsSuperFirst()
        {
            foreach (var c in SuperFirst())
                foreach (var f in c.Fields) yield return f;
        }
    }

    public abstract class JavaValue { }

    public sealed class JavaNull : JavaValue
    {
        public static readonly JavaNull Instance = new();
        private JavaNull() { }
    }

    public sealed class JavaString : JavaValue
    {
        public string Value = "";
        public int Pos = -1;        // vi tri 0x74 trong buffer goc
        public int Len = -1;        // do dai byte UTF-8
    }

    public sealed class JavaNumber : JavaValue
    {
        public char Tc;
        public long Bits;           // raw bits (F/D cung luu raw)
    }

    public sealed class JavaBlock : JavaValue
    {
        public byte[] Data = Array.Empty<byte>();
    }

    public sealed class JavaObject : JavaValue
    {
        public JavaClassDesc Desc;
        public List<JavaValue> Values = new();

        /// <summary>
        /// Du lieu "custom" cua SC_WRITE_METHOD (vd java.util.Vector) theo dung thu tu
        /// Desc.SuperFirst(); phan tu null = lop khong co custom data.
        /// </summary>
        public List<byte[]> CustomData = new();

        /// <summary>Lay custom data cua 1 lop (theo thu tu super-first) hoac null.</summary>
        public byte[] GetCustomData(JavaClassDesc cls)
        {
            int i = 0;
            foreach (var c in Desc.SuperFirst())
            {
                if (ReferenceEquals(c, cls)) return i < CustomData.Count ? CustomData[i] : null;
                i++;
            }
            return null;
        }

        /// <summary>Gan custom data cho 1 lop (theo thu tu super-first).</summary>
        public void SetCustomData(JavaClassDesc cls, byte[] data)
        {
            int i = 0;
            foreach (var c in Desc.SuperFirst())
            {
                if (ReferenceEquals(c, cls))
                {
                    while (CustomData.Count <= i) CustomData.Add(null);
                    CustomData[i] = data;
                    return;
                }
                i++;
            }
        }

        public int IndexOf(string name)
        {
            int i = 0;
            foreach (var f in Desc.AllFieldsSuperFirst())
            {
                if (string.Equals(f.Name, name, StringComparison.Ordinal)) return i;
                i++;
            }
            return -1;
        }

        public JavaValue Get(string name)
        {
            int i = IndexOf(name);
            return i >= 0 && i < Values.Count ? Values[i] : null;
        }

        public void Set(string name, JavaValue value)
        {
            int i = IndexOf(name);
            if (i < 0) return;
            while (Values.Count <= i) Values.Add(JavaNull.Instance);
            Values[i] = value;
        }

        public string GetString(string name)
        {
            var v = Get(name);
            return v is JavaString s ? s.Value : null;
        }

        public long? GetNumber(string name)
        {
            var v = Get(name);
            if (v is not JavaNumber n) return null;
            return n.Tc switch
            {
                'B' => (sbyte)n.Bits,
                'Z' => n.Bits != 0 ? 1 : 0,
                'S' => (short)n.Bits,
                'C' => (char)n.Bits,
                'I' => (int)n.Bits,
                'J' => n.Bits,
                _ => n.Bits
            };
        }

        public JavaObject CloneDeep()
        {
            var o = new JavaObject { Desc = Desc };
            foreach (var v in Values) o.Values.Add(CloneValue(v));
            foreach (var d in CustomData) o.CustomData.Add(d == null ? null : (byte[])d.Clone());
            return o;
        }

        public static JavaValue CloneValue(JavaValue v) => v switch
        {
            JavaObject o => o.CloneDeep(),
            JavaArray a => a.CloneDeep(),
            JavaString s => new JavaString { Value = s.Value },
            JavaNumber n => new JavaNumber { Tc = n.Tc, Bits = n.Bits },
            JavaBlock b => new JavaBlock { Data = (byte[])b.Data.Clone() },
            _ => v ?? JavaNull.Instance
        };
    }

    public sealed class JavaArray : JavaValue
    {
        public JavaClassDesc Desc;
        public int Length;
        public List<JavaValue> Items = new();

        public char ElementType
        {
            get
            {
                var n = Desc?.Name ?? "";
                if (n.Length < 2 || n[0] != '[') return 'L';
                return n[1] == 'L' || n[1] == '[' ? 'L' : n[1];
            }
        }

        public JavaArray CloneDeep()
        {
            var a = new JavaArray { Desc = Desc, Length = Length };
            foreach (var v in Items) a.Items.Add(JavaObject.CloneValue(v));
            return a;
        }
    }

    // ------------------------------------------------------------
    //  Object InfoGatewayMapping (1 mapping = 1 account)
    // ------------------------------------------------------------

    public sealed class GatewayMappingObject
    {
        public JavaObject Node;
        public byte[] Raw;

        public long Id => Node?.GetNumber("id") ?? 0;
        public long CustomerId => Node?.GetNumber("customer_id") ?? 0;

        public string Account =>
            Node?.GetString("customerAccount") ?? Node?.GetString("name") ?? "";

        public string CustomerName => Node?.GetString("customerName") ?? "";

        public string CalloutCallerPrefixes => Node?.GetString("calloutCallerPrefixes");

        public string GetString(string name) => Node?.GetString(name);
        public long? GetNumber(string name) => Node?.GetNumber(name);

        public GatewayMappingObject Clone() => new()
        {
            Node = Node?.CloneDeep(),
            Raw = Raw
        };
    }

    // ------------------------------------------------------------
    //  Reader / writer Java serialization
    // ------------------------------------------------------------

    internal sealed class JavaStreamReader
    {
        const int BaseWireHandle = 0x7E0000;

        readonly byte[] _b;
        readonly List<object> _handles = new();
        int _p;

        public JavaStreamReader(byte[] buf, int start = 0)
        {
            _b = buf ?? throw new ArgumentNullException(nameof(buf));
            _p = start;
            if (_p + 4 <= _b.Length && _b[_p] == 0xAC && _b[_p + 1] == 0xED)
                _p += 4;                        // Java stream magic + version
        }

        public int Position => _p;
        public int Length => _b.Length;

        public bool HasMore => _p < _b.Length && _b[_p] != 0x00;

        public JavaValue ReadValue()
        {
            if (_p >= _b.Length) throw new EndOfStreamException();
            byte t = _b[_p];

            switch (t)
            {
                case 0x70:                      // TC_NULL
                    _p++;
                    return JavaNull.Instance;

                case 0x71:                      // TC_REFERENCE
                    _p++;
                    return ResolveValue(ReadHandle());

                case 0x72:                      // TC_CLASSDESC
                    return ReadClassDesc();

                case 0x73:                      // TC_OBJECT
                    return ReadObject();

                case 0x74:                      // TC_STRING
                    {
                        int pos = _p;
                        _p++;
                        string s = ReadUtf();
                        var v = new JavaString { Value = s, Pos = pos, Len = Encoding.UTF8.GetByteCount(s) };
                        _handles.Add(v);
                        return v;
                    }

                case 0x7C:                      // TC_LONGSTRING
                    {
                        _p++;
                        long n = ReadInt64();
                        if (n < 0 || n > int.MaxValue || _p + (int)n > _b.Length) throw new EndOfStreamException();
                        string s = Encoding.UTF8.GetString(_b, _p, (int)n);
                        _p += (int)n;
                        var v = new JavaString { Value = s, Len = (int)n };
                        _handles.Add(v);
                        return v;
                    }

                case 0x75:                      // TC_ARRAY
                    return ReadArray();

                case 0x77:                      // TC_BLOCKDATA
                    {
                        _p++;
                        if (_p >= _b.Length) throw new EndOfStreamException();
                        int n = _b[_p++];
                        if (_p + n > _b.Length) throw new EndOfStreamException();
                        var data = new byte[n];
                        Array.Copy(_b, _p, data, 0, n);
                        _p += n;
                        return new JavaBlock { Data = data };
                    }

                case 0x7A:                      // TC_BLOCKDATALONG
                    {
                        _p++;
                        int n = ReadInt32();
                        if (n < 0 || _p + n > _b.Length) throw new EndOfStreamException();
                        var data = new byte[n];
                        Array.Copy(_b, _p, data, 0, n);
                        _p += n;
                        return new JavaBlock { Data = data };
                    }

                case 0x78:                      // TC_ENDBLOCK
                    _p++;
                    return JavaNull.Instance;

                case 0x79:                      // TC_RESET
                    _p++;
                    _handles.Clear();
                    return ReadValue();

                default:
                    throw new InvalidDataException($"Java token 0x{t:X2} tai {_p}");
            }
        }

        JavaObject ReadObject()
        {
            _p++;                               // TC_OBJECT
            var desc = ReadClassDesc();
            if (desc == null) throw new InvalidDataException("TC_OBJECT khong co classdesc");

            var obj = new JavaObject { Desc = desc };
            _handles.Add(obj);                  // handle cua object (sau classdesc)

            foreach (var c in desc.SuperFirst())
            {
                foreach (var f in c.Fields)
                    obj.Values.Add(ReadFieldValue(f));

                if ((c.Flags & 0x01) != 0)
                {
                    int start = _p;
                    ReadAnnotations();          // SC_WRITE_METHOD: doc va giu lai custom data
                    var data = new byte[_p - start];
                    Array.Copy(_b, start, data, 0, data.Length);
                    obj.CustomData.Add(data);
                }
                else obj.CustomData.Add(null);
            }
            return obj;
        }

        JavaArray ReadArray()
        {
            _p++;                               // TC_ARRAY
            var desc = ReadClassDesc();
            if (desc == null) throw new InvalidDataException("TC_ARRAY khong co classdesc");

            var arr = new JavaArray { Desc = desc };
            _handles.Add(arr);
            arr.Length = ReadInt32();
            if (arr.Length < 0 || arr.Length > 10_000_000) throw new InvalidDataException("array qua lon");

            char et = arr.ElementType;
            for (int i = 0; i < arr.Length; i++)
                arr.Items.Add(et == 'L' ? ReadValue() : ReadPrimitive(et));
            return arr;
        }

        JavaValue ReadFieldValue(JavaFieldDesc f)
            => f.IsString ? ReadValue() : ReadPrimitive(f.Tc);

        JavaValue ReadPrimitive(char tc)
        {
            switch (tc)
            {
                case 'B': { sbyte v = unchecked((sbyte)_b[_p]); _p++; return Num(tc, v); }
                case 'Z': { long v = _b[_p] != 0 ? 1 : 0; _p++; return Num(tc, v); }
                case 'C': { ushort v = (ushort)((_b[_p] << 8) | _b[_p + 1]); _p += 2; return Num(tc, v); }
                case 'S': { short v = (short)((_b[_p] << 8) | _b[_p + 1]); _p += 2; return Num(tc, v); }
                case 'I': { int v = ReadInt32(); return Num(tc, v); }
                case 'F': { int v = ReadInt32(); return Num(tc, v); }
                case 'J': { long v = ReadInt64(); return Num(tc, v); }
                case 'D': { long v = ReadInt64(); return Num(tc, v); }
                default: throw new InvalidDataException($"primitive type '{tc}' khong ho tro");
            }
        }

        static JavaNumber Num(char tc, long bits) => new() { Tc = tc, Bits = bits };

        JavaClassDesc ReadClassDesc()
        {
            if (_p >= _b.Length) throw new EndOfStreamException();
            byte t = _b[_p];

            if (t == 0x70) { _p++; return null; }           // TC_NULL
            if (t == 0x71)                                  // TC_REFERENCE
            {
                _p++;
                var h = ReadHandle();
                return Resolve(h) as JavaClassDesc
                    ?? throw new InvalidDataException($"handle {h} khong phai classdesc");
            }
            if (t != 0x72) throw new InvalidDataException($"classdesc token 0x{t:X2} tai {_p}");

            _p++;                                           // TC_CLASSDESC
            string name = ReadUtf();
            long suid = ReadInt64();

            var desc = new JavaClassDesc { Name = name, Suid = suid };
            _handles.Add(desc);                             // handle duoc cap sau ten + suid

            if (_p >= _b.Length) throw new EndOfStreamException();
            desc.Flags = _b[_p++];
            int nf = ReadU16();

            for (int i = 0; i < nf; i++)
            {
                if (_p + 3 > _b.Length) throw new EndOfStreamException();
                char tc = (char)_b[_p++];
                string fn = ReadUtf();
                string sig = "";
                if (tc == 'L' || tc == '[')
                {
                    if (_p >= _b.Length) throw new EndOfStreamException();
                    if (_b[_p] == 0x74)
                    {
                        int sigPos = _p;
                        _p++;
                        sig = ReadUtf();
                        _handles.Add(new JavaString { Value = sig, Pos = sigPos, Len = Encoding.UTF8.GetByteCount(sig) });
                    }
                    else if (_b[_p] == 0x71)
                    {
                        _p++;
                        var h = ReadHandle();
                        sig = (Resolve(h) as JavaString)?.Value ?? "";
                    }
                    else throw new InvalidDataException("field signature khong phai TC_STRING/TC_REFERENCE");
                }
                desc.Fields.Add(new JavaFieldDesc { Name = fn, Tc = tc, TypeSig = sig });
            }

            desc.Annotations = ReadAnnotationsRaw();
            desc.Super = ReadClassDesc();
            return desc;
        }

        /// <summary>Doc annotation cua classdesc, tra ve raw bytes (gồm ca TC_ENDBLOCK cuoi).</summary>
        byte[] ReadAnnotationsRaw()
        {
            int start = _p;
            while (true)
            {
                if (_p >= _b.Length) throw new EndOfStreamException();
                if (_b[_p] == 0x78) { _p++; break; }
                ReadValue();
            }
            var data = new byte[_p - start];
            Array.Copy(_b, start, data, 0, data.Length);
            return data;
        }

        /// <summary>Doc annotation cua instance (SC_WRITE_METHOD), bo qua noi dung.</summary>
        void ReadAnnotations()
        {
            while (true)
            {
                if (_p >= _b.Length) throw new EndOfStreamException();
                if (_b[_p] == 0x78) { _p++; break; }
                ReadValue();
            }
        }

        JavaValue ResolveValue(int handle)
        {
            var o = Resolve(handle);
            return o as JavaValue ?? throw new InvalidDataException($"handle {handle} khong phai value");
        }

        object Resolve(int handle)
        {
            int idx = handle - BaseWireHandle;
            if (idx < 0 || idx >= _handles.Count)
                throw new InvalidDataException($"handle {handle} ngoai pham vi ({_handles.Count})");
            return _handles[idx];
        }

        int ReadHandle() => ReadInt32();

        int ReadInt32()
        {
            if (_p + 4 > _b.Length) throw new EndOfStreamException();
            int v = (_b[_p] << 24) | (_b[_p + 1] << 16) | (_b[_p + 2] << 8) | _b[_p + 3];
            _p += 4;
            return v;
        }

        long ReadInt64()
        {
            if (_p + 8 > _b.Length) throw new EndOfStreamException();
            long v = ((long)_b[_p] << 56) | ((long)_b[_p + 1] << 48) | ((long)_b[_p + 2] << 40) | ((long)_b[_p + 3] << 32)
                   | ((long)_b[_p + 4] << 24) | ((long)_b[_p + 5] << 16) | ((long)_b[_p + 6] << 8) | _b[_p + 7];
            _p += 8;
            return v;
        }

        int ReadU16()
        {
            if (_p + 2 > _b.Length) throw new EndOfStreamException();
            int v = (_b[_p] << 8) | _b[_p + 1];
            _p += 2;
            return v;
        }

        string ReadUtf()
        {
            int n = ReadU16();
            if (_p + n > _b.Length) throw new EndOfStreamException();
            string s = Encoding.UTF8.GetString(_b, _p, n);
            _p += n;
            return s;
        }
    }

    internal sealed class JavaStreamWriter
    {
        readonly MemoryStream _ms = new();

        public JavaStreamWriter()
        {
            // Java Object Serialization Stream Protocol: magic + version.
            _ms.WriteByte(0xAC);
            _ms.WriteByte(0xED);
            _ms.WriteByte(0x00);
            _ms.WriteByte(0x05);
        }

        public byte[] ToArray() => _ms.ToArray();

        public void WriteValue(JavaValue v)
        {
            switch (v)
            {
                case null:
                case JavaNull:
                    W(0x70);
                    break;
                case JavaString s:
                    W(0x74);
                    WriteUtf(s.Value ?? "");
                    break;
                case JavaNumber n:
                    WritePrimitive(n.Tc, n);
                    break;
                case JavaObject o:
                    WriteObject(o);
                    break;
                case JavaArray a:
                    WriteArray(a);
                    break;
                case JavaBlock b:
                    WriteRaw(b.Data);
                    break;
                case JavaClassDesc cd:
                    WriteClassDesc(cd);
                    break;
                default:
                    W(0x70);
                    break;
            }
        }

        void WriteObject(JavaObject o)
        {
            W(0x73);
            WriteClassDesc(o.Desc);

            int idx = 0;
            int ci = 0;
            foreach (var c in o.Desc.SuperFirst())
            {
                foreach (var f in c.Fields)
                {
                    var v = idx < o.Values.Count ? o.Values[idx] : null;
                    if (f.IsString) WriteValue(v);
                    else WritePrimitive(f.Tc, v as JavaNumber);
                    idx++;
                }

                if ((c.Flags & 0x01) != 0)
                {
                    var custom = ci < o.CustomData.Count ? o.CustomData[ci] : null;
                    if (custom != null && custom.Length > 0) WriteRaw(custom);
                    else W(0x78);
                }
                ci++;
            }
        }

        void WriteArray(JavaArray a)
        {
            W(0x75);
            WriteClassDesc(a.Desc);
            WriteInt32(a.Length);
            char et = a.ElementType;
            for (int i = 0; i < a.Length; i++)
            {
                var v = i < a.Items.Count ? a.Items[i] : null;
                if (et == 'L') WriteValue(v);
                else WritePrimitive(et, v as JavaNumber);
            }
        }

        void WriteClassDesc(JavaClassDesc d)
        {
            if (d == null) { W(0x70); return; }

            W(0x72);
            WriteUtf(d.Name);
            WriteInt64(d.Suid);
            W(d.Flags);
            WriteU16(d.Fields.Count);

            foreach (var f in d.Fields)
            {
                W((byte)f.Tc);
                WriteUtf(f.Name);
                if (f.IsString)
                {
                    W(0x74);
                    WriteUtf(f.TypeSig ?? "");
                }
            }

            if (d.Annotations != null && d.Annotations.Length > 0) WriteRaw(d.Annotations);
            else W(0x78);

            if (d.Super != null) WriteClassDesc(d.Super);
            else W(0x70);
        }

        void WritePrimitive(char tc, JavaNumber n)
        {
            long bits = n?.Bits ?? 0;
            switch (tc)
            {
                case 'B': W((byte)bits); break;
                case 'Z': W(bits != 0 ? (byte)1 : (byte)0); break;
                case 'C': WriteU16((int)bits); break;
                case 'S': WriteU16((int)bits); break;
                case 'I': WriteInt32((int)bits); break;
                case 'F': WriteInt32((int)bits); break;
                case 'J': WriteInt64(bits); break;
                case 'D': WriteInt64(bits); break;
                default: throw new InvalidDataException($"khong ghi duoc primitive '{tc}'");
            }
        }

        void WriteUtf(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > 0xFFFF) throw new InvalidDataException("UTF qua 65535 byte");
            WriteU16(b.Length);
            WriteRaw(b);
        }

        void WriteU16(int v) { _ms.WriteByte((byte)(v >> 8)); _ms.WriteByte((byte)v); }

        void WriteInt32(int v)
        {
            _ms.WriteByte((byte)(v >> 24));
            _ms.WriteByte((byte)(v >> 16));
            _ms.WriteByte((byte)(v >> 8));
            _ms.WriteByte((byte)v);
        }

        void WriteInt64(long v)
        {
            _ms.WriteByte((byte)(v >> 56));
            _ms.WriteByte((byte)(v >> 48));
            _ms.WriteByte((byte)(v >> 40));
            _ms.WriteByte((byte)(v >> 32));
            _ms.WriteByte((byte)(v >> 24));
            _ms.WriteByte((byte)(v >> 16));
            _ms.WriteByte((byte)(v >> 8));
            _ms.WriteByte((byte)v);
        }

        void WriteRaw(byte[] data) => _ms.Write(data, 0, data.Length);
        void W(byte b) => _ms.WriteByte(b);
    }

    public static class GatewayMappingCodec
    {
        public const byte TC_STRING = 0x74;
        public const byte TC_REFERENCE = 0x71;
        public const byte TC_NULL = 0x70;
        public const byte TC_BLOCKDATA = 0x77;
        public const byte TC_ENDBLOCK = 0x78;
        public const byte TC_CLASSDESC = 0x72;
        public const byte TC_OBJECT = 0x73;

        public const string MappingClass = "com.kunshi.interfacef.vos2000.InfoGatewayMapping";
        public const string RoutingClass = "com.kunshi.interfacef.vos2000.InfoGatewayRouting";
        public const string ParentClass = "com.kunshi.interfacef.common.Info";
        public const string FilterClass = "com.kunshi.interfacef.vos2000.FilterGatewayMapping";
        public const string SetListClass = "com.kunshi.interfacef.common.ReqSetInfoList";
        public const string DefaultPrefix = "1;";

        // ---------- tim object ----------

        public static int IndexOf(byte[] hay, byte[] needle, int from = 0)
        {
            if (hay == null || needle == null) return -1;
            for (int i = Math.Max(0, from); i + needle.Length <= hay.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>
        /// Tìm packet GET (ReqGetInfo) trong buffer, ưu tiên packet chứa class trong <paramref name="preferClass"/>.
        /// Dùng cho live traffic/proxy capture: 1 chunk có thể gồm nhiều packet nối tiếp.
        /// </summary>
        public static byte[] FindGetPacket(byte[] buf, string preferClass)
        {
            if (buf == null || buf.Length == 0) return null;
            byte[] best = null;
            for (int i = 0; i < buf.Length; i++)
            {
                var pkt = VosProto.PacketAt(buf, i);
                if (pkt == null) continue;
                if (!VosProto.TryDecompressFrom(pkt, VosProto.HeaderLen, out var obj)) continue;
                var txt = Encoding.UTF8.GetString(obj);
                if (!txt.Contains("ReqGetInfo") && !txt.Contains("Filter")) continue;

                if (preferClass != null && txt.Contains(preferClass)) return pkt;
                best ??= pkt;
            }
            return best;
        }

        /// <summary>Dựng lại packet (hdr của template + payload đã nén) từ object Java đã parse.</summary>
        public static byte[] RebuildPacket(byte[] templatePkt, byte[] javaStream)
        {
            if (templatePkt == null || javaStream == null) return null;
            var hdr = new byte[VosProto.HeaderLen];
            Array.Copy(templatePkt, hdr, VosProto.HeaderLen);
            return VosProto.BuildPacket(hdr, VosProto.CompressGzip(javaStream));
        }

        /// <summary>
        /// Dựng packet SET cho InfoGatewayRouting (call-in) từ template ReqSetInfoList + object GET live.
        /// Chỉ thay field prefix; các field khác giữ nguyên (kể cả InfoMbx lồng nhau).
        /// </summary>
        public static byte[] BuildRoutingSetPacket(byte[] templatePkt, JavaObject live, string newPrefix, Action<string> log = null)
        {
            if (templatePkt == null || live?.Desc == null) return null;
            try
            {
                if (!VosProto.TryDecompressFrom(templatePkt, VosProto.HeaderLen, out var tplObj))
                {
                    log?.Invoke("        !! khong giai nen duoc template SET routing");
                    return null;
                }

                var root = ParseStreamValue(tplObj) as JavaObject;
                if (root == null || !string.Equals(root.Desc?.Name, SetListClass, StringComparison.Ordinal))
                {
                    log?.Invoke("        !! template SET routing khong phai ReqSetInfoList");
                    return null;
                }

                var routeNode = live.CloneDeep();
                routeNode.Set("prefix", new JavaString { Value = newPrefix ?? "" });

                return RebuildSetPacket(root, routeNode, templatePkt);
            }
            catch (Exception e)
            {
                log?.Invoke("        !! dung packet SET routing loi: " + e.Message);
                return null;
            }
        }

        /// <summary>Prefix cua routing: danh sach thuan "84...,#84..." (khong co "x;").</summary>
        public static string RoutingPrefix(JavaObject o) => o?.GetString("prefix") ?? "";

        /// <summary>Ghi prefix (danh sach thuan) cho 1 object InfoGatewayRouting.</summary>
        public static void PatchRoutingPrefix(JavaObject o, string value) =>
            o?.Set("prefix", new JavaString { Value = value ?? "" });

        /// <summary>Dung packet SET cho InfoGatewayMapping (call-out) tu template + object GET live.</summary>
        public static byte[] BuildMappingSetPacket(byte[] templatePkt, GatewayMappingObject live, string newPrefix, Action<string> log = null)
        {
            if (templatePkt == null || live?.Node == null) return null;
            try
            {
                if (!VosProto.TryDecompressFrom(templatePkt, VosProto.HeaderLen, out var tplObj))
                {
                    log?.Invoke("        !! khong giai nen duoc template SET");
                    return null;
                }

                var root = ParseStreamValue(tplObj) as JavaObject;
                if (root == null || !string.Equals(root.Desc?.Name, SetListClass, StringComparison.Ordinal))
                {
                    log?.Invoke("        !! template SET khong phai ReqSetInfoList");
                    return null;
                }

                var val = NormalizePrefixValue(newPrefix);
                var mapNode = live.Node.CloneDeep();
                mapNode.Set("calloutCallerPrefixes", new JavaString { Value = val });

                return RebuildSetPacket(root, mapNode, templatePkt);
            }
            catch (Exception e)
            {
                log?.Invoke("        !! dung packet SET loi: " + e.Message);
                return null;
            }
        }

        static byte[] RebuildSetPacket(JavaObject root, JavaObject infoNode, byte[] templatePkt)
        {
            var create = root.Get("createInfoList") as JavaObject;
            var vectorDesc = create?.Desc;
            var arrayDesc = (create?.Get("elementData") as JavaArray)?.Desc;
            var modify = root.Get("modifyInfoList") as JavaObject;
            var modifyArr = modify?.Get("elementData") as JavaArray;
            var setInfo = modifyArr?.Items?.OfType<JavaObject>().FirstOrDefault();
            var reqSetInfoDesc = setInfo?.Desc;

            if (vectorDesc == null || arrayDesc == null || reqSetInfoDesc == null) return null;

            var setInfoNode = new JavaObject
            {
                Desc = reqSetInfoDesc,
                Values = new List<JavaValue> { new JavaNumber { Tc = 'I', Bits = 0 }, infoNode }
            };

            var arr = NewObjectArray(arrayDesc, setInfoNode);
            var empty1 = NewEmptyVector(vectorDesc, arrayDesc);
            var empty2 = NewEmptyVector(vectorDesc, arrayDesc);
            var empty3 = NewEmptyVector(vectorDesc, arrayDesc);
            var modifyVec = new JavaObject
            {
                Desc = vectorDesc,
                Values = new List<JavaValue>
                {
                    new JavaNumber { Tc = 'I', Bits = 0 },
                    new JavaNumber { Tc = 'I', Bits = 1 },
                    arr
                }
            };

            var root2 = new JavaObject
            {
                Desc = root.Desc,
                Values = new List<JavaValue>
                {
                    new JavaNumber { Tc = 'I', Bits = 0 },
                    empty1, empty2, empty3, modifyVec
                }
            };

            var writer = new JavaStreamWriter();
            writer.WriteValue(root2);
            return RebuildPacket(templatePkt, writer.ToArray());
        }

        /// <summary>Vi tri bat dau ten class cua classdesc (bo qua 72 len). -1 neu khong co.</summary>
        public static int FindClassDesc(byte[] obj, string name, int from = 0)
            => VosProto.FindClassDesc(obj, name, from);

        public static bool IsMappingObject(byte[] obj) =>
            obj != null
            && IndexOf(obj, Encoding.ASCII.GetBytes(MappingClass)) >= 0
            && IndexOf(obj, Encoding.ASCII.GetBytes("calloutCallerPrefixes")) >= 0;

        /// <summary>Doc toan bo stream, tra ve cac mapping object (bo qua object khac).</summary>
        public static List<GatewayMappingObject> ParseAll(byte[] buf, Action<string> log = null)
        {
            var res = new List<GatewayMappingObject>();
            if (buf == null || buf.Length == 0) return res;

            try
            {
                var reader = new JavaStreamReader(buf);
                var seen = new HashSet<JavaObject>();
                while (reader.HasMore)
                {
                    int before = reader.Position;
                    JavaValue root;
                    try { root = reader.ReadValue(); }
                    catch (Exception e)
                    {
                        log?.Invoke($"  !! parse stream dung tai byte {reader.Position}: {e.Message}");
                        break;
                    }
                    if (reader.Position <= before) break;

                    var objects = new List<JavaObject>();
                    CollectMappings(root, objects, seen);
                    foreach (var o in objects)
                        res.Add(new GatewayMappingObject { Node = o, Raw = buf });
                }
            }
            catch (Exception e)
            {
                log?.Invoke("  !! parse mapping loi: " + e.Message);
            }
            return res;
        }

        static void CollectMappings(JavaValue v, List<JavaObject> outList, HashSet<JavaObject> seen)
        {
            switch (v)
            {
                case JavaObject o:
                    if (!seen.Add(o)) return;
                    if (o.Desc != null && o.Desc.IsMappingHierarchy) outList.Add(o);
                    foreach (var x in o.Values) CollectMappings(x, outList, seen);
                    break;
                case JavaArray a:
                    foreach (var x in a.Items) CollectMappings(x, outList, seen);
                    break;
            }
        }

        /// <summary>Doc toan bo stream, tra ve cac object InfoGatewayRouting (call-in).</summary>
        public static List<JavaObject> ParseRoutingObjects(byte[] buf, Action<string> log = null)
        {
            var res = new List<JavaObject>();
            if (buf == null || buf.Length == 0) return res;
            try
            {
                var reader = new JavaStreamReader(buf);
                var seen = new HashSet<JavaObject>();
                while (reader.HasMore)
                {
                    int before = reader.Position;
                    JavaValue root;
                    try { root = reader.ReadValue(); }
                    catch (Exception e)
                    {
                        log?.Invoke($"  !! parse stream dung tai byte {reader.Position}: {e.Message}");
                        break;
                    }
                    if (reader.Position <= before) break;
                    CollectRoutings(root, res, seen);
                }
            }
            catch (Exception e)
            {
                log?.Invoke("  !! parse routing loi: " + e.Message);
            }
            return res;
        }

        static void CollectRoutings(JavaValue v, List<JavaObject> outList, HashSet<JavaObject> seen)
        {
            switch (v)
            {
                case JavaObject o:
                    if (!seen.Add(o)) return;
                    if (o.Desc != null && o.Desc.IsRoutingHierarchy) outList.Add(o);
                    foreach (var x in o.Values) CollectRoutings(x, outList, seen);
                    break;
                case JavaArray a:
                    foreach (var x in a.Items) CollectRoutings(x, outList, seen);
                    break;
            }
        }

        /// <summary>
        /// Nhan dien loai object: "mapping" (InfoGatewayMapping), "routing" (InfoGatewayRouting),
        /// null neu khac. Dung khi ghep packet SET voi object GET tuong ung.
        /// </summary>
        public static string ClassifyObject(JavaObject o)
        {
            if (o?.Desc == null) return null;
            if (o.Desc.IsMappingHierarchy) return "mapping";
            if (o.Desc.IsRoutingHierarchy) return "routing";
            return null;
        }

        /// <summary>Loc Filter: dat agentAccount rong de server tra ve moi routing (call-in).</summary>
        public static byte[] BroadcastRoutingFilter(byte[] obj)
        {
            if (obj == null) return null;
            try
            {
                var root = ParseStreamValue(obj) as JavaObject;
                if (root == null) return null;

                root.Set("agentAccount", new JavaString { Value = "" });

                var inner = root.Get("info") as JavaObject;
                if (inner == null)
                {
                    inner = new JavaObject { Desc = root.Desc };
                    root.Set("info", inner);
                }
                inner.Set("clearingCustomerAccount", new JavaString { Value = "*" });

                var writer = new JavaStreamWriter();
                writer.WriteValue(root);
                return writer.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Doc 1 object trong buffer (tuong thich API cu).</summary>
        public static GatewayMappingObject Parse(byte[] buf, int at)
        {
            var all = ParseAll(buf);
            if (all.Count == 0) return null;
            return all[0];
        }

        /// <summary>Doc top-level value dau tien (dung cho template SET / filter).</summary>
        public static JavaValue ParseStreamValue(byte[] buf)
        {
            if (buf == null || buf.Length == 0) return null;
            var r = new JavaStreamReader(buf);
            return r.HasMore ? r.ReadValue() : null;
        }

        // ---------- ghi / patch ----------

        /// <summary>Patch 1 chuoi TC_STRING tai vi tri da biet. Tra object moi.</summary>
        public static byte[] PatchStringAt(byte[] obj, int pos, int oldLen, string newValue)
        {
            if (obj == null || pos < 0) return null;
            newValue ??= "";
            var nb = Encoding.UTF8.GetBytes(newValue);
            int tail = pos + 3 + oldLen;
            if (tail > obj.Length) return null;
            var res = new byte[pos + 3 + nb.Length + (obj.Length - tail)];
            Array.Copy(obj, res, pos);
            res[pos] = TC_STRING;
            res[pos + 1] = (byte)(nb.Length >> 8);
            res[pos + 2] = (byte)(nb.Length & 0xFF);
            Array.Copy(nb, 0, res, pos + 3, nb.Length);
            Array.Copy(obj, tail, res, pos + 3 + nb.Length, obj.Length - tail);
            return res;
        }

        public static byte[] PatchString(GatewayMappingObject m, string field, string newValue)
        {
            if (m?.Node == null || m.Raw == null) return null;
            var v = m.Node.Get(field) as JavaString;
            if (v == null || v.Pos < 0) return null;
            return PatchStringAt(m.Raw, v.Pos, v.Len, newValue);
        }

        /// <summary>Chuan hoa chuoi prefix truoc khi ghi de server khong coi la "rong".</summary>
        public static string NormalizePrefixValue(string p)
        {
            var s = (p ?? "").Trim();
            if (s.Length == 0) return DefaultPrefix;
            if (s.IndexOf(';') < 0) s += ";";
            if (!VosProto.IsPrefixLike(s)) s = DefaultPrefix + s;
            return s;
        }

        /// <summary>
        /// Dung packet SET tu template, nhung object InfoGatewayMapping duoc lay
        /// tu GET live (day du field cua dung account), chi thay
        /// calloutCallerPrefixes. Khong dung lai field cua account khac.
        /// </summary>
        public static byte[] BuildSetPacket(byte[] templatePkt, GatewayMappingObject live, string newPrefix, Action<string> log = null)
        {
            if (templatePkt == null || live?.Node == null) return null;
            try
            {
                if (!VosProto.TryDecompressFrom(templatePkt, VosProto.HeaderLen, out var tplObj))
                {
                    log?.Invoke("        !! khong giai nen duoc template SET");
                    return null;
                }

                var root = ParseStreamValue(tplObj) as JavaObject;
                if (root == null || !string.Equals(root.Desc?.Name, SetListClass, StringComparison.Ordinal))
                {
                    log?.Invoke("        !! template SET khong phai ReqSetInfoList");
                    return null;
                }

                var create = root.Get("createInfoList") as JavaObject;
                var vectorDesc = create?.Desc;
                var arrayDesc = (create?.Get("elementData") as JavaArray)?.Desc;
                var modify = root.Get("modifyInfoList") as JavaObject;
                var modifyArr = modify?.Get("elementData") as JavaArray;
                var setInfo = modifyArr?.Items?.OfType<JavaObject>().FirstOrDefault();
                var reqSetInfoDesc = setInfo?.Desc;

                if (vectorDesc == null || arrayDesc == null || reqSetInfoDesc == null)
                {
                    log?.Invoke("        !! template SET thieu Vector/ReqSetInfo");
                    return null;
                }

                var mapNode = live.Node.CloneDeep();
                mapNode.Set("calloutCallerPrefixes", new JavaString { Value = NormalizePrefixValue(newPrefix) });

                var setInfoNode = new JavaObject
                {
                    Desc = reqSetInfoDesc,
                    Values = new List<JavaValue> { new JavaNumber { Tc = 'I', Bits = 0 }, mapNode }
                };

                var arr = NewObjectArray(arrayDesc, setInfoNode);
                var empty1 = NewEmptyVector(vectorDesc, arrayDesc);
                var empty2 = NewEmptyVector(vectorDesc, arrayDesc);
                var empty3 = NewEmptyVector(vectorDesc, arrayDesc);
                var modifyVec = new JavaObject
                {
                    Desc = vectorDesc,
                    Values = new List<JavaValue>
                    {
                        new JavaNumber { Tc = 'I', Bits = 0 },
                        new JavaNumber { Tc = 'I', Bits = 1 },
                        arr
                    }
                };

                var root2 = new JavaObject
                {
                    Desc = root.Desc,
                    Values = new List<JavaValue>
                    {
                        new JavaNumber { Tc = 'I', Bits = 0 },
                        empty1, empty2, empty3, modifyVec
                    }
                };

                var writer = new JavaStreamWriter();
                writer.WriteValue(root2);
                var payload = writer.ToArray();

                var hdr = new byte[VosProto.HeaderLen];
                Array.Copy(templatePkt, hdr, VosProto.HeaderLen);
                return VosProto.BuildPacket(hdr, VosProto.CompressGzip(payload));
            }
            catch (Exception e)
            {
                log?.Invoke("        !! dung packet SET loi: " + e.Message);
                return null;
            }
        }

        static JavaArray NewObjectArray(JavaClassDesc arrayDesc, JavaValue first)
        {
            var arr = new JavaArray { Desc = arrayDesc, Length = 10 };
            arr.Items.Add(first);
            for (int i = 1; i < 10; i++) arr.Items.Add(JavaNull.Instance);
            return arr;
        }

        static JavaObject NewEmptyVector(JavaClassDesc vectorDesc, JavaClassDesc arrayDesc)
        {
            var arr = new JavaArray { Desc = arrayDesc, Length = 10 };
            for (int i = 0; i < 10; i++) arr.Items.Add(JavaNull.Instance);
            return new JavaObject
            {
                Desc = vectorDesc,
                Values = new List<JavaValue>
                {
                    new JavaNumber { Tc = 'I', Bits = 0 },
                    new JavaNumber { Tc = 'I', Bits = 0 },
                    arr
                }
            };
        }

        /// <summary>
        /// Dung lai packet FilterGatewayMapping "lay TAT CA":
        ///   - agentAccount = "" (neu template dang loc 1 agent)
        ///   - info.customerAccount = "*"
        ///   - idSet = rong (neu template dang loc san vai mapping id -> moi account)
        /// </summary>
        public static byte[] BroadcastFilter(byte[] obj)
        {
            if (obj == null) return null;
            try
            {
                var root = ParseStreamValue(obj) as JavaObject;
                if (root == null) return null;

                var res = obj;

                // agentAccount cua FilterGatewayMapping
                var agent = root.Get("agentAccount") as JavaString;
                if (agent != null && agent.Pos >= 0 && !string.IsNullOrEmpty(agent.Value))
                    res = PatchStringAt(res, agent.Pos, agent.Len, "");

                // customerAccount cua object InfoGatewayMapping long ben trong
                var root2 = ParseStreamValue(res) as JavaObject;
                var mapping = FindMappingNode(root2);
                var account = mapping?.Get("customerAccount") as JavaString;
                if (account != null && account.Pos >= 0 && account.Value != "*")
                    res = PatchStringAt(res, account.Pos, account.Len, "*");

                // idSet: neu template dang loc san vai id -> xoa rong de server tra TAT CA mapping
                var root3 = ParseStreamValue(res) as JavaObject;
                var setObj = root3?.Get("idSet") as JavaObject;
                var setIdxs = setObj?.Desc?.AllFieldsSuperFirst().Select(f => f.Name).ToList();
                long setCount = 0;
                if (setObj != null && setIdxs != null)
                {
                    int ci = setIdxs.IndexOf("elementCount");
                    if (ci >= 0 && ci < setObj.Values.Count && setObj.Values[ci] is JavaNumber cn) setCount = cn.Bits;
                }
                if (setObj != null && setCount > 0)
                {
                    var dataArr = setObj.Get("elementData") as JavaArray;
                    root3.Set("idSet", NewEmptyVector(setObj.Desc, dataArr?.Desc));
                    var writer = new JavaStreamWriter();
                    writer.WriteValue(root3);
                    res = writer.ToArray();
                }

                return res;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Chẩn đoán: mô tả ngắn cấu trúc 1 object filter (tên field + giá trị chuỗi/số),
        /// để biết filter thật đang lọc theo field nào (agentAccount / idSet / customerAccount...).
        /// </summary>
        public static string Describe(JavaValue v, int depth = 0)
        {
            if (v == null || depth > 3) return "…";
            var pad = new string(' ', depth * 2);
            switch (v)
            {
                case JavaObject o when o.Desc != null:
                    {
                        var sb = new StringBuilder();
                        var shortName = o.Desc.Name ?? "?";
                        int dot = shortName.LastIndexOf('.');
                        if (dot >= 0) shortName = shortName.Substring(dot + 1);
                        sb.Append(shortName);
                        int i = 0;
                        foreach (var f in o.Desc.AllFieldsSuperFirst())
                        {
                            var val = i < o.Values.Count ? o.Values[i] : null;
                            i++;
                            if (val is JavaString s) sb.Append($"\n{pad}  {f.Name}='{s.Value}'");
                            else if (val is JavaNumber n) sb.Append($"\n{pad}  {f.Name}={n.Bits}");
                            else if (val is JavaObject jo && jo.Desc != null)
                                sb.Append($"\n{pad}  {f.Name}: {{{Describe(jo, depth + 1)}}}");
                            else if (val is JavaArray ja)
                                sb.Append($"\n{pad}  {f.Name}: [{ja.Length}] {Describe(ja, depth + 1)}");
                        }
                        return sb.ToString();
                    }
                case JavaArray a:
                    {
                        var len = Math.Min(a.Items.Count, 4);
                        var inner = string.Join(", ", a.Items.Take(len).Select(x => Describe(x, depth + 1)));
                        return $"{a.Desc?.Name}[{a.Length}]{{{(inner.Length > 200 ? inner.Substring(0, 200) : inner)}}}";
                    }
                default:
                    return v.GetType().Name;
            }
        }

        /// <summary>Chẩn đoán packet GET (FilterGatewayMapping): in ra cấu trúc filter đang dùng.</summary>
        public static string DescribeFilter(byte[] pkt)
        {
            if (pkt == null) return "(khong co packet)";
            if (!VosProto.TryDecompressFrom(pkt, VosProto.HeaderLen, out var obj))
                return "(khong giai nen duoc)";
            var root = ParseStreamValue(obj);
            var d = Describe(root);
            return d.Length > 1500 ? d.Substring(0, 1500) + "\n…" : d;
        }

        static JavaObject FindMappingNode(JavaValue v)
        {
            switch (v)
            {
                case JavaObject o:
                    if (o.Desc != null && o.Desc.IsMappingHierarchy) return o;
                    foreach (var x in o.Values)
                    {
                        var found = FindMappingNode(x);
                        if (found != null) return found;
                    }
                    break;
                case JavaArray a:
                    foreach (var x in a.Items)
                    {
                        var found = FindMappingNode(x);
                        if (found != null) return found;
                    }
                    break;
            }
            return null;
        }

        // ---------- helpers so ----------

        public static int NumericSize(byte tc) => tc switch
        {
            (byte)'B' or (byte)'Z' => 1,
            (byte)'C' or (byte)'S' => 2,
            (byte)'I' or (byte)'F' => 4,
            (byte)'J' or (byte)'D' => 8,
            _ => 0
        };
    }

    /// <summary>
    /// Codec cho call-in: object InfoGatewayRouting (e_gatewayrouting).
    /// Dùng chung parser/writer Java với mapping, khác ở class + field "prefix" (danh sách thuần).
    /// </summary>
    public static class RoutingCodec
    {
        public const string RoutingClass = "com.kunshi.interfacef.vos2000.InfoGatewayRouting";
        public const string RoutingFilterClass = "com.kunshi.interfacef.vos2000.FilterGatewayRouting";

        /// <summary>Đọc packet GET (FilterGatewayRouting) trong 1 chunk traffic/response.</summary>
        public static byte[] FindFilterPacket(byte[] buf) =>
            GatewayMappingCodec.FindGetPacket(buf, RoutingFilterClass);

        /// <summary>Đổi filter sang "lấy tất cả": agentAccount="" + info.clearingCustomerAccount="*".</summary>
        public static byte[] BroadcastFilter(byte[] templatePkt)
        {
            if (templatePkt == null) return null;
            if (!VosProto.TryDecompressFrom(templatePkt, VosProto.HeaderLen, out var obj)) return null;
            var patched = GatewayMappingCodec.BroadcastRoutingFilter(obj);
            return patched == null ? null : GatewayMappingCodec.RebuildPacket(templatePkt, patched);
        }

        /// <summary>Đọc tất cả routing (InfoGatewayRouting) từ các object GET trả về.</summary>
        public static List<JavaObject> ParseAll(IEnumerable<byte[]> objs, Action<string> log = null)
        {
            var res = new List<JavaObject>();
            var seen = new HashSet<JavaObject>();
            foreach (var o in objs ?? Enumerable.Empty<byte[]>())
                res.AddRange(GatewayMappingCodec.ParseRoutingObjects(o, log).Where(seen.Add));
            return res;
        }

        /// <summary>Prefix của routing là danh sách thuần ("84...,#84...", không có "x;").</summary>
        public static string Prefix(JavaObject o) => o?.GetString("prefix") ?? "";

        /// <summary>Ghi prefix mới vào object live (để lần ghi tiếp dùng đúng dữ liệu mới).</summary>
        public static void SetPrefix(JavaObject o, string value) =>
            o?.Set("prefix", new JavaString { Value = value ?? "" });

        public static long Id(JavaObject o) => o?.GetNumber("id") ?? 0;

        public static long ClearingCustomerId(JavaObject o) => o?.GetNumber("clearingCustomer_id") ?? 0;

        public static string Name(JavaObject o) => o?.GetString("name") ?? "";

        public static string ClearingCustomerAccount(JavaObject o) => o?.GetString("clearingCustomerAccount") ?? "";

        /// <summary>
        /// Chẩn đoán: liệt kê các field CHUỖI không rỗng của 1 routing (tên = giá trị),
        /// để phát hiện danh sách số nằm ở field khác 'prefix'.
        /// </summary>
        public static string DescribeStringFields(JavaObject o)
        {
            if (o?.Desc == null) return "(khong co object)";
            var parts = new List<string>();
            int i = 0;
            foreach (var f in o.Desc.AllFieldsSuperFirst())
            {
                var v = i < o.Values.Count ? o.Values[i] : null;
                i++;
                if (v is JavaString s && !string.IsNullOrEmpty(s.Value) && s.Value.Length <= 200)
                    parts.Add($"{f.Name}='{s.Value}'");
            }
            return parts.Count == 0 ? "(khong co field chuoi)" : string.Join(", ", parts);
        }

        /// <summary>Dựng packet SET routing từ template ReqSetInfoList + object GET live.</summary>
        public static byte[] BuildSetPacket(byte[] templatePkt, JavaObject live, string newPrefix, Action<string> log = null)
            => GatewayMappingCodec.BuildRoutingSetPacket(templatePkt, live, newPrefix, log);
    }
}
