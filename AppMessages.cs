
namespace Vos3000Agent
{
    public static class Msg
    {
        public static class Db
        {
            public const string NoConfig = "Chưa khai báo thông tin DB (host/tên DB).";
            public const string NoConfigShort = "Chưa khai báo thông tin DB.";
            public const string NoDatabaseName = "Chưa điền tên database.";
            public const string ConnectOk = "Kết nối OK — MySQL {0}, {1}";
            public const string Header = "{0}:{1} (chưa chọn DB / DB: {2})";
            public const string HostNotConfigured = "IP Database chưa khai báo";
            public const string DatabaseReady = "Database '{0}' sẵn sàng (chưa gen bảng).";
            
            public const string GenTablesOk = "Gen bảng thành công: {0}";
            public const string GenTablesFail = "Gen bảng lỗi: {0}";
            public const string UnknownError = "(không rõ)";
        }

        public static class Api
        {
            public const string BaseUrlNotConfigured = "API Base URL chưa cấu hình";
            public const string RequireHttps = "API URL must use HTTPS (HTTP allowed only for localhost testing)";
            public const string TokenNotConfigured = "Chưa cấu hình Agent Token";
            public const string HttpError = "HTTP {0}: {1}";
            public const string InvalidJson = "API response không phải JSON hợp lệ";
            public const string ConnectionError = "Lỗi kết nối API: {0}";
        }

        public static class Prefix
        {
            public const string ApiNotConfigured = "(API danh sách số đã chưa cấu hình - bỏ qua)";
            public const string FetchStart = "API #{0} — lấy danh sách số từ {1}...";
            public const string EmptyList = "API trả về danh sách rỗng (sau bộ lọc/dupe). Không làm gì.";
            public const string EmptyListLog = "  API trả về rỗng - không làm gì.";
            public const string FetchedLog = "  Lấy được {0} số (đã trừ trùng): {1}";
            public const string ReadingCurrent = "Đọc prefix hiện tại trên VOS...";
            public const string CannotConnectVos = "Không kết nối VOS3000.";
            public const string CannotReadPrefix = "Không đọc được prefix hiện tại.";
            public const string CurrentPrefixLog = "  Prefix hiện tại: '{0}'";
            public const string NothingChanged = "Không có số mới/trạng thái đổi ({0} số trong API đều khớp trên server).";
            public const string AddSuccess = "ADD {0} số THÀNH CÔNG (tổng {1} số trên server, {2} vòng).";
            public const string DupFoundLog = "  Server có {0} entry trùng — tiến hành dọn (không có thay đổi khác từ API).";
            public const string NeedAddLog = "  Cần add {0} số mới: {1}";
            public const string AutoLockLog = "  Tự khóa {0} số theo API (API báo khóa, server đang mở): {1}";
            public const string AutoOpenLog = "  Tự mở {0} số theo API (API báo mở, server đang khóa): {1}";
            public const string DedupLog = "  Dọn trùng: {0} entry lặp bị bỏ ({1} -> {2} số).";
            public const string ServerChangedBeforeSet = "  ! Vòng {0}/{1}: server đổi trước khi SET (live ≠ vừa đọc) — một người khác đang ghi, thử lại...";
            public const string ReadbackDifferentAfterSet = "  ! Vòng {0}/{1}: readback SET OK nhưng đọc lại khác — server có thay đổi, thử lại...";
            public const string ReadbackMismatchLog = "  ! Vòng {0}/{1}: readback khác giá trị đã SET ('{2}') — đọc lại danh sách mới và thử lại...";
            public const string MismatchAfterRetry = "! MISMATCH sau {0} lần thử — không ghi đè, cần thử lại sau (tránh xóa nhầm số của người khác).";
            public const string ErrorLog = "Loi: {0}";
            public const string HttpFail = "HTTP {0} {1} ({2}ms)";            public const string ApiOkLog = "  API {0}ms OK ({1}), {2} bytes";
            public const string FieldNotFound = "Không tìm được mảng số trong JSON (field '{0}' chưa rõ)";
            public const string Timeout = "Timeout {0}s";
            public const string CallApiError = "Gọi API lỗi: {0}";

            
            public const string ResultLockAndOpen = "ADD {0} số + TỰ KHÓA {1} + TỰ MỞ {2} số theo API (tổng {3} số trên server).";
            public const string ResultLockOnly = "ADD {0} số + TỰ KHÓA {1} số theo API (tổng {2} số trên server).";
            public const string ResultOpenOnly = "ADD {0} số + TỰ MỞ {1} số theo API (tổng {2} số trên server).";
            public const string ResultAddOnly = "ADD {0} số THÀNH CÔNG (tổng {1} số trên server).";
            public const string RecordOperation = "sync: lấy {0} số / chưa có trên VOS {1} — {2}";

