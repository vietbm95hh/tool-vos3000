// ============================================================
//  CustomerDb - Quản lý account customer VOS3000 (bảng e_customer)
//
//  Dùng chung cấu hình DB trong Settings:
//     db_host / db_port / db_user / db_pass   -> server MySQL VOS3000
//     vos_db_name (mặc định vos3000db)         -> database của VOS3000
//  (db_name hiện tại là DB riêng của tool: vos_tool, KHÔNG dùng cho VOS)
//
//  Phân loại hướng (Direction):
//     - CALL OUT : customer có gateway mapping (e_gatewaymapping.customer_id)
//     - CALL IN  : customer có phone (e_phone.customer_id) hoặc dải số (r_customer_e164ranges)
//     - Cả hai   : IN + OUT
//     - Chưa rõ  : không có gì
//   Cột type / locktype / status vẫn đọc và hiển thị để đối chiếu.
//
//  AN TOÀN: mọi thao tác ghi đều ghi log vào t_log của tool; xoá có kiểm tra
//  ràng buộc (gateway mapping / phone) trước khi xoá.
// ============================================================
using MySqlConnector;

namespace Vos3000Agent
{
    public class CustomerRow
    {
        public int Id;
        public string Account = "";
        public string Name = "";
        public int Type;
        public int Status;
        public int LockType;
        public double Money;
        public double LimitMoney;
        public double TodayConsumption;
        public DateTime? StartTime;
        public DateTime? ValidTime;
        public DateTime? LastUpdateTime;
        public string Memo = "";
        public int CustomerId = -1;
        public int FeeRateGroupId = -1;
        public int GatewayCount;
        public int PhoneCount;
        public int RangeCount;
        public string Gateways = "";

        public bool HasOut => GatewayCount > 0;
        public bool HasIn => PhoneCount > 0 || RangeCount > 0;

        public string Direction => HasIn && HasOut ? "IN + OUT" : HasOut ? "CALL OUT" : HasIn ? "CALL IN" : "Chưa rõ";
        public string StatusText => Status switch
        {
            0 => "Active",
            1 => "Locked",
            _ => "?" + Status
        };
    }

    public static class CustomerDb
    {
        private static Settings _s;

        public static void Configure(Settings s) => _s = s;

        private static string VosDbName =>
            string.IsNullOrWhiteSpace(_s?.vos_db_name) ? "vos3000db" : _s.vos_db_name.Trim();

        public static string ConnString(string database = null)
        {
            var s = _s ?? new Settings();
            return $"Server={s.db_host};Port={s.db_port};User ID={s.db_user};Password={s.db_pass};" +
                   $"Database={database ?? VosDbName};SslMode=Preferred;Connection Timeout=10;DefaultCommandTimeout=30;AllowUserVariables=true;";
        }

        public static bool HasConfig() =>
            _s != null && !string.IsNullOrWhiteSpace(_s.db_host) && !string.IsNullOrWhiteSpace(_s.db_user);

        // ---------- ĐỌC ----------

