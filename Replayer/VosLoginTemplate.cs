// ============================================================
//  VosLoginTemplate - doc / sua tai khoan dang nhap VOS3000
//  trong file session template (TCP 1202).
//
//  Packet login nam SAN trong vos_session_capture.bin:
//    ReqSetInfo{ InfoUser{ loginName, password } }
//  Khong co o nhap user/pass o UI, nen khi chay tren may khac /
//  server khac voi tai khoan khac thi login that bai va moi lan
//  GET tra ve 0.
//
//  AN TOAN: chi doc/ghi FILE LOCAL, doc lai khong khop thi khong
//  ghi. TestLogin la tuy chon (1 lan hello + login, khong GET/SET).
// ============================================================
using System.Text;
using Vos3000Agent;

namespace Replayer
{
    public class VosLoginInfo
    {
        public string LoginName = "";
        public string Password = "";
        public bool HasCryptoPassword;
        public bool Recognized;
    }

    public static class VosLoginTemplate
    {
        public static int FindLoginIndex(IReadOnlyList<byte[]> packets)
        {
            if (packets == null) return -1;
            for (int i = 0; i < packets.Count; i++)
            {
                if (!VosProto.TryDecompressFrom(packets[i], VosProto.HeaderLen, out var obj)) continue;
                if (Encoding.UTF8.GetString(obj).Contains("loginName")) return i;
            }
            return -1;
        }

        public static VosLoginInfo Read(byte[] loginPacket)
        {
            var res = new VosLoginInfo();
            if (loginPacket == null) return res;
            if (!VosProto.TryDecompressFrom(loginPacket, VosProto.HeaderLen, out var obj)) return res;
            try
            {
                var user = FindUserNode(GatewayMappingCodec.ParseStreamValue(obj));
                if (user == null) return res;
                var ln = user.Get("loginName") as JavaString;
                var pw = user.Get("password") as JavaString;
                var cp = user.Get("cryptoPassword") as JavaString;
                if (ln != null && ln.Pos >= 0) res.LoginName = ln.Value;
                if (pw != null && pw.Pos >= 0) res.Password = pw.Value;
                res.HasCryptoPassword = cp != null && cp.Pos >= 0 && !string.IsNullOrEmpty(cp.Value);
                res.Recognized = ln != null && ln.Pos >= 0 && pw != null && pw.Pos >= 0;
            }
            catch { }
            return res;
        }

        public static byte[] Build(byte[] loginPacket, string user, string password,
                                   out string error, Action<string> log = null)
        {
            error = null;
            if (loginPacket == null) { error = "Khong co packet login."; return null; }
            if (!VosProto.TryDecompressFrom(loginPacket, VosProto.HeaderLen, out var obj))
            {
                error = "Khong giai nen duoc packet login.";
                return null;
            }

            JavaValue root;
            try { root = GatewayMappingCodec.ParseStreamValue(obj); }
            catch (Exception e) { error = "Khong parse duoc packet login: " + e.Message; return null; }

            var uNode = FindUserNode(root);
            if (uNode == null) { error = "Khong tim thay InfoUser trong packet login."; return null; }

            var lnV = uNode.Get("loginName") as JavaString;
            var pwV = uNode.Get("password") as JavaString;
            if (lnV == null || lnV.Pos < 0 || pwV == null || pwV.Pos < 0)
            {
                error = "Packet login khong co field loginName/password dang TC_STRING.";
                return null;
            }

            string newUser = string.IsNullOrEmpty(user) ? lnV.Value : user;
            string newPass = password ?? "";
            if (newUser.Length == 0) { error = "User dang nhap rong."; return null; }
            if (newUser.Length > 0xFFFF || Encoding.UTF8.GetByteCount(newPass) > 0xFFFF)
            {
                error = "User/pass qua dai (> 65535 byte).";
                return null;
            }

            log?.Invoke("Doi tai khoan login: '" + lnV.Value + "' -> '" + newUser + "'" +
                        (newPass == pwV.Value ? " (pass giu nguyen)" : " (pass thay doi)"));

            var crypto = uNode.Get("cryptoPassword") as JavaString;
            if (crypto != null && crypto.Pos >= 0 && !string.IsNullOrEmpty(crypto.Value))
                log?.Invoke("  !! Packet co cryptoPassword - server co the uu tien gia tri nay hon password.");

            byte[] result = null;

            try
            {
                uNode.Set("loginName", new JavaString { Value = newUser });
                uNode.Set("password", new JavaString { Value = newPass });
                var writer = new JavaStreamWriter();
                writer.WriteValue(root);
                var rebuilt = writer.ToArray();
                var chk = FindUserNode(GatewayMappingCodec.ParseStreamValue(rebuilt));
                if (chk != null
                    && string.Equals(chk.GetString("loginName"), newUser, StringComparison.Ordinal)
                    && string.Equals(chk.GetString("password"), newPass, StringComparison.Ordinal))
                    result = rebuilt;
            }
            catch (Exception e) { log?.Invoke("  (serialize lai loi: " + e.Message + " - dung patch byte)"); }

            if (result == null)
            {
                var step1 = GatewayMappingCodec.PatchStringAt(obj, lnV.Pos, lnV.Len, newUser);
                if (step1 == null) { error = "Patch loginName that bai."; return null; }

                int delta = Encoding.UTF8.GetByteCount(newUser) - lnV.Len;
                int pwPos = pwV.Pos + delta;
                var step2 = GatewayMappingCodec.PatchStringAt(step1, pwPos, pwV.Len, newPass);
                if (step2 == null) { error = "Patch password that bai."; return null; }
                result = step2;

                try
                {
                    var chk = FindUserNode(GatewayMappingCodec.ParseStreamValue(result));
                    if (chk == null
                        || !string.Equals(chk.GetString("loginName"), newUser, StringComparison.Ordinal)
                        || !string.Equals(chk.GetString("password"), newPass, StringComparison.Ordinal))
                    {
                        error = "Da patch nhung doc lai khong khop - dung de an toan.";
                        return null;
                    }
                }
                catch (Exception e) { error = "Patch xong nhung parse lai loi: " + e.Message; return null; }
            }

            var hdr = new byte[VosProto.HeaderLen];
            Array.Copy(loginPacket, hdr, VosProto.HeaderLen);
            return VosProto.BuildPacket(hdr, VosProto.CompressGzip(result));
        }