            // ---- đồng bộ nhiều account (mapping) ----
            public const string NoAccounts = "Không tìm thấy account (gateway mapping) nào trên server.";
            public const string AccountsFound = "  Tìm thấy {0} gateway mapping (call-out) — đồng bộ số cho TẤT CẢ.";
            public const string AccountMergeLog = "  • {0}: +{1} số, khóa {2}, mở {3} (tổng {4} số trên account).";
            public const string OutMergeLog = "  • OUT {0} (mapping {1}): +{2} số, khóa {3}, mở {4} (tổng {5} số).";
            public const string InMergeLog = "  • IN  {0} (routing {1}): +{2} số, khóa {3}, mở {4} (tổng {5} số).";
            public const string WritingAccounts = "  Cần cập nhật {0} gateway mapping (call-out) — đang ghi...";
            public const string WritingInRoutings = "  Cần cập nhật {0} gateway routing (call-in) — đang ghi MySQL...";
            public const string InDbReloadWarning = "  (e_gatewayrouting ghi trực tiếp MySQL — VOS3000 có thể cần reload/restart để áp dụng)";
            public const string NothingChangedAll = "Không có thay đổi: {0} account đều khớp {1} số từ API.";
            public const string AccountsSyncedOk =
                "ĐỒNG BỘ XONG: {0} account, cập nhật {1} — khóa {3}, mở {4} (không add số mới).";
            public const string AccountsSyncedPartial =
                "ĐỒNG BỘ MỘT PHẦN: {0} account, ghi {1}, khớp {2}, LỆCH {3} — sẽ đối chiếu lại vòng sau.";

            public const string DbNotConfigured = "Chưa cấu hình DB VOS — bỏ qua đồng bộ call-in (e_gatewayrouting).";
            public const string OwnersLoaded = "  Đọc DB: {0} gateway mapping (chủ sở hữu account).";
            public const string InRoutingsLoaded = "  Đọc DB: {0} gateway routing (call-in).";
            public const string DbReadError = "Đọc DB lỗi: {0}";
            public const string GroupSummary = "  Tổng hợp theo account ({0} account, in/out):";
            public const string GroupLine = "    #{0} {1}: in={2} (khóa {3}) | out={4} (khóa {5})";

            
            public const string FieldNotArrayLog = "field '{0}' khong phai mang So (hoac co phan tu khong hop le) - thu tu do toan JSON...";
            public const string FieldMissingLog = "khong tim thay field '{0}' trong JSON - thu tu do toan JSON...";
            public const string JsonErrorLog = "JSON error: {0}";
            public const string SkipEmptyItem = " Bo qua item rong";
            public const string SkipNonNumber = "  Bo qua item khong phai so: '{0}'";

            // Mốc last_created_at (gọi tuần tự)
            public const string WatermarkFirstRun = "  Lần đầu (chưa có mốc) — gọi full: {0}";
            public const string WatermarkIncremental = "  Gọi tăng dần: created_at={0} (mốc đọc từ {1})";
            public const string WatermarkSaved = "  Mốc last_created_at mới = {0} — đã lưu ({1})";
            public const string WatermarkSaveFail = "  !! Không lưu được mốc last_created_at = {0}";
            public const string WatermarkMissingLog = "  API không trả last_created_at — lần sau vẫn gọi tăng dần theo mốc cũ.";

            // ---- chỉ update số đã có trên VOS3000; số chưa có lưu DB ----
            public const string AllOnServerLog = "  Phân loại: tất cả {0} số từ API đã có trên VOS3000 — chỉ cập nhật khóa/mở, KHÔNG add số mới.";
            public const string MissingOnServerLog = "  Phân loại: {0} SỐ MỚI (API có, VOS3000 không có — không add) / {1} số đã có (chỉ update): {2}";
            public const string MissingSavedLog = "  Đã lưu {0} số mới vào bảng {1} — xem tab \"Số chưa có (VOS)\".";
            public const string MissingRemovedLog = "  Đã xoá {0} dòng cũ khỏi bảng t_number_missing (nay đã có trên server).";
            public const string MissingSaveError = "Lưu bảng số chưa có lỗi: {0}";
        }

