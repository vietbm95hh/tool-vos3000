// ============================================================
//  ToolDb - Database cua tool (MySQL).
//  - Luu log nghiep vu / hanh dong khoa so / vong sync prefix
//  - Doc log tu DB (Khong ghi cache file nua)
//  - Cap so lieu thong ke cho tab Tong quan
//
//  Settings lien quan: db_host, db_port, db_name, db_user, db_pass
//  Bang:
//    t_log            - log tong hop (module, level, job, message)
//    t_operation      - moi phan hinh nghiep vu (khoa/mo/sync/...)
//    t_number_action  - tung lan sua 1 so (khoan '#' / mo '84xxx')
//    t_sync_run       - tung vong lay API danh sach so
// ============================================================
using MySqlConnector;
using System.Text.Json;

namespace Vos3000Agent
{
    public class ToolDbStats
    {
        public long OpsTotal, OkTotal, FailTotal;
        public long OpsToday, OkToday, FailToday;
        public long LockTotal, UnlockTotal;
        public long SyncRuns, SyncAddedTotal, SyncOk;
        public long MappedTotal, MappedToday;
        public long MappedHistory;   // lũy kế mọi số từng mapping (t_mapped_history)
        public long MissingTotal;    // số API chưa có trên VOS3000 (t_number_missing)
        public bool DbOk;          // lần đếm cuối kết nối DB có OK không
        public string DbErr;       // chi tiết lỗi (hiện theo tooltip)
        public DateTime? LastTs;
    }

    /// <summary>1 dòng bảng t_number_missing — số API trả về nhưng VOS3000 chưa có.</summary>
    public class MissingNumberRow
    {
        public string Num { get; set; } = "";
        public string ApiStatus { get; set; } = "";     // "locked" / "open" theo API
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public int Hits { get; set; }
    }

    /// <summary>
    /// 1 dòng tổng hợp số theo account + hướng (t_account_number_set).
    /// numbers giữ nguyên token trên server: "84..." = mở, "#84..." = khóa.
    /// </summary>
    public class AccountNumberSetRow
    {
        public int AccountId { get; set; }
        public string Account { get; set; } = "";
        public string Direction { get; set; } = "";     // "in" / "out"
        public List<string> Numbers { get; set; } = new();
        public DateTime? UpdatedAt { get; set; }

        public int NumCount => Numbers?.Count ?? 0;
        public int LockedCount => Numbers?.Count(x => !NumberLockUtil.IsValid(x)) ?? 0;
    }

    public static class ToolDb
    {
        private static Settings _s;
        private static readonly SemaphoreSlim _gate = new(1, 1);

        public static void Configure(Settings s)
        {
            _s = s;
            s?.Normalize();
        }

        public static string ConnString(bool withDatabase = true)
        {
            var s = _s;
            string db = withDatabase && s != null && !string.IsNullOrWhiteSpace(s.db_name)
                ? $"Database={s.db_name};"
                : "";
            return $"Server={s.db_host};Port={s.db_port};User ID={s.db_user};Password={s.db_pass};" +
                   db + "SslMode=Preferred;Connection Timeout=10;DefaultCommandTimeout=30;";
        }

        public static bool HasConfig()
        {
            var s = _s;
            return s != null && !string.IsNullOrWhiteSpace(s.db_host) && !string.IsNullOrWhiteSpace(s.db_name);
        }

        // ---------- Khoi tao / gen ----------

        /// Kiem tra ket noi (khong chon database).
        public static (bool Ok, string Info) TestConnection()
        {
            try
            {
                if (!HasConfig()) return (false, Msg.Db.NoConfig);
                using var conn = new MySqlConnection(ConnString(withDatabase: false));
                conn.Open();
                return (true, string.Format(Msg.Db.ConnectOk, conn.ServerVersion, HeaderOf()));
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }
        }

        private static string HeaderOf()
        {
            var s = _s;
            return string.Format(Msg.Db.Header, s.db_host, s.db_port, s.db_name);
        }