        public static string SaveWithLogin(string sourcePath, string targetPath, string user, string password,
                                           out string error, out int loginIndex, Action<string> log = null)
        {
            error = null; loginIndex = -1;
            List<byte[]> pk;
            try { pk = VosProto.SplitPackets(File.ReadAllBytes(sourcePath)); }
            catch (Exception e) { error = "Khong doc duoc session: " + e.Message; return null; }

            loginIndex = FindLoginIndex(pk);
            if (loginIndex < 0) { error = "Khong tim thay packet login trong session."; return null; }

            var newLogin = Build(pk[loginIndex], user, password, out error, log);
            if (newLogin == null) return null;
            pk[loginIndex] = newLogin;

            try
            {
                var full = Path.GetFullPath(targetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                using (var fs = File.Create(full))
                    foreach (var p in pk) fs.Write(p, 0, p.Length);
            }
            catch (Exception e) { error = "Ghi session that bai: " + e.Message; return null; }
            return Path.GetFullPath(targetPath);
        }

        public static (bool Ok, string Message) TestLogin(string host, int port, byte[] helloPacket, byte[] loginPacket,
                                                          int settleMs = 2500)
        {
            using var s = new VosSession(host, port, null);
            if (!s.Connect()) return (false, "Khong ket noi duoc " + host + ":" + port + " - " + s.LastError);

            // hello: server tra greeting dang TEXT (khong phai packet) -> dem byte nhan duoc
            var greet = s.SendPacket(helloPacket, 3000);
            if (greet.Count == 0 && s.LastByteCount == 0)
                return (false, "Server khong tra loi hello (sai port / firewall / IP bi chan?).");

            var resp = s.SendPacket(loginPacket, settleMs);

            if (!s.IsAlive)
                return (false, "Server dong ket noi ngay sau login - user/pass sai, tai khoan bi khoa hoac IP bi chan.");

            if (resp.Count == 0 && s.LastByteCount == 0)
                return (false, "Server khong tra loi login (thuong do user/pass sai hoac bi chan IP).");

            if (Contains(resp, "Error") || Contains(resp, "error") || Contains(resp, "exception"))
                return (false, "Server tra ve loi khi login: " + Short(DumpText(resp)));

            if (Contains(resp, "InfoUser")) return (true, "Login OK (server tra thong tin tai khoan).");
            if (Contains(resp, "RspSetInfo") || Contains(resp, "RspGetInfo")) return (true, "Login OK.");
            if (resp.Count == 0)
                return (true, "Login co phan hoi (" + s.LastByteCount + " byte, khong co object) - thu GET de chac chan.");
            return (true, "Login co phan hoi (" + resp.Count + " object).");
        }

        static JavaObject FindUserNode(JavaValue v)
        {
            if (v is JavaObject o)
            {
                if (o.Desc != null && (o.Desc.Name ?? "").EndsWith("InfoUser", StringComparison.Ordinal))
                {
                    if (o.Get("loginName") is JavaString str && str.Pos >= 0) return o;
                    if (o.Get("loginName") != null) return o;
                }
                foreach (var child in o.Values)
                {
                    var f = FindUserNode(child);
                    if (f != null) return f;
                }
            }
            else if (v is JavaArray a)
            {
                foreach (var it in a.Items)
                {
                    var f = FindUserNode(it);
                    if (f != null) return f;
                }
            }
            return null;
        }

        static bool Contains(List<byte[]> objs, string needle)
        {
            foreach (var o in objs)
                if (VosSession.ContainsAscii(o, needle)) return true;
            return false;
        }

        static string DumpText(List<byte[]> objs)
        {
            var sb = new StringBuilder();
            foreach (var o in objs)
            {
                var t = Encoding.UTF8.GetString(o);
                foreach (var part in t.Split('\0'))
                    if (part.Length > 2) sb.Append(part).Append(' ');
            }
            return sb.ToString();
        }

        static string Short(string s) => s.Length > 160 ? s.Substring(0, 160) + "..." : s;
    }
}