        public static async Task<List<CustomerRow>> LoadAsync()
        {
            var list = new List<CustomerRow>();
            await using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT c.id, c.account, c.name, c.type, c.status, c.locktype,
       c.money, c.limitmoney, c.todayconsumption,
       c.starttime, c.validtime, c.lastupdatetime, c.memo,
       c.customer_id, c.feerategroup_id,
       (SELECT COUNT(*) FROM e_gatewaymapping g WHERE g.customer_id = c.id)                  AS gw_count,
       (SELECT GROUP_CONCAT(g.name ORDER BY g.name) FROM e_gatewaymapping g WHERE g.customer_id = c.id) AS gws,
       (SELECT COUNT(*) FROM e_phone p WHERE p.customer_id = c.id)                           AS phone_count,
       (SELECT COUNT(*) FROM r_customer_e164ranges r WHERE r.customer_id = c.id)             AS range_count
FROM e_customer c
ORDER BY c.account";

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new CustomerRow
                {
                    Id = r.GetInt32(0),
                    Account = S(r, 1),
                    Name = S(r, 2),
                    Type = I(r, 3),
                    Status = I(r, 4),
                    LockType = I(r, 5),
                    Money = D(r, 6),
                    LimitMoney = D(r, 7),
                    TodayConsumption = D(r, 8),
                    StartTime = DT(r, 9),
                    ValidTime = DT(r, 10),
                    LastUpdateTime = DT(r, 11),
                    Memo = S(r, 12),
                    CustomerId = I(r, 13),
                    FeeRateGroupId = I(r, 14),
                    GatewayCount = I(r, 15),
                    Gateways = S(r, 16),
                    PhoneCount = I(r, 17),
                    RangeCount = I(r, 18),
                });
            }
            return list;
        }

        /// Thông tin chi tiết: dải số + phone + gateway (để xác định hướng).
        public static async Task<string> DescribeAsync(CustomerRow c)
        {
            var sb = new System.Text.StringBuilder();
            await using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM e_gatewaymapping WHERE customer_id=@id ORDER BY name";
                cmd.Parameters.AddWithValue("@id", c.Id);
                await using var r = await cmd.ExecuteReaderAsync();
                var names = new List<string>();
                while (await r.ReadAsync()) names.Add(r.GetString(0));
                sb.AppendLine("Gateway (call-out): " + (names.Count == 0 ? "(không có)" : string.Join(", ", names)));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT e164 FROM e_phone WHERE customer_id=@id ORDER BY e164";
                cmd.Parameters.AddWithValue("@id", c.Id);
                await using var r = await cmd.ExecuteReaderAsync();
                var ph = new List<string>();
                while (await r.ReadAsync()) ph.Add(r.GetString(0));
                sb.AppendLine("Phone (call-in): " + (ph.Count == 0 ? "(không có)" : string.Join(", ", ph)));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT begine164, ende164 FROM r_customer_e164ranges WHERE customer_id=@id ORDER BY begine164";
                cmd.Parameters.AddWithValue("@id", c.Id);
                await using var r = await cmd.ExecuteReaderAsync();
                var rg = new List<string>();
                while (await r.ReadAsync()) rg.Add($"{r.GetString(0)}-{r.GetString(1)}");
                sb.AppendLine("Dải số (call-in): " + (rg.Count == 0 ? "(không có)" : string.Join(", ", rg)));
            }

            return sb.ToString().TrimEnd();
        }

        // ---------- GHI ----------

        /// Cập nhật 1 customer. Trả về chuỗi lỗi, null nếu OK.
        public static async Task<string> UpdateAsync(CustomerRow c)
        {
            if (c == null) return "Không có bản ghi.";
            if (string.IsNullOrWhiteSpace(c.Account)) return "Account không được rỗng.";
            var oldSnap = TakeOldSnapshot();

            try
            {
                await using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();

                // kiểm tra trùng account (khác id)
                await using (var chk = conn.CreateCommand())
                {
                    chk.CommandText = "SELECT COUNT(*) FROM e_customer WHERE account=@acc AND id<>@id";
                    chk.Parameters.AddWithValue("@acc", c.Account.Trim());
                    chk.Parameters.AddWithValue("@id", c.Id);
                    var cnt = Convert.ToInt64(await chk.ExecuteScalarAsync() ?? 0L);
                    if (cnt > 0) return $"Account '{c.Account}' đã tồn tại (customer khác).";
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE e_customer SET
    account=@acc, name=@name, type=@type, status=@status, locktype=@lock,
    money=@money, limitmoney=@limitmoney, validtime=@validtime, memo=@memo,
    lastupdatetime=NOW()
WHERE id=@id";
                cmd.Parameters.AddWithValue("@acc", c.Account.Trim());
                cmd.Parameters.AddWithValue("@name", c.Name ?? "");
                cmd.Parameters.AddWithValue("@type", c.Type);
                cmd.Parameters.AddWithValue("@status", c.Status);
                cmd.Parameters.AddWithValue("@lock", c.LockType);
                cmd.Parameters.AddWithValue("@money", c.Money);
                cmd.Parameters.AddWithValue("@limitmoney", c.LimitMoney);
                cmd.Parameters.AddWithValue("@validtime", (object)c.ValidTime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@memo", c.Memo ?? "");
                cmd.Parameters.AddWithValue("@id", c.Id);

                int n = await cmd.ExecuteNonQueryAsync();
                if (n <= 0)
                {
                    await AuditAsync("update", c.Id, c.Account, null, null, false, "id không tồn tại");
                    return "Không cập nhật được (id không tồn tại?).";
                }

                ToolDb.Log("customer", LogLevel.Info, "",
                    $"Sửa customer id={c.Id} account={c.Account} name={c.Name} type={c.Type} status={c.Status} lock={c.LockType}");
                await AuditAsync("update", c.Id, c.Account, oldSnap, Snapshot(c), true, null);
                return null;
            }
            catch (Exception e)
            {
                await AuditAsync("update", c.Id, c.Account, null, null, false, e.Message);
                return e.Message;
            }
        }

        [ThreadStatic] private static string _lastOldSnapshot;

        private static string TakeOldSnapshot()
        {
            var v = _lastOldSnapshot;
            _lastOldSnapshot = null;
            return v;
        }

        /// Gán snapshot "trước khi sửa" (gọi trước UpdateAsync) để audit ghi được old_value.
        public static void MarkBeforeUpdate(CustomerRow oldRow) => _lastOldSnapshot = Snapshot(oldRow);

        /// Đổi mật khẩu customer (VOS3000 lưu plaintext trong e_customer.password).
        public static async Task<string> ChangePasswordAsync(int id, string newPassword)
        {
            try
            {
                await using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE e_customer SET password=@p, lastupdatetime=NOW() WHERE id=@id";
                cmd.Parameters.AddWithValue("@p", newPassword ?? "");
                cmd.Parameters.AddWithValue("@id", id);
                int n = await cmd.ExecuteNonQueryAsync();
                if (n <= 0)
                {
                    await AuditAsync("password", id, "", null, null, false, "id không tồn tại");
                    return "Không cập nhật được mật khẩu.";
                }
                ToolDb.Log("customer", LogLevel.Info, "", $"Đổi mật khẩu customer id={id}");
                // KHÔNG ghi mật khẩu vào audit — chỉ ghi hành động
                await AuditAsync("password", id, "", null, "***", true, null);
                return null;
            }
            catch (Exception e)
            {
                await AuditAsync("password", id, "", null, null, false, e.Message);
                return e.Message;
            }
        }

        /// Xoá customer. Kiểm tra ràng buộc trước; khi xoá bỏ luôn detail + ranges + privileges.
        public static async Task<string> DeleteAsync(int id, bool force)
        {
            try
            {
                await using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();

                int gw = 0, ph = 0;
                await using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT (SELECT COUNT(*) FROM e_gatewaymapping WHERE customer_id=@id), " +
                                    "(SELECT COUNT(*) FROM e_phone WHERE customer_id=@id)";
                    c.Parameters.AddWithValue("@id", id);
                    await using var r = await c.ExecuteReaderAsync();
                    if (await r.ReadAsync()) { gw = r.GetInt32(0); ph = r.GetInt32(1); }
                }
                if (!force && (gw > 0 || ph > 0))
                    return $"Customer đang có {gw} gateway và {ph} phone — không xoá được. " +
                           "Hãy gỡ gateway/phone trước, hoặc chọn 'Xoá kèm ràng buộc'.";

                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    foreach (var sql in new[]
                    {
                        "DELETE FROM r_customer_e164ranges WHERE customer_id=@id",
                        "DELETE FROM r_customer_privileges WHERE customer_id=@id",
                        "DELETE FROM e_customerdetail     WHERE customer_id=@id",
                        "DELETE FROM e_customer           WHERE id=@id",
                    })
                    {
                        await using var d = conn.CreateCommand();
                        d.Transaction = tx;
                        d.CommandText = sql;
                        d.Parameters.AddWithValue("@id", id);
                        await d.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }

                ToolDb.Log("customer", LogLevel.Info, "", $"XOÁ customer id={id}{(force ? " (kèm ràng buộc)" : "")}");
                await AuditAsync("delete", id, "", _lastDeleteSnapshot, null, true, force ? "force (kèm gateway/phone)" : null);
                return null;
            }
            catch (Exception e)
            {
                await AuditAsync("delete", id, "", null, null, false, e.Message);
                return e.Message;
            }
        }

        [ThreadStatic] private static string _lastDeleteSnapshot;

        /// Gán snapshot customer trước khi xoá để audit ghi được old_value.
        public static void MarkBeforeDelete(CustomerRow row) => _lastDeleteSnapshot = Snapshot(row);

        // ---------- Audit (t_customer_log) ----------

        public record AuditRow(DateTime Ts, string Action, int CustomerId, string Account, string OldValue, string NewValue, bool Ok, string Message);

        /// Đọc lịch sử thao tác customer (mới nhất trước). customerId = 0 => tất cả.
        public static async Task<List<AuditRow>> LoadAuditAsync(int customerId = 0, int limit = 200)
        {
            var res = new List<AuditRow>();
            try
            {
                if (!ToolDb.HasConfig()) return res;
                await using var conn = new MySqlConnection(ToolDb.ConnString());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = customerId > 0
                    ? "SELECT created_at, action, customer_id, account, old_value, new_value, ok, message FROM t_customer_log " +
                      "WHERE customer_id=@id ORDER BY id DESC LIMIT @n"
                    : "SELECT created_at, action, customer_id, account, old_value, new_value, ok, message FROM t_customer_log " +
                      "ORDER BY id DESC LIMIT @n";
                cmd.Parameters.AddWithValue("@n", limit);
                if (customerId > 0) cmd.Parameters.AddWithValue("@id", customerId);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    res.Add(new AuditRow(
                        r.IsDBNull(0) ? DateTime.MinValue : r.GetDateTime(0),
                        S(r, 1),
                        r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                        S(r, 3), S(r, 4), S(r, 5),
                        !r.IsDBNull(6) && Convert.ToInt32(r.GetValue(6)) == 1,
                        S(r, 7)));
            }
            catch { /* trả rỗng khi lỗi */ }
            return res;
        }

        // ---------- helpers ----------

        /// Ghi log audit thao tác customer vào t_customer_log (DB của tool). Không ném lỗi.
        private static async Task AuditAsync(string action, int id, string account, string oldValue, string newValue, bool ok, string message)
        {
            try
            {
                if (!ToolDb.HasConfig()) return;
                await using var conn = new MySqlConnection(ToolDb.ConnString());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO t_customer_log (action, customer_id, account, old_value, new_value, ok, message) " +
                                  "VALUES (@a, @id, @acc, @ov, @nv, @ok, @ms)";
                cmd.Parameters.AddWithValue("@a", (action ?? "").Length > 16 ? action.Substring(0, 16) : (action ?? ""));
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@acc", (account ?? "").Length > 255 ? account.Substring(0, 255) : (account ?? ""));
                cmd.Parameters.AddWithValue("@ov", (object)oldValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nv", (object)newValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("@ms", (object)message ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* audit lỗi không được làm hỏng nghiệp vụ */ }
        }

        /// Snapshot dùng cho audit (so sánh cũ/mới).
        public static string Snapshot(CustomerRow c) =>
            c == null ? "" :
            $"account={c.Account}; name={c.Name}; type={c.Type}; status={c.Status}; lock={c.LockType}; " +
            $"money={c.Money}; limit={c.LimitMoney}; valid={c.ValidTime:yyyy-MM-dd HH:mm}; memo={c.Memo}";

        /// Đọc 1 customer theo id (dùng để lấy bản ghi cũ trước khi sửa).
        public static async Task<CustomerRow> GetAsync(int id)
        {
            var all = await LoadAsync();
            return all.FirstOrDefault(x => x.Id == id);
        }

        // ---------- Gateway mapping / routing (đồng bộ số vào/ra) ----------

        /// <summary>1 gateway mapping (call-out) + chủ sở hữu account trong e_customer.</summary>
        public sealed class GatewayMappingOwner
        {
            public int MappingId;
            public int CustomerId;
            public string Account = "";
            public string MappingName = "";
        }

        /// <summary>1 gateway routing (call-in) + account trong e_customer.</summary>
        public sealed class GatewayRoutingRow
        {
            public int Id;
            public int CustomerId;
            public string Account = "";
            public string Name = "";
            public string Prefix = "";
        }

        /// <summary>Toàn bộ gateway mapping (call-out) kèm account chủ sở hữu.</summary>
        public static async Task<List<GatewayMappingOwner>> LoadGatewayMappingOwnersAsync()
        {
            var list = new List<GatewayMappingOwner>();
            if (!HasConfig()) return list;
            await using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT g.id, g.customer_id, COALESCE(c.account, ''), COALESCE(g.name, '')
FROM e_gatewaymapping g
LEFT JOIN e_customer c ON c.id = g.customer_id
ORDER BY g.id";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new GatewayMappingOwner
                {
                    MappingId = I(r, 0),
                    CustomerId = I(r, 1),
                    Account = S(r, 2),
                    MappingName = S(r, 3)
                });
            }
            return list;
        }

        /// <summary>Toàn bộ gateway routing (call-in) kèm account chủ sở hữu.</summary>
        public static async Task<List<GatewayRoutingRow>> LoadGatewayRoutingsAsync()
        {
            var list = new List<GatewayRoutingRow>();
            if (!HasConfig()) return list;
            await using var conn = new MySqlConnection(ConnString());
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT r.id, r.clearingcustomer_id, COALESCE(c.account, ''), COALESCE(r.name, ''), COALESCE(r.prefix, '')
FROM e_gatewayrouting r
LEFT JOIN e_customer c ON c.id = r.clearingcustomer_id
WHERE r.clearingcustomer_id > 0
ORDER BY r.id";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new GatewayRoutingRow
                {
                    Id = I(r, 0),
                    CustomerId = I(r, 1),
                    Account = S(r, 2),
                    Name = S(r, 3),
                    Prefix = S(r, 4)
                });
            }
            return list;
        }

        /// <summary>
        /// Ghi prefix cho 1 gateway routing (call-in) - CHỈ UPDATE dòng đã có,
        /// không insert/delete. Trả null nếu OK, ngược lại là thông báo lỗi.
        /// </summary>
        public static async Task<string> UpdateGatewayRoutingPrefixAsync(int routingId, string prefix)
        {
            try
            {
                await using var conn = new MySqlConnection(ConnString());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE e_gatewayrouting SET prefix=@p WHERE id=@id";
                cmd.Parameters.AddWithValue("@p", prefix ?? "");
                cmd.Parameters.AddWithValue("@id", routingId);
                int n = await cmd.ExecuteNonQueryAsync();
                return n > 0 ? null : $"routing id={routingId} không tồn tại";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // ---------- helpers ----------
        private static string S(MySqlDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
        private static int I(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
        private static double D(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i));
        private static DateTime? DT(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i));

        public static string[] TypeNames = { "0 — thường (call-out)", "1", "2 — (call-in)" };
        public static string TypeText(int t) => $"type {t}";
    }
}