        public static class Missing
        {
            public const string Title = "Số mới — API có nhưng VOS3000 chưa có (chỉ lưu DB, không tự add)";
            public const string Hint = "“Add Số (VOS)” chỉ cập nhật khóa/mở cho số đã có trên server. Số API trả về mà VOS3000 không có được coi là SỐ MỚI và lưu ở đây (bảng t_number_missing); khi số đã có trên server hoặc rời khỏi server thì tự xoá khỏi bảng.";
            public const string StateNotLoaded = "● Chưa tải";
            public const string StateLoading = "● Đang tải…";
            public const string StateLoaded = "● OK — {0} số";
            public const string StateDbNotConfig = "● Chưa cấu hình DB tool";
            public const string StateError = "● Lỗi tải";
            public const string Empty = "Bảng trống — chưa có số nào thiếu trên server.";
            public const string AlreadyOnServer = "Số này hiện không còn trong bảng (có thể đã có trên server).";
            public const string ClearConfirm = "Xoá TOÀN BỘ danh sách số chưa có trong bảng?\nThao tác này không ảnh hưởng VOS3000.";
            public const string ClearDone = "Đã xoá {0} số khỏi bảng.";
            public const string ExcelDone = "Đã xuất {0} dòng:\n{1}";
            public const string ExcelNoData = "Chưa có dữ liệu để xuất.";
            public const string ExcelError = "Xuất Excel lỗi: {0}";
            public const string DbNotConfigLog = "Chưa cấu hình DB tool (Cấu hình → Database). Không đọc được bảng.";
            public const string LoadedLog = "Đã tải {0} số chưa có trên server.";
            public const string LoadError = "Lỗi đọc bảng t_number_missing: {0}";
            public const string StatusLocked = "Khóa";
            public const string StatusOpen = "Mở";
        }

        public static class AccountSet
        {
            public const string Title = "Số đang có trên VOS3000 — tổng hợp theo từng account";
            public const string Hint = "Bảng này do mỗi vòng “Add Số (VOS)” tự cập nhật: tool đọc lại toàn bộ call-in/call-out trên server, gom số theo account và lưu trạng thái khóa/mở. Không thao tác lên VOS3000.";
            public const string StateNotLoaded = "● Chưa tải";
            public const string StateLoading = "● Đang tải…";
            public const string StateLoaded = "● OK — {0} số / {1} account";
            public const string StateDbNotConfig = "● Chưa cấu hình DB tool";
            public const string StateError = "● Lỗi tải";
            public const string Empty = "Bảng trống — chạy “API: Lấy + Add ngay 1 lần” để tool đọc số trên VOS3000 và tổng hợp theo account.";
            public const string DbNotConfigLog = "Chưa cấu hình DB tool (Cấu hình → Database). Không đọc được bảng.";
            public const string LoadedLog = "Đã tải {0} số / {1} account từ bảng t_account_number_set.";
            public const string LoadError = "Lỗi đọc bảng t_account_number_set: {0}";
            public const string AllAccounts = "Tất cả account";
            public const string AllDirections = "Tất cả hướng";
            public const string DirIn = "Call-in (IN)";
            public const string DirOut = "Call-out (OUT)";
            public const string StatusLocked = "Khóa";
            public const string StatusOpen = "Mở";
            public const string ExcelNoData = "Chưa có dữ liệu để xuất.";
            public const string ExcelDone = "Đã xuất {0} dòng:\n{1}";
            public const string ExcelError = "Xuất Excel lỗi: {0}";
            public const string ClearConfirm = "Xoá TOÀN BỘ bảng tổng hợp số theo account trong DB tool?\nThao tác này không ảnh hưởng VOS3000.";
            public const string ClearDone = "Đã xoá {0} dòng khỏi bảng tổng hợp.";
            public const string ClearEmpty = "Bảng đang trống, không có gì để xoá.";
        }

        public static class NumberLock
        {
            public const string Title = "Khóa số — danh sách số trên VOS3000";
            public const string StateNotLoaded = "● Chưa tải";
            public const string StateOk = "● OK";
            public const string StateOkGrid = "● OK (đã cập nhật lưới từ server)";
            public const string StateNoChange = "● Không đổi";
            public const string StateNotFound = "● Không tìm thấy số";
            public const string StateWriteError = "● Lỗi ghi";
            public const string StateMismatch = "● MISMATCH";
            public const string StateError = "● Lỗi";
            public const string StateRunning = "● Đang chạy…";

