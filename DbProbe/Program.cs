// CÃ´ng cá»¥ kháº£o sÃ¡t + backup DB VOS3000 (MySQL).
// DÃ¹ng:
//   dotnet run -- show            -> xem cá»™t prefix (kÃ¨m HEX)
//   dotnet run -- backup          -> sao lÆ°u báº£ng e_gatewaymappingsetting
//   dotnet run -- restore <file>  -> phá»¥c há»“i
//   dotnet run -- sql "<cau lenh>"-> cháº¡y SELECT tá»± do
using MySqlConnector;
using System.Text;

const string ConnStr =
    "Server=172.29.101.32;Port=3306;User ID=toolEtl;Password=Cts@12345;" +
    "Database=vos3000db;SslMode=Preferred;Connection Timeout=15;AllowLoadLocalInfile=true;";

string BackupDir = Path.Combine(AppContext.BaseDirectory, "backups");
Directory.CreateDirectory(BackupDir);

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "show";

try
{
    await using var conn = new MySqlConnection(ConnStr);
    await conn.OpenAsync();

    switch (cmd)
    {
        case "show":    await ShowAsync(conn); break;
        case "backup":  await BackupAsync(conn); break;
        case "restore":
            if (args.Length < 2) { Console.WriteLine("Thieu file backup"); break; }
            await RestoreAsync(conn, args[1]);
            break;
        case "sql":
            await SqlAsync(conn, string.Join(" ", args.Skip(1)));
            break;
        case "setprefix":
            if (args.Length < 2) { Console.WriteLine("Thieu gia tri"); break; }
            await SetPrefixAsync(conn, args[1]);
            break;
        default:
            Console.WriteLine("Lenh: show | backup | restore <file> | sql \"<select ...>\"");
            break;
    }
}
catch (Exception e)
{
    Console.WriteLine("LOI: " + e.Message);
    Environment.Exit(1);
}

// ---------------------------------------------------------------------------

async Task ShowAsync(MySqlConnection conn)
{
    const string sql = @"
SELECT gatewaymapping_id,
       calloutcallerprefixes      AS caller_prefix,
       calloutcalleeprefixes      AS callee_prefix,
       scheduledcalloutprefixes   AS sched_prefix,
       rewriterulesoutcaller      AS rewrite_caller,
       rewriterulesoutcallee      AS rewrite_callee
FROM e_gatewaymappingsetting";

    await using var cmd = new MySqlCommand(sql, conn);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        Console.WriteLine($"gatewaymapping_id = {r["gatewaymapping_id"]}");
        for (int i = 1; i < r.FieldCount; i++)
        {
            string val = r.IsDBNull(i) ? "<NULL>" : r.GetValue(i).ToString();
            Console.WriteLine($"  {r.GetName(i),-16} = [{val}]");
            if (!r.IsDBNull(i))
                Console.WriteLine($"  {"",-16}   HEX = {Convert.ToHexString(Encoding.UTF8.GetBytes(val))}");
        }
    }
}

async Task SqlAsync(MySqlConnection conn, string sql)
{
    if (string.IsNullOrWhiteSpace(sql)) { Console.WriteLine("Thieu cau lenh SQL"); return; }

    await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 60 };
    await using var r = await cmd.ExecuteReaderAsync();

    int rows = 0;
    do
    {
        if (r.FieldCount == 0) continue;
        var heads = Enumerable.Range(0, r.FieldCount).Select(r.GetName);
        Console.WriteLine(string.Join(" | ", heads));
        Console.WriteLine(new string('-', 100));

        while (await r.ReadAsync())
        {
            var vals = Enumerable.Range(0, r.FieldCount)
                .Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i)) ?? "");
            Console.WriteLine(string.Join(" | ", vals));
            if (++rows > 500) { Console.WriteLine("...(gioi han 500)"); break; }
        }
    } while (await r.NextResultAsync());

    Console.WriteLine($"== {rows} dong ==");
}

async Task SetPrefixAsync(MySqlConnection conn, string value)
{
    const string sql = "UPDATE e_gatewaymappingsetting SET calloutcallerprefixes = @v WHERE gatewaymapping_id = 223";
    await using var cmd = new MySqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@v", value);
    int n = await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"UPDATE {n} dong -> calloutcallerprefixes = [{value}]");

    await using var check = new MySqlCommand(
        "SELECT calloutcallerprefixes FROM e_gatewaymappingsetting WHERE gatewaymapping_id = 223", conn);
    await using var r = await check.ExecuteReaderAsync();
    if (await r.ReadAsync())
        Console.WriteLine($"DOC LAI = [{r.GetValue(0)}]");
}

async Task BackupAsync(MySqlConnection conn)
{
    await using var cmd = new MySqlCommand("SELECT * FROM e_gatewaymappingsetting", conn);
    await using var r = await cmd.ExecuteReaderAsync();

    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    var sb = new StringBuilder();
    sb.AppendLine("-- Backup e_gatewaymappingsetting " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

    int n = 0;
    while (await r.ReadAsync())
    {
        n++;
        var vals = new List<string>();
        for (int i = 0; i < r.FieldCount; i++)
            vals.Add(r.IsDBNull(i)
                ? "NULL"
                : "'" + r.GetValue(i).ToString().Replace("\\", "\\\\").Replace("'", "''") + "'");

        sb.AppendLine($"INSERT INTO e_gatewaymappingsetting ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
    }

    string path = Path.Combine(BackupDir, $"egatewaymappingsetting_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
    await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"DA BACKUP {n} dong -> {path}");
}

async Task RestoreAsync(MySqlConnection conn, string file)
{
    string path = File.Exists(file) ? file : Path.Combine(BackupDir, file);
    if (!File.Exists(path)) { Console.WriteLine("Khong thay file: " + path); return; }

    string content = await File.ReadAllTextAsync(path);
    var statements = content.Split('\n')
        .Select(l => l.Trim().TrimEnd('\r'))
        .Where(l => l.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase))
        .ToList();

    int n = 0;
    foreach (var stmt in statements)
    {
        await using var cmd = new MySqlCommand(stmt, conn);
        await cmd.ExecuteNonQueryAsync();
        n++;
    }
    Console.WriteLine($"DA RESTORE {n} dong tu {path}");
}
