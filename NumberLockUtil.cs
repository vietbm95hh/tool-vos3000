// ============================================================
//  NumberLockUtil - Xu ly danh sach so cho tab "Khoa so".
//
//  Prefix tren server co dang: "1;0901,0912,#0902,84901"
//  Quy uoc:
//    - So bat dau bang "84"  -> HOP LE (mo)
//    - So khac (0..., #...)  -> BI KHOA
//    - Khoa mot so  = them "#" truoc so do
//    - Mo mot so    = bo "#", doi thanh dang "84xxx"
// ============================================================
namespace Vos3000Agent
{
    public static class NumberLockUtil
    {
        /// Tach chuoi prefix thanh (phan dau "1;", danh sach so).
        public static (string Head, List<string> Nums) Split(string prefix)
        {
            var nums = new List<string>();
            if (string.IsNullOrEmpty(prefix)) return (";", nums);
            int idx = prefix.IndexOf(';');
            string head = idx < 0 ? (prefix.Length > 0 ? prefix : ";") : prefix.Substring(0, idx + 1);
            string rest = idx < 0 ? "" : prefix.Substring(idx + 1);
            foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var n = part.Trim();
                if (n.Length > 0) nums.Add(n);
            }
            return (head, nums);
        }

        public static string Join(string head, IEnumerable<string> nums) => head + string.Join(",", nums);