            public const string AlreadyInState = "  '{0}' đã ở đúng trạng thái yêu cầu — không cần ghi.";
            public const string AddedToEnd = "  '{0}' chưa có trên server — thêm '{1}' vào cuối danh sách.";
            public const string NotOnServer = "!! '{0}' không còn trên server. Hãy 'Tải danh sách số' lại.";
            public const string CommitAddedToEnd = "  '{0}' không còn trên server — thêm '{1}' vào cuối danh sách.";
            public const string CommitAlreadyExists = "  '{0}' → '{1}': '{2}' đã có trên server, bỏ qua.";
            public const string CommitApplying = "Áp dụng {0} ô đã sửa...";
            public const string NoChange = "Giá trị không đổi — không cần ghi.";
            public const string NewValue = "Giá trị mới: '{0}'";
            public const string WriteFailed = "!! Ghi thất bại (không có readback).";
            public const string ServerDroppedHash = "  (server đã tự bỏ '#' — giá trị tương đương, coi như OK)";
            public const string ReadbackOk = "✔ THÀNH CÔNG — server đã cập nhật + Apply (readback khớp).";
            public const string ReadbackMismatch = "✗ MISMATCH readback: '{0}' khác giá trị ghi.";
            public const string CannotReadList = "!! Không đọc được danh sách số hiện tại.";
            public const string CurrentList = "Danh sách hiện tại: '{0}'";
            public const string LockedOk = "✔ Hợp lệ (84…)";
            public const string LockedNo = "🔒 Bị khóa";

            // ---- gộp TẤT CẢ account customer (mỗi số hiển thị 1 lần) ----
            public const string StateLoadingAll = "● Đang đọc tất cả account…";
            public const string StateApplying = "● Đang áp lên tất cả account…";
            public const string NoAccounts = "!! Không tìm thấy account (gateway mapping) nào trên server.";
            public const string LoadedAll = "Nạp {0} số DUY NHẤT (mỗi số hiển thị 1 lần). Account: call-out {1}/{2}, call-in {3}/{4}. ✔ Hợp lệ: {5} | 🔒 Bị khóa: {6} — {7} số/trang.";
            public const string AppliedAll = "✔ Xong ({4} '{3}') — cập nhật OK {1}/{2} account (call-out + call-in).";
            public const string ApplyFailedAll = "!! Lỗi khi {1} '{0}' — OK {2}/{3} account cần đổi, xem log.";
            public const string NotInAnyAccount = "  '{0}' không có trong account nào — thêm '{1}' vào cuối TẤT CẢ account.";

            // ---- chọn NHIỀU số (checkbox trên lưới) ----
            public const string StateMultiNoChange = "● Không đổi ({0} số đã ở đúng trạng thái)";
            public const string MultiSummary = "  Tổng hợp: {0} thành công {1}/{2} số (bỏ qua {2}).";

            // Record DB
            public const string RecordOperation = "[{0}] '{1}' → giá trị mới '{2}' | readback '{3}'";
            public const string RecordLoad = "[GET] {0}:{1} — {2} số";
            public const string RecordNumberActionOk = "readback khớp";
            public const string RecordNumberActionFail = "mismatch: '{0}'";
            public const string RecordLog = "{0}: {1}";
        }

        /// <summary>Nhãn trạng thái dạng chấm màu "● ..." dùng trên các thanh công cụ.</summary>
        public static class State
        {
            public const string None = "● Chưa khởi tạo";
            public const string NotRun = "● Chưa chạy";
            public const string NoAction = "● Chưa thao tác";
            public const string Testing = "● Đang test…";
            public const string ConnectingOk = "● Kết nối OK";
            public const string ConnectError = "● Lỗi kết nối";
            public const string Initializing = "● Đang khởi tạo…";
            public const string DbReady = "● DB sẵn sàng";
            public const string InitError = "● Lỗi khởi tạo";
            public const string Generating = "● Đang gen bảng…";
            public const string TablesReady = "● Bảng sẵn sàng";
            public const string GenError = "● Lỗi gen bảng";
            public const string CallingApi = "● Đang gọi API…";
            public const string Pinging = "● Đang ping…";
            public const string Stopped = "● Đã dừng";
            public const string Error = "● Lỗi";
            public const string Ok = "● OK";
            public const string ScheduleOff = "● Lịch: tắt";
            public const string ScheduleOn = "● Lịch: chạy mỗi {0}s — lần trước: {1}";
            public const string ScheduleNever = "● Lịch: chạy mỗi {0}s — lần trước: —";
        }