        /// Tao database neu chua co + gen tat ca bang cua tool.
        public static async Task<(bool Ok, string Info)> InitDatabaseAsync(bool skipTables)
        {
            try
            {
                if (_s == null || !HasConfig()) return (false, Msg.Db.NoConfigShort);
                var s = _s;
                if (string.IsNullOrWhiteSpace(s.db_name))
                    return (false, Msg.Db.NoDatabaseName);

                using var conn = new MySqlConnection(ConnString(withDatabase: false));
                await conn.OpenAsync();

                var db = s.db_name.Replace("`", "``");
                var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}` DEFAULT CHARACTER SET utf8 COLLATE utf8_general_ci";
                await cmd.ExecuteNonQueryAsync();

                if (!skipTables)
                    return await GenTablesAsync(conn, db);

                return (true, string.Format(Msg.Db.DatabaseReady, s.db_name));
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }
        }

        /// Gen (CREATE TABLE IF NOT EXISTS) cac bang cua tool trong DB da chon.
        public static async Task<(bool Ok, string Info)> GenTablesAsync(MySqlConnection conn = null, string db = null)
        {
            MySqlConnection own = null;
            try
            {
                if (conn == null)
                {
                    if (!HasConfig()) return (false, Msg.Db.NoConfigShort);
                    // DB chưa tồn tại thì tự tạo trước, rồi mới mở với Database=
                    var s0 = _s;
                    using (var boot = new MySqlConnection(ConnString(withDatabase: false)))
                    {
                        await boot.OpenAsync();
                        var bdb = s0.db_name.Replace("`", "``");
                        using var bcmd = boot.CreateCommand();
                        bcmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{bdb}` DEFAULT CHARACTER SET utf8 COLLATE utf8_general_ci";
                        await bcmd.ExecuteNonQueryAsync();
                    }
                    own = new MySqlConnection(ConnString(withDatabase: true));
                    await own.OpenAsync();
                    conn = own;
                }

                var schema = new[]
                {
@"CREATE TABLE IF NOT EXISTS t_log (
    id BIGINT NOT NULL AUTO_INCREMENT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    level VARCHAR(16) NOT NULL DEFAULT 'INFO',
    module VARCHAR(32) NOT NULL DEFAULT '',
    job_id VARCHAR(64) NOT NULL DEFAULT '',
    message TEXT,
    PRIMARY KEY (id),
    KEY ix_t_log_ts (created_at),
    KEY ix_t_log_module (module)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

@"CREATE TABLE IF NOT EXISTS t_operation (
    id BIGINT NOT NULL AUTO_INCREMENT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    type VARCHAR(32) NOT NULL DEFAULT '',
    job_id VARCHAR(64) NOT NULL DEFAULT '',
    account VARCHAR(64) NOT NULL DEFAULT '',
    status VARCHAR(16) NOT NULL DEFAULT '',
    message TEXT,
    PRIMARY KEY (id),
    KEY ix_t_operation_ts (created_at),
    KEY ix_t_operation_type (type),
    KEY ix_t_operation_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

@"CREATE TABLE IF NOT EXISTS t_number_action (
    id BIGINT NOT NULL AUTO_INCREMENT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    action VARCHAR(16) NOT NULL DEFAULT '',
    old_value VARCHAR(64) NOT NULL DEFAULT '',
    new_value VARCHAR(64) NOT NULL DEFAULT '',
    ok TINYINT NOT NULL DEFAULT 0,
    message TEXT,
    PRIMARY KEY (id),
    KEY ix_t_number_action_ts (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

@"CREATE TABLE IF NOT EXISTS t_sync_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    fetched INT NOT NULL DEFAULT 0,
    added INT NOT NULL DEFAULT 0,
    ok TINYINT NOT NULL DEFAULT 0,
    final_prefix LONGTEXT,
    message TEXT,
    PRIMARY KEY (id),
    KEY ix_t_sync_run_ts (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// Bang cau hinh cua tool: key -> value (JSON settings)
// — cũng chứa mốc sync: key 'sync_watermark:<url-api>' -> last_created_at
@"CREATE TABLE IF NOT EXISTS t_config (
    k VARCHAR(64) NOT NULL,
    v LONGTEXT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (k)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// Bang cac so da mapping (trang thai hien tai tren server) — key = "goc" cua so (dedupe 84/#/0)
@"CREATE TABLE IF NOT EXISTS t_mapped_number (
    num_key VARCHAR(32) NOT NULL,
    num VARCHAR(32) NOT NULL DEFAULT '',
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    source VARCHAR(16) NOT NULL DEFAULT '',
    PRIMARY KEY (num_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// LICH SU luy ke: moi so tung duoc mapping (append-only, den la su that lich su)
@"CREATE TABLE IF NOT EXISTS t_mapped_history (
    num_key VARCHAR(32) NOT NULL,
    num VARCHAR(32) NOT NULL DEFAULT '',
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NULL DEFAULT NULL,
    source VARCHAR(16) NOT NULL DEFAULT '',
    hits INT NOT NULL DEFAULT 1,
    PRIMARY KEY (num_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// TRANG THAI HIEN TAI: dung bang so DANG CO tren server (mirror chan doan chinh xac)
// — được làm mới mỗi lần load/sync: xóa hết rồi chèn lại đúng danh sách server
@"CREATE TABLE IF NOT EXISTS t_mapped_active (
    num_key VARCHAR(32) NOT NULL,
    num VARCHAR(32) NOT NULL DEFAULT '',
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NULL DEFAULT NULL,
    source VARCHAR(16) NOT NULL DEFAULT '',
    PRIMARY KEY (num_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// AUDIT: moi thao tac sua/xoa/doi mat khau customer VOS3000 (giu lai de doi chieu)
@"CREATE TABLE IF NOT EXISTS t_customer_log (
    id BIGINT NOT NULL AUTO_INCREMENT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    action VARCHAR(16) NOT NULL DEFAULT '',
    customer_id INT NOT NULL DEFAULT 0,
    account VARCHAR(255) NOT NULL DEFAULT '',
    old_value TEXT,
    new_value TEXT,
    ok TINYINT NOT NULL DEFAULT 0,
    message TEXT,
    PRIMARY KEY (id),
    KEY ix_t_customer_log_ts (created_at),
    KEY ix_t_customer_log_cust (customer_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// TONG HOP so theo account + huong (in/out) sau moi vong sync.
// account_id = e_customer.id, direction = 'in' | 'out', numbers = JSON array.
@"CREATE TABLE IF NOT EXISTS t_account_number_set (
    account_id INT NOT NULL,
    account VARCHAR(64) NOT NULL DEFAULT '',
    direction VARCHAR(8) NOT NULL DEFAULT '',
    numbers LONGTEXT,
    num_count INT NOT NULL DEFAULT 0,
    locked_count INT NOT NULL DEFAULT 0,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, direction),
    KEY ix_t_account_number_set_dir (direction)
) ENGINE=InnoDB DEFAULT CHARSET=utf8",

// SO API CHUA CO TREN VOS3000: chi luu lai de xu ly rieng, KHONG tu add len server.
// Moi lan sync: upsert (hits++) so con thieu; xoa so nay da xuat hien tren server.
// LUU Y: MySQL cu (< 5.6.5) chi cho 1 cot TIMESTAMP co DEFAULT/ON UPDATE CURRENT_TIMESTAMP
//        -> chi last_seen dung DATETIME; first_seen ghi bang NOW() tu app.
@"CREATE TABLE IF NOT EXISTS t_number_missing (
    num_key VARCHAR(32) NOT NULL,
    num VARCHAR(32) NOT NULL DEFAULT '',
    api_status VARCHAR(16) NOT NULL DEFAULT '',
    first_seen DATETIME NOT NULL,
    last_seen DATETIME NOT NULL,
    hits INT NOT NULL DEFAULT 1,
    PRIMARY KEY (num_key),
    KEY ix_t_number_missing_last (last_seen),
    KEY ix_t_number_missing_num (num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8"
                };

                // 1) Tạo bảng — lỗi 1 bảng không chặn các bảng còn lại (DB MySQL cũ dễ vướng
                //    giới hạn TIMESTAMP/ENGINE...). Mỗi bảng báo lỗi riêng.
                var created = new List<string>();
                var failed = new List<string>();
                foreach (var tbl in Tables.All)
                {
                    var stmt = FindSchema(schema, tbl);
                    if (stmt == null) { failed.Add($"{tbl} (không có script)"); continue; }
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = stmt;
                        await cmd.ExecuteNonQueryAsync();
                        created.Add(tbl);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{tbl}: {ex.Message}");
                    }
                }

                // 2) Bảng nào tạo lỗi vì "giá trị mặc định 0000" (MySQL mới bật NO_ZERO_DATE / STRICT)
                //    -> thử lại với sql_mode nới lỏng hơn.
                foreach (var item in failed.ToList())
                {
                    var tbl = item.Split(':')[0].Trim();
                    if (!item.Contains("invalid default value", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var relax = conn.CreateCommand();
                        relax.CommandText = $"SET SESSION sql_mode = TRIM(BOTH ',' FROM REPLACE(CONCAT(',', @@SESSION.sql_mode, ','), ',NO_ZERO_DATE,', ','))";
                        await relax.ExecuteNonQueryAsync();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = FindSchema(schema, tbl);
                        await cmd.ExecuteNonQueryAsync();
                        created.Add(tbl);
                        failed.Remove(item);
                    }
                    catch { }
                }

                // 3) Migration cũ -> mới: đổi cột ts -> created_at
                //    (bỏ qua lỗi — bảng mới đã có created_at sẵn)
                foreach (var tbl in Tables.All)
                {
                    try
                    {
                        using var mcmd = conn.CreateCommand();
                        mcmd.CommandText = $"ALTER TABLE {tbl} CHANGE ts created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP";
                        await mcmd.ExecuteNonQueryAsync();
                    }
                    catch { /* cột không tồn tại / đã là created_at */ }
                }

                // 4) Nâng cấp cũ -> mới: t_mapped_history / t_mapped_active chưa có thì nạp lại từ t_mapped_number
                try
                {
                    using var c2 = conn.CreateCommand();
                    c2.CommandText =
                        "INSERT IGNORE INTO t_mapped_history (num_key, num, source, created_at, updated_at) " +
                        "SELECT num_key, num, source, created_at, CURRENT_TIMESTAMP FROM t_mapped_number";
                    await c2.ExecuteNonQueryAsync();
                }
                catch { }

                if (failed.Count > 0)
                    return (false, "Gen bảng lỗi: " + string.Join(" | ", failed));

                return (true, string.Format(Msg.Db.GenTablesOk, string.Join(", ", Tables.All)));
            }
            catch (Exception e)
            {
                return (false, string.Format(Msg.Db.GenTablesFail, e.Message));
            }
            finally
            {
                if (own != null) await own.DisposeAsync();
            }
        }

        /// <summary>Lấy đúng câu CREATE TABLE của 1 bảng trong danh sách schema.</summary>
        static string FindSchema(string[] schema, string table)
            => schema.FirstOrDefault(s => s.Contains("CREATE TABLE IF NOT EXISTS " + table + " (", StringComparison.Ordinal));

        // ---------- Ghi (fire and forget, nuốt lỗi) ----------

        private static void Queue(Action<Settings> write)
        {
            var s = _s;
            if (s == null || !HasConfig() || string.IsNullOrWhiteSpace(s.db_host)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _gate.WaitAsync();
                    try
                    {
                        // đổi sang Settings gần nhất bằng closure: gọi lại thông qua _s
                        // (nếu user đổi config trong lúc chạy, dùng bản mới nhất)
                        write(_s ?? s);
                    }
                    finally { _gate.Release(); }
                }
                catch { /* log DB không bao giờ làm app lỗi */ }
            });
        }

        public static void Log(string module, string level, string jobId, string message)
            => Queue(s =>
            {
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_log (level, module, job_id, message) VALUES (@l, @m, @j, @msg)";
                cmd.Parameters.AddWithValue("@l", (level ?? LogLevel.Info).Length > 16 ? (level ?? "").Substring(0, 16) : (level ?? LogLevel.Info));
                cmd.Parameters.AddWithValue("@m", (module ?? "").Length > 32 ? (module ?? "").Substring(0, 32) : (module ?? ""));
                cmd.Parameters.AddWithValue("@j", jobId ?? "");
                cmd.Parameters.AddWithValue("@msg", (object)(message ?? "") ?? DBNull.Value);
                cmd.ExecuteNonQueryAsync().GetAwaiter().GetResult();
            });

        public static void RecordOperation(Settings s, string type, string jobId, string account, string status, string message)
            => Queue(x =>
            {
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_operation (type, job_id, account, status, message) VALUES (@t, @j, @a, @st, @ms)";
                cmd.Parameters.AddWithValue("@t", Cut(type, 32));
                cmd.Parameters.AddWithValue("@j", Cut(jobId, 64));
                cmd.Parameters.AddWithValue("@a", Cut(account, 64));
                cmd.Parameters.AddWithValue("@st", Cut(status, 16));
                cmd.Parameters.AddWithValue("@ms", (object)(message ?? "") ?? DBNull.Value);
                cmd.ExecuteNonQueryAsync().GetAwaiter().GetResult();
            });

        public static void RecordNumberAction(string action, string oldValue, string newValue, bool ok, string message)
            => Queue(s =>
            {
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_number_action (action, old_value, new_value, ok, message) VALUES (@a, @ov, @nv, @ok, @ms)";
                cmd.Parameters.AddWithValue("@a", Cut(action, 16));
                cmd.Parameters.AddWithValue("@ov", Cut(oldValue, 64));
                cmd.Parameters.AddWithValue("@nv", Cut(newValue, 64));
                cmd.Parameters.AddWithValue("@ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("@ms", (object)(message ?? "") ?? DBNull.Value);
                cmd.ExecuteNonQueryAsync().GetAwaiter().GetResult();
            });

        public static void RecordSyncRun(int fetched, int added, bool ok, string finalPrefix, string message)
            => Queue(s =>
            {
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_sync_run (fetched, added, ok, final_prefix, message) VALUES (@f, @a, @ok, @fp, @ms)";
                cmd.Parameters.AddWithValue("@f", fetched);
                cmd.Parameters.AddWithValue("@a", added);
                cmd.Parameters.AddWithValue("@ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("@fp", (object)Cut(finalPrefix, 10000) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ms", (object)(message ?? "") ?? DBNull.Value);
                cmd.ExecuteNonQueryAsync().GetAwaiter().GetResult();
            });

        static string Cut(string s, int n)
        {
            if (s == null) return "";
            s = s.Length > n ? s.Substring(0, n) : s;
            return s;
        }

        // ---------- Cấu hình lưu DB ----------

        /// Ghi JSON settings vào t_config (key 'app_settings').
        public static async Task SaveConfigToDbAsync(Settings s)
        {
            try
            {
                if (!HasConfig() || s == null) return;
                var json = System.Text.Json.JsonSerializer.Serialize(s, Config.JsonOpts);
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_config (k, v) VALUES (@k, @v) ON DUPLICATE KEY UPDATE v=VALUES(v), created_at=CURRENT_TIMESTAMP";
                cmd.Parameters.AddWithValue("@k", ConfigKeys.AppSettings);
                cmd.Parameters.AddWithValue("@v", json);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* cấu hình DB không làm app lỗi */ }
        }

        /// Đọc JSON settings từ t_config; null nếu chưa có/lỗi.
        public static async Task<string> LoadConfigJsonAsync()
        {
            try
            {
                if (!HasConfig()) return null;
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM t_config WHERE k=@k LIMIT 1";
                cmd.Parameters.AddWithValue("@k", ConfigKeys.AppSettings);
                var r = await cmd.ExecuteScalarAsync();
                return r as string;
            }
            catch { return null; }
        }

        /// Cập nhật danh sách số hiện có trên server (từ final prefix '1;num1,num2,#num3').
        /// - t_mapped_history : lũy kế mọi số từng mapping (append-only, hits++)
        /// - t_mapped_active  : THAY THẾ toàn bộ = đúng danh sách đang có trên server (mirror)
        public static async Task UpdateMappedNumbersAsync(string finalPrefix, string source)
        {
            try
            {
                if (!HasConfig() || string.IsNullOrWhiteSpace(finalPrefix)) return;
                var body = finalPrefix;
                int i = body.IndexOf(';');
                if (i >= 0) body = body.Substring(i + 1);

                // LƯU Ý: phải giữ ký tự '#' (số đang khóa) — trước đây filter digit-only làm mất số khóa khỏi DB
                var nums = body
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => NumberLockUtil.Clean(x))
                    .Where(x => x.Length > 0)
                    .Select(x => (Key: NumberLockUtil.TailKey(x), Num: Cut(x, 32)))
                    .Where(t => !string.IsNullOrEmpty(t.Key))
                    .GroupBy(t => t.Key)
                    .Select(g => g.Last())            // entry đến sau thắng (khớp logic syncer)
                    .ToArray();
                if (nums.Length == 0) return;

                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();

                const int Batch = 500;

                // 1) HISTORY — lũy kế
                for (int start = 0; start < nums.Length; start += Batch)
                {
                    var part = nums.Skip(start).Take(Batch).ToArray();
                    using var cmd = conn.CreateCommand();
                    var sb = new System.Text.StringBuilder("INSERT INTO t_mapped_history (num_key, num, source) VALUES ");
                    sb.Append(string.Join(",", part.Select((_, j) => $"(@k{j}, @n{j}, @s)")));
                    sb.Append(" ON DUPLICATE KEY UPDATE num=VALUES(num), source=VALUES(source), updated_at=NOW(), hits=hits+1");
                    cmd.CommandText = sb.ToString();
                    for (int j = 0; j < part.Length; j++)
                    {
                        cmd.Parameters.AddWithValue("@k" + j, part[j].Key);
                        cmd.Parameters.AddWithValue("@n" + j, part[j].Num);
                    }
                    cmd.Parameters.AddWithValue("@s", Cut(source ?? MappedSource.Read, 16));
                    await cmd.ExecuteNonQueryAsync();
                }

                // 2) ACTIVE — thay thế toàn bộ (mirror chính xác server)
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM t_mapped_active";
                    await del.ExecuteNonQueryAsync();
                }
                for (int start = 0; start < nums.Length; start += Batch)
                {
                    var part = nums.Skip(start).Take(Batch).ToArray();
                    using var cmd = conn.CreateCommand();
                    var sb = new System.Text.StringBuilder("INSERT INTO t_mapped_active (num_key, num, source) VALUES ");
                    sb.Append(string.Join(",", part.Select((_, j) => $"(@k{j}, @n{j}, @s)")));
                    cmd.CommandText = sb.ToString();
                    for (int j = 0; j < part.Length; j++)
                    {
                        cmd.Parameters.AddWithValue("@k" + j, part[j].Key);
                        cmd.Parameters.AddWithValue("@n" + j, part[j].Num);
                    }
                    cmd.Parameters.AddWithValue("@s", Cut(source ?? MappedSource.Read, 16));
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { /* DB lỗi không làm nghiệp vụ ảnh hưởng */ }
        }

        /// Fire-and-forget.
        public static void UpdateMappedNumbers(string finalPrefix, string source)
            => _ = UpdateMappedNumbersAsync(finalPrefix, source);

        // ---------- Số API chưa có trên VOS3000 (t_number_missing) ----------

        /// <summary>
        /// Ghi danh sách số API trả về nhưng KHÔNG có trên VOS3000 vào bảng t_number_missing
        /// (chỉ lưu để xử lý riêng — không tự add lên server), đồng thời xoá khỏi bảng
        /// những số nay đã xuất hiện trên server.
        /// </summary>
        /// <param name="missing">Số chưa có + trạng thái API ("locked"/"open").</param>
        /// <param name="serverKeys">Tập gốc số đang có trên VOS3000 (TailKey).</param>
        /// <param name="serverSnapshot">
        /// ẢNH CHỤP server của vòng này — cũng là tập <paramref name="serverKeys"/>.
        /// Khác null  => đây là vòng "chốt": dòng cũ nào KHÔNG còn trong ảnh chụp (số đã rời server)
        ///               cũng được xoá, để bảng luôn = đúng số đang còn thiếu trên VOS.
        /// Null       => không đọc đủ server / dọn rác không xác nhận -> chỉ thêm/upsert, KHÔNG xoá dòng cũ.
        /// </param>
        public static async Task<(int Saved, int Removed)> SaveMissingNumbersAsync(
            IEnumerable<(string Num, string Status)> missing,
            IEnumerable<string> serverKeys,
            IEnumerable<string> serverSnapshot = null)
        {
            if (!HasConfig()) return (0, 0);
            try
            {
                var miss = (missing ?? Enumerable.Empty<(string Num, string Status)>())
                    .Select(x => (
                        Key: NumberLockUtil.TailKey(x.Num),
                        Num: Cut(NumberLockUtil.Clean(x.Num), 32),
                        Status: Cut(x.Status ?? "", 16)))
                    .Where(x => x.Key.Length > 0)
                    .GroupBy(x => x.Key)
                    .Select(g => g.Last())          // entry đến sau thắng
                    .ToArray();

                var keys = (serverKeys ?? Enumerable.Empty<string>())
                    .Where(k => !string.IsNullOrEmpty(k))
                    .Distinct()
                    .ToArray();

                // Ảnh chụp server -> tách thành các "cửa sổ" giá trị (tránh IN (...) quá dài)
                const int Batch = 500;
                string[][] windows = null;
                if (serverSnapshot != null)
                {
                    var snap = serverSnapshot.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToArray();
                    if (snap.Length > 0)
                        windows = snap.Chunk(Batch).Select(c => c.ToArray()).ToArray();
                }

                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                await EnsureMissingTableAsync(conn);

                int removed = 0;

                // 1) Xoá số đã có trên server — không còn "chưa có" nữa
                for (int start = 0; start < keys.Length; start += Batch)
                {
                    var part = keys.Skip(start).Take(Batch).ToArray();
                    using var del = conn.CreateCommand();
                    var sb = new System.Text.StringBuilder("DELETE FROM t_number_missing WHERE num_key IN (");
                    for (int j = 0; j < part.Length; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append("@k").Append(j);
                    }
                    sb.Append(')');
                    del.CommandText = sb.ToString();
                    for (int j = 0; j < part.Length; j++)
                        del.Parameters.AddWithValue("@k" + j, part[j]);
                    removed += await del.ExecuteNonQueryAsync();
                }

                // 1b) Vòng "chốt": xoá dòng cũ đã rời khỏi server (không có trong ảnh chụp)
                //     → bảng chỉ còn đúng những số API đang trả mà VOS chưa có.
                if (windows != null)
                {
                    for (int w = 0; w < windows.Length; w++)
                    {
                        var part = windows[w];
                        using var del = conn.CreateCommand();
                        var sb = new System.Text.StringBuilder("DELETE FROM t_number_missing WHERE num_key NOT IN (");
                        for (int j = 0; j < part.Length; j++)
                        {
                            if (j > 0) sb.Append(',');
                            sb.Append("@k").Append(j);
                        }
                        sb.Append(')');
                        del.CommandText = sb.ToString();
                        for (int j = 0; j < part.Length; j++)
                            del.Parameters.AddWithValue("@k" + j, part[j]);
                        removed += await del.ExecuteNonQueryAsync();
                    }
                }

                // 2) Upsert số chưa có
                for (int start = 0; start < miss.Length; start += Batch)
                {
                    var part = miss.Skip(start).Take(Batch).ToArray();
                    using var cmd = conn.CreateCommand();
                    var sb = new System.Text.StringBuilder("INSERT INTO t_number_missing (num_key, num, api_status, first_seen, last_seen) VALUES ");
                    sb.Append(string.Join(",", part.Select((_, j) => $"(@k{j}, @n{j}, @s{j}, NOW(), NOW())")));
                    sb.Append(" ON DUPLICATE KEY UPDATE num=VALUES(num), api_status=VALUES(api_status), " +
                              "last_seen=NOW(), hits=hits+1");
                    cmd.CommandText = sb.ToString();
                    for (int j = 0; j < part.Length; j++)
                    {
                        cmd.Parameters.AddWithValue("@k" + j, part[j].Key);
                        cmd.Parameters.AddWithValue("@n" + j, part[j].Num);
                        cmd.Parameters.AddWithValue("@s" + j, part[j].Status);
                    }
                    await cmd.ExecuteNonQueryAsync();
                }

                return (miss.Length, removed);
            }
            catch
            {
                return (0, 0);   // DB lỗi không làm hỏng sync
            }
        }

        static async Task EnsureMissingTableAsync(MySqlConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS t_number_missing (
    num_key VARCHAR(32) NOT NULL,
    num VARCHAR(32) NOT NULL DEFAULT '',
    api_status VARCHAR(16) NOT NULL DEFAULT '',
    first_seen DATETIME NOT NULL,
    last_seen DATETIME NOT NULL,
    hits INT NOT NULL DEFAULT 1,
    PRIMARY KEY (num_key),
    KEY ix_t_number_missing_last (last_seen),
    KEY ix_t_number_missing_num (num)
) ENGINE=InnoDB DEFAULT CHARSET=utf8";
            await cmd.ExecuteNonQueryAsync();
        }

        static async Task EnsureAccountSetTableAsync(MySqlConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS t_account_number_set (
    account_id INT NOT NULL,
    account VARCHAR(64) NOT NULL DEFAULT '',
    direction VARCHAR(8) NOT NULL DEFAULT '',
    numbers LONGTEXT,
    num_count INT NOT NULL DEFAULT 0,
    locked_count INT NOT NULL DEFAULT 0,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, direction),
    KEY ix_t_account_number_set_dir (direction)
) ENGINE=InnoDB DEFAULT CHARSET=utf8";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Đọc danh sách số chưa có trên VOS3000 (mới thấy gần nhất trước). Ném lỗi nếu DB lỗi.</summary>
        public static async Task<List<MissingNumberRow>> LoadMissingNumbersAsync(int limit = 20000)
        {
            var list = new List<MissingNumberRow>();
            if (!HasConfig()) return list;

            using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();
            await EnsureMissingTableAsync(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT num, api_status, first_seen, last_seen, hits FROM t_number_missing " +
                              "ORDER BY last_seen DESC, num ASC LIMIT @n";
            cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 200000));
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new MissingNumberRow
                {
                    Num = r.IsDBNull(0) ? "" : r.GetString(0),
                    ApiStatus = r.IsDBNull(1) ? "" : r.GetString(1),
                    FirstSeen = r.IsDBNull(2) ? DateTime.MinValue : r.GetDateTime(2),
                    LastSeen = r.IsDBNull(3) ? DateTime.MinValue : r.GetDateTime(3),
                    Hits = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                });
            }
            return list;
        }

        /// <summary>Xoá toàn bộ bảng số chưa có. Trả số dòng đã xoá; ném lỗi nếu DB lỗi.</summary>
        public static async Task<int> ClearMissingNumbersAsync()
        {
            if (!HasConfig()) return 0;
            using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();
            await EnsureMissingTableAsync(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM t_number_missing";
            return await cmd.ExecuteNonQueryAsync();
        }

        // ---------- Tổng hợp số theo account + hướng in/out ----------

        /// <summary>
        /// Lưu bảng tổng hợp: mỗi account_id có tối đa 2 dòng (in/out).
        /// numbers lưu dạng JSON array (giữ nguyên '#' của số đang khóa).
        /// <paramref name="pruneMissing"/> = true: xoá các account không còn xuất hiện
        /// trong ảnh chụp server của vòng sync này (chỉ gọi khi vòng sync đọc/ghi thành công).
        /// </summary>
        public static async Task SaveAccountNumberSetsAsync(IEnumerable<AccountNumberGroup> groups, bool pruneMissing = false)
        {
            if (!HasConfig() || groups == null) return;
            try
            {
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                await EnsureAccountSetTableAsync(conn);
                using var tx = await conn.BeginTransactionAsync();

                var keep = new HashSet<int>();
                foreach (var g in groups)
                {
                    if (g == null || g.AccountId <= 0) continue;
                    keep.Add((int)g.AccountId);
                    await UpsertSetAsync(conn, tx, g, "in", g.In);
                    await UpsertSetAsync(conn, tx, g, "out", g.Out);
                }

                // Chỉ dọn account cũ khi vòng này có ảnh chụp account đáng tin.
                if (pruneMissing && keep.Count > 0)
                {
                    var existing = new List<int>();
                    await using (var read = conn.CreateCommand())
                    {
                        read.Transaction = tx;
                        read.CommandText = "SELECT DISTINCT account_id FROM t_account_number_set";
                        await using var r = await read.ExecuteReaderAsync();
                        while (await r.ReadAsync()) existing.Add(r.GetInt32(0));
                    }

                    foreach (var id in existing.Where(x => !keep.Contains(x)))
                    {
                        await using var del = conn.CreateCommand();
                        del.Transaction = tx;
                        del.CommandText = "DELETE FROM t_account_number_set WHERE account_id=@id";
                        del.Parameters.AddWithValue("@id", id);
                        await del.ExecuteNonQueryAsync();
                    }
                }

                await tx.CommitAsync();
            }
            catch { /* DB tổng hợp lỗi không làm hỏng sync */ }
        }

        static async Task UpsertSetAsync(MySqlConnection conn, MySqlTransaction tx,
            AccountNumberGroup g, string direction, List<string> nums)
        {
            nums ??= new List<string>();
            if (nums.Count == 0)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM t_account_number_set WHERE account_id=@id AND direction=@d";
                del.Parameters.AddWithValue("@id", g.AccountId);
                del.Parameters.AddWithValue("@d", direction);
                await del.ExecuteNonQueryAsync();
                return;
            }

            var json = JsonSerializer.Serialize(nums);
            int locked = nums.Count(x => !NumberLockUtil.IsValid(x));

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO t_account_number_set (account_id, account, direction, numbers, num_count, locked_count, updated_at)
VALUES (@id, @acc, @dir, @nums, @cnt, @locked, NOW())
ON DUPLICATE KEY UPDATE
    account=VALUES(account), numbers=VALUES(numbers), num_count=VALUES(num_count),
    locked_count=VALUES(locked_count), updated_at=NOW()";
            cmd.Parameters.AddWithValue("@id", g.AccountId);
            cmd.Parameters.AddWithValue("@acc", Cut(g.Account ?? "", 64));
            cmd.Parameters.AddWithValue("@dir", direction);
            cmd.Parameters.AddWithValue("@nums", json);
            cmd.Parameters.AddWithValue("@cnt", nums.Count);
            cmd.Parameters.AddWithValue("@locked", locked);
            await cmd.ExecuteNonQueryAsync();
        }

        public static void SaveAccountNumberSets(IEnumerable<AccountNumberGroup> groups, bool pruneMissing = false)
            => _ = SaveAccountNumberSetsAsync(groups, pruneMissing);

        /// <summary>Đọc bảng tổng hợp (account_id -> group).</summary>
        public static async Task<Dictionary<int, AccountNumberGroup>> LoadAccountNumberSetsAsync()
        {
            var res = new Dictionary<int, AccountNumberGroup>();
            if (!HasConfig()) return res;
            try
            {
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT account_id, account, direction, numbers FROM t_account_number_set ORDER BY account_id, direction";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    int id = r.GetInt32(0);
                    if (!res.TryGetValue(id, out var g))
                        res[id] = g = new AccountNumberGroup { AccountId = id, Account = r.IsDBNull(1) ? "" : r.GetString(1) };

                    var dir = r.IsDBNull(2) ? "" : r.GetString(2);
                    var json = r.IsDBNull(3) ? "[]" : r.GetString(3);
                    List<string> nums;
                    try { nums = JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
                    catch { nums = new(); }

                    if (string.Equals(dir, "in", StringComparison.OrdinalIgnoreCase)) g.In = nums;
                    else if (string.Equals(dir, "out", StringComparison.OrdinalIgnoreCase)) g.Out = nums;
                }
            }
            catch { }
            return res;
        }

        /// <summary>
        /// Đọc bảng tổng hợp dạng danh sách dòng (account + hướng + số + thời điểm cập nhật),
        /// dùng cho tab "Số theo account (VOS)".
        /// </summary>
        public static async Task<List<AccountNumberSetRow>> LoadAccountNumberSetRowsAsync()
        {
            var res = new List<AccountNumberSetRow>();
            if (!HasConfig()) return res;

            using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();
            await EnsureAccountSetTableAsync(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT account_id, account, direction, numbers, updated_at
FROM t_account_number_set
ORDER BY account, account_id, direction";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var json = r.IsDBNull(3) ? "[]" : r.GetString(3);
                List<string> nums;
                try { nums = JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
                catch { nums = new(); }

                res.Add(new AccountNumberSetRow
                {
                    AccountId = r.IsDBNull(0) ? 0 : r.GetInt32(0),
                    Account = r.IsDBNull(1) ? "" : r.GetString(1),
                    Direction = r.IsDBNull(2) ? "" : r.GetString(2),
                    Numbers = nums,
                    UpdatedAt = r.IsDBNull(4) ? null : r.GetDateTime(4)
                });
            }
            return res;
        }

        /// <summary>Xoá toàn bộ bảng tổng hợp số theo account (chỉ DB tool, không đụng VOS3000).</summary>
        public static async Task<int> ClearAccountNumberSetsAsync()
        {
            if (!HasConfig()) return 0;
            using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();
            await EnsureAccountSetTableAsync(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM t_account_number_set";
            return await cmd.ExecuteNonQueryAsync();
        }

        // ---------- Mốc last_created_at của API nguồn (t_config) ----------

        public const string SyncWatermarkKeyPrefix = "sync_watermark:";

        /// Đọc mốc đã lưu cho 1 URL API; null nếu chưa có / DB lỗi.
        public static long? ReadSyncWatermark(string syncKey)
        {
            try
            {
                if (!HasConfig() || string.IsNullOrEmpty(syncKey)) return null;
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM t_config WHERE k=@k LIMIT 1";
                var k = Cut(SyncWatermarkKeyPrefix + syncKey, 64);
                cmd.Parameters.AddWithValue("@k", k);
                var r = cmd.ExecuteScalar();
                if (r is string sv && long.TryParse(sv.Trim(), out var v) && v > 0) return v;
            }
            catch { }
            return null;
        }

        /// Ghi mốc (chỉ tăng). Trả true nếu ghi được.
        public static bool SaveSyncWatermark(string syncKey, long value)
        {
            try
            {
                if (!HasConfig() || string.IsNullOrEmpty(syncKey) || value <= 0) return false;
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO t_config (k, v) VALUES (@k, @v) " +
                    "ON DUPLICATE KEY UPDATE v = GREATEST(CAST(v AS SIGNED), CAST(VALUES(v) AS SIGNED)), created_at = CURRENT_TIMESTAMP";
                cmd.Parameters.AddWithValue("@k", Cut(SyncWatermarkKeyPrefix + syncKey, 64));
                cmd.Parameters.AddWithValue("@v", value.ToString());
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }

        public static void DeleteSyncWatermark(string syncKey)
        {
            try
            {
                if (!HasConfig() || string.IsNullOrEmpty(syncKey)) return;
                using var conn = new MySqlConnection(ConnString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM t_config WHERE k=@k";
                cmd.Parameters.AddWithValue("@k", Cut(SyncWatermarkKeyPrefix + syncKey, 64));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ---------- Đọc ----------

        public record OpRow(long Id, DateTime Ts, string Type, string Status, string Message, string JobId = "", string Account = "");

        public static async Task<List<OpRow>> LoadOperationsAsync(int limit = 500, string type = "")
        {
            var res = new List<OpRow>();
            try
            {
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = (string.IsNullOrWhiteSpace(type)
                        ? "SELECT id, created_at, type, status, message, job_id, account FROM t_operation ORDER BY id DESC LIMIT @n"
                        : "SELECT id, created_at, type, status, message, job_id, account FROM t_operation WHERE type=@t ORDER BY id DESC LIMIT @n");
                cmd.Parameters.AddWithValue("@n", limit);
                if (!string.IsNullOrWhiteSpace(type)) cmd.Parameters.AddWithValue("@t", type);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    res.Add(new OpRow(
                        r.GetInt64(0),
                        r.GetDateTime(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? "" : r.GetString(3),
                        r.IsDBNull(4) ? "" : r.GetString(4),
                        r.IsDBNull(5) ? "" : r.GetString(5),
                        r.IsDBNull(6) ? "" : r.GetString(6)));
                }
            }
            catch (Exception) { /* trả rỗng khi lỗi */ }
            return res;
        }

        /// Thong ke cho tab Tong quan.
        public static async Task<ToolDbStats> GetStatsAsync()
        {
            var st = new ToolDbStats();
            if (!HasConfig()) return st;
            try
            {
                using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM t_operation) AS ops_total,
    (SELECT COUNT(*) FROM t_operation WHERE status='success') AS ok_total,
    (SELECT COUNT(*) FROM t_operation WHERE status IN ('failed','needs_reconciliation') ) AS fail_total,
    (SELECT COUNT(*) FROM t_operation WHERE DATE(created_at)=CURDATE()) AS ops_today,
    (SELECT COUNT(*) FROM t_operation WHERE DATE(created_at)=CURDATE() AND status='success') AS ok_today,
    (SELECT COUNT(*) FROM t_operation WHERE DATE(created_at)=CURDATE() AND status IN ('failed','needs_reconciliation')) AS fail_today,
    (SELECT COUNT(*) FROM t_number_action WHERE action='lock' AND ok=1) AS lock_total,
    (SELECT COUNT(*) FROM t_number_action WHERE action='unlock' AND ok=1) AS unlock_total,
    (SELECT COUNT(*) FROM t_sync_run) AS sync_runs,
    (SELECT COUNT(*) FROM t_sync_run WHERE ok=1) AS sync_ok,
    (SELECT COALESCE(SUM(added),0) FROM t_sync_run WHERE ok=1) AS sync_added,
    (SELECT MAX(created_at) FROM t_operation) AS last_ts,
    (SELECT COUNT(*) FROM t_mapped_active) AS mapped_total,
    (SELECT COUNT(*) FROM t_mapped_active WHERE created_at>=CURDATE()) AS mapped_today,
    (SELECT COUNT(*) FROM t_mapped_history) AS mapped_history";
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    st.OpsTotal = r.GetInt64(0);
                    st.OkTotal = r.GetInt64(1);
                    st.FailTotal = r.GetInt64(2);
                    st.OpsToday = r.GetInt64(3);
                    st.OkToday = r.GetInt64(4);
                    st.FailToday = r.GetInt64(5);
                    st.LockTotal = r.GetInt64(6);
                    st.UnlockTotal = r.GetInt64(7);
                    st.SyncRuns = r.GetInt64(8);
                    st.SyncOk = r.GetInt64(9);
                    st.SyncAddedTotal = r.GetInt64(10);
                    st.LastTs = r.IsDBNull(11) ? null : r.GetDateTime(11);
                    st.MappedTotal = r.GetInt64(12);
                    st.MappedToday = r.GetInt64(13);
                    st.MappedHistory = r.GetInt64(14);
                }
                st.DbOk = true;
                st.DbErr = null;
            }
            catch (Exception e)
            {
                st.DbOk = false;
                st.DbErr = e.Message;
            }
            return st;
        }
    }
}