        /// <summary>Tách danh sách thuần "84...,#84..." (không có phần đầu "x;").</summary>
        public static List<string> SplitPlain(string list) =>
            (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();

        /// So hop le: bat dau "84" (va co it nhat 1 digit sau 84).
        public static bool IsValid(string n) =>
            !string.IsNullOrEmpty(n) && n.Length > 2 && n.StartsWith("84") && IsDigits(n.Substring(2));

        /// So bi khoa: khac quy tac hop le (khong bat dau 84, ke ca so co "#").
        public static bool IsLocked(string n) => !IsValid(n);

        /// [Khóa số] thêm "#" truoc so. So da co "#" thi giu nguyen.
        public static string Lock(string n)
        {
            n = Clean(n);
            if (n.StartsWith("#")) return n;
            return "#" + n;
        }

        /// [Mo so] bo "#" va doi thanh dang "84xxx" (nho chi gom digit).
        public static string Unlock(string n)
        {
            n = Clean(n);
            int i = 0;
            while (i < n.Length && n[i] == '#') i++;
            var digits = n.Substring(i);
            if (digits.Length == 0) return "84";
            // da co dang 84 dung quy tac thi giu nguyen
            if (digits.StartsWith("84")) return digits;
            return "84" + digits;
        }

        /// Chuan hoa 1 so tu API ve dung quy uoc prefix cua server:
        ///   khoa (#...) -> "#84..."   mo (0.../sai dang) -> "84..."   so 84... giu nguyen.
        /// API co the tra "#2369995995" (thieu ma quoc gia) -> "#842369995995".
        public static string NormalizeFromApi(string n)
        {
            n = Clean(n);
            if (n.Length == 0) return "";
            bool locked = n.StartsWith("#");
            var s = locked ? Unlock(n) : n;
            if (!s.StartsWith("84") || s.Length <= 2) s = "84" + (s.StartsWith("0") ? s.Substring(1) : s);
            return locked ? "#" + s : s;
        }

        /// Chuan hoa ca chuoi prefix (giu nguyen phan dau "x;"), dung cho gia tri doc tu API.
        public static string NormalizePrefix(string prefix)
        {
            var (head, nums) = Split(prefix);
            return Join(head, nums.Select(NormalizeFromApi).Where(x => x.Length > 2));
        }

        /// Chi giu lai ky tu hop le (0-9 va '#').
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in s.Trim())
            {
                if (c == '#' || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        public static string StatusText(string n) => IsValid(n) ? Msg.NumberLock.LockedOk : Msg.NumberLock.LockedNo;

        /// <summary>
        /// Khoa so khop "goc": bo "#", bo tien to "84", bo so 0 dau.
        /// 0123456781 == 840123456781 == #840123456781  ->  "123456781"
        /// Dung de syncer khong re-add so da duoc bien doi / khoa.
        /// </summary>
        public static string TailKey(string n)
        {
            n = Clean(n);
            int i = 0;
            while (i < n.Length && n[i] == '#') i++;
            var d = n.Substring(i);
            if (d.StartsWith("84")) d = d.Substring(2);
            while (d.Length > 0 && d[0] == '0') d = d.Substring(1);
            return d;
        }

        // ============================================================
        //  NHIEU ACCOUNT (tab "Khóa số" hiển thị gộp)
        // ============================================================

        /// <summary>
        /// Gộp danh sách số của NHIỀU account thành 1 danh sách DUY NHẤT (mỗi số chỉ hiện 1 lần).
        /// Khử trùng theo "gốc" số (<see cref="TailKey"/>) — mỗi số chỉ còn 1 entry.
        ///
        /// Cùng 1 số ở nhiều account mà trạng thái khác nhau (account A mở, account B khóa) thì
        /// hiển thị <see cref="Locked"/> (ưu tiên "bị khóa" trên lưới, an toàn hơn). Người dùng
        /// bấm "Khóa"/"Mở khóa" sẽ đưa TẤT CẢ account về cùng 1 trạng thái đó.
        /// </summary>
        public static List<string> DistinctByKey(IEnumerable<string> nums)
        {
            var idx = new Dictionary<string, int>();
            var list = new List<string>();
            foreach (var raw in nums ?? Enumerable.Empty<string>())
            {
                var n = Clean(raw);
                var k = TailKey(n);
                if (k.Length == 0) continue;
                if (idx.TryGetValue(k, out var pos))
                {
                    // gặp lại cùng gốc số: nếu có account đang khóa -> hiển thị nguồn khóa
                    if (IsLocked(n) && !IsLocked(list[pos])) list[pos] = n;
                }
                else { idx[k] = list.Count; list.Add(n); }
            }
            return list;
        }

        /// <summary>Tập số (khóa/mở) mà danh sách gộp đang hiển thị: gốc số -> entry đầy đủ.</summary>
        public static Dictionary<string, string> StateByKey(IEnumerable<string> nums)
        {
            var d = new Dictionary<string, string>();
            foreach (var n in DistinctByKey(nums)) d[TailKey(n)] = n;
            return d;
        }

        /// <summary>
        /// Áp trạng thái khóa/mở (lấy từ danh sách gộp) lên prefix của MỘT account.
        ///   - Số của account CÓ trong tập: đổi sang đúng trạng thái (khóa = '#', mở = '84…').
        ///   - Số của account KHÔNG có trong tập: giữ nguyên (không xoá số riêng của account).
        ///   - Khử trùng gốc số trong account (entry đầu thắng).
        /// Head "x;" của account được giữ nguyên.
        /// </summary>
        public static (string NewPrefix, int Locked, int Opened, int RemovedDup) ApplyState(
            string accountPrefix, IReadOnlyDictionary<string, string> stateByKey)
        {
            var (head, nums) = Split(accountPrefix);
            int locked = 0, opened = 0;
            var seen = new HashSet<string>();
            var list = new List<string>(nums.Count);

            foreach (var sr in nums)
            {
                var k = TailKey(sr);
                if (k.Length == 0) continue;
                if (!seen.Add(k)) continue;                         // trùng gốc số -> bỏ

                if (stateByKey == null || !stateByKey.TryGetValue(k, out var want))
                {
                    // Account này có số mà số đó KHÔNG nằm trên lưới: không đụng (chỉ dọn trùng)
                    list.Add(sr);
                    continue;
                }

                // entry trùng gốc vẫn được tính (xem RemoveDup), nhưng danh sách chỉ giữ entry đầu
                bool wantLock = IsLocked(want);
                bool curLock = IsLocked(sr);
                if (wantLock && !curLock) locked++;
                else if (!wantLock && curLock) opened++;
                list.Add(wantLock ? Lock(sr) : Unlock(sr));
            }

            return (Join(head, list), locked, opened, nums.Count - list.Count);
        }

        /// <summary>
        /// So sánh prefix của account với tập hiển thị: true nếu mọi số CÓ trong account
        /// đều đúng trạng thái của tập và không còn entry trùng gốc.
        /// </summary>
        public static bool MatchesState(string accountPrefix, IReadOnlyDictionary<string, string> stateByKey)
        {
            var (_, nums) = Split(accountPrefix);
            var seen = new HashSet<string>();
            foreach (var sr in nums)
            {
                var k = TailKey(sr);
                if (k.Length == 0) return false;
                if (!seen.Add(k)) return false;
                if (stateByKey == null || !stateByKey.TryGetValue(k, out var want)) continue;
                if (IsLocked(want) != IsLocked(sr)) return false;
            }
            return true;
        }

        /// <summary>
        /// Như <see cref="MatchesState"/> nhưng cho danh sách thuần "84...,#84..." (call-in,
        /// không có head "x;") — dùng cho gateway routing / settlement.
        /// </summary>
        public static bool MatchesStatePlain(string list, IReadOnlyDictionary<string, string> stateByKey)
        {
            var seen = new HashSet<string>();
            foreach (var sr in SplitPlain(list))
            {
                var k = TailKey(sr);
                if (k.Length == 0) return false;
                if (!seen.Add(k)) return false;
                if (stateByKey == null || !stateByKey.TryGetValue(k, out var want)) continue;
                if (IsLocked(want) != IsLocked(sr)) return false;
            }
            return true;
        }

        static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>
        /// Số "rác" trên server — KHÔNG phải SĐT thật và không đến từ API:
        ///   - bắt đầu "0" (ví dụ "08445685324" — nhầm đầu số đầu tiên)
        ///   - phần sau "84" dài quá 10 chữ số (số thật VN tối đa 10)
        ///   - dãy số giả lập (chứa "9999999" hoặc "9000000")
        /// Chỉ dùng để DỌN dữ liệu cũ; không bao giờ xoá số mà API đang khai báo.
        /// </summary>
        public static bool IsGarbage(string n)
        {
            n = Clean(n);
            while (n.StartsWith("#")) n = n.Substring(1);
            if (n.Length < 3) return true;
            if (n[0] == '0') return true;
            if (!n.StartsWith("84")) return true;
            var rest = n.Substring(2);
            if (!IsDigits(rest)) return true;
            if (rest.Length > 10) return true;
            // chỉ các dấu hiệu rõ ràng của số giả lập thủ công
            if (rest.StartsWith("29999999") || rest.StartsWith("9999999")
                || rest.EndsWith("12345678") || rest.Contains("00000")) return true;
            return false;
        }
    }
}