        public static class Mapping
        {            public const string Header = "=== {0} → {1}:{2} [{3}] ===";
            public const string CannotReadPrefix = "!! Không đọc được prefix.";
            public const string CurrentPrefix = "PREFIX HIỆN TẠI: {0} ...";
            public const string RecordOperation = "[GET prefix] {0}:{1} → '{2}'";
            public const string RecordLoadDetail = "load prefix: {0}";
            public const string ErrorLog = "!! Lỗi: {0}";
            public const string EmptyValue = "(không đọc được)";
        }

        public static class Gui
        {
            public const string UnsupportedJob = "Không hỗ trợ loại job: {0}";
            public const string SimulateStep = "SIMULATE step {0}: {1}";
            public const string NoControlSteps = "Chưa cấu hình control steps; chưa thao tác VOS3000.";
            public const string SimulatedDone = "Mô phỏng xong; không thay đổi dữ liệu VOS3000.";
            public const string LiveLocked = "Live GUI đang bị khóa trong settings.json.";
            public const string NoSteps = "Profile {0} chưa có steps; từ chối thao tác để tránh nhập sai.";
            public const string NoVerifySteps = "Chưa cấu hình verify_steps; không cho phép live mode khi không có bước xác minh.";
            public const string WindowCountMismatch = "Cần đúng một cửa sổ khớp '{0}'; tìm thấy {1}.";
            public const string VerifyFailed = "Không có verify assertion thành công; cần đối soát.";
            public const string Completed = "Thao tác đã hoàn thành và bước xác minh thành công.";
            public const string GuiError = "Lỗi GUI hoặc trạng thái không rõ: {0}; cần đối soát trước khi chạy lại.";
            public const string ControlNotFound = "Không tìm thấy control: {0}";
            public const string UnsupportedAction = "Action không hỗ trợ: {0}";
            public const string NoWindow = "Không tìm thấy cửa sổ VOS3000 nào đang mở.";
            public const string InspectError = "Lỗi khảo sát: {0}";
            public const string InspectTruncated = "    ... (đã giới hạn {0} control)";
        }

        public static class Worker
        {
            public const string NoJob = "Không có job mới";
            public const string PollError = "Polling/API lỗi: {0}";
            public const string Stopped = "Đã dừng polling";
            public const string NoJobId = "Job không có job_id";
            public const string JobReceived = "Nhận job {0} / {1}";
            public const string JobClaimedByOther = "Job {0} đã được agent khác nhận";
            public const string Result = "{0}: {1} — {2}";
            public const string ProcessError = "{0}: lỗi xử lý/báo kết quả: {1}";
        }

        public static class Health
        {
            public const string DomainNotSet = "Chưa nhập domain API nguồn";
            public const string Ok = "OK ({0} {1})";
            public const string HttpError = "HTTP {0} {1}";
            public const string Timeout = "Timeout sau {0}s";
            public const string TooltipReady = "Sẵn sàng";
            public const string TooltipSourceNotPinged = "Server nguồn: chưa ping";
            public const string TooltipDbOk = "DB OK — {0}\nTrạng thái: {1}";
            public const string TooltipDbFail = "KHÔNG kết nối được DB — {0}\nChi tiết: {1}\nTrạng thái: {2}";
        }

        public static class Bootstrap
        {
            public const string DataDirError = "Không thể thiết lập thư mục dữ liệu:\n{0}\n\nỨng dụng vẫn chạy nhưng cấu hình sẽ không được lưu.";
            public const string DataDirTitle = "VOS3000 Agent — Thiết lập môi trường";
            public const string FirstRunOk = "Thiết lập môi trường hoàn tất.\nThư mục dữ liệu: {0}{1}\n\nVào Khác ▾ → Cấu hình để kiểm tra kết nối VOS3000 và Database.";
            public const string FirstRunTitle = "VOS3000 Agent — Cài đặt lần đầu";
            public const string NoteResources = "Đã giải nén tài nguyên (logo, icon, session mẫu).";
            public const string NoteDefaultConfig = "Đã tạo cấu hình mặc định.";
            public const string NoteFirstRun = "Lần chạy đầu — môi trường đã được thiết lập.";
            public const string LogFirstRun = "=== BOOTSTRAP LAN DAU ===";
            public const string LogRun = "=== BOOTSTRAP ===";
            public const string LogError = "LOI BOOTSTRAP: {0}";
        }
    }
}
