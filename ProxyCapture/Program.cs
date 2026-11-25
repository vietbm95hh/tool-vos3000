// TCP Proxy cho VOS3000: listen tren moi IP cua may (0.0.0.0:1202),
// forward sang server that. KHONG can hosts/netsh/route.
// User chi can doi Server IP trong man hinh login VOS3000 thanh 127.0.0.1
using System.Net;
using System.Net.Sockets;
using System.Text;

string targetIp = "172.29.101.32";
int listenPort = 1202;
int targetPort = 1202;

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--target-ip": targetIp = args[i + 1]; break;
        case "--listen-port": listenPort = int.Parse(args[i + 1]); break;
        case "--target-port": targetPort = int.Parse(args[i + 1]); break;
    }
}

string logDir = Path.Combine(AppContext.BaseDirectory, "capture");
Directory.CreateDirectory(logDir);

int sessionCounter = 0;

TcpListener listen;
try
{
    // Listen moi IP: 127.0.0.1, 172.29.101.55, ... deu bat duoc
    listen = new TcpListener(IPAddress.Any, listenPort);
    listen.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    listen.Start();
}
catch (Exception e)
{
    Console.WriteLine("==================================================");
    Console.WriteLine($" KHONG LISTEN DUOC 0.0.0.0:{listenPort}");
    Console.WriteLine($" LOI: {e.Message}");
    Console.WriteLine(" Goi y: kiem tra xem co chuong trinh nao dang giu port 1202.");
    return 1;
}

Console.WriteLine("==============================================");
Console.WriteLine("  VOS3000 PROXY");
Console.WriteLine($"   LISTEN : 0.0.0.0:{listenPort}  (moi IP cua may nay)");
Console.WriteLine($"   TARGET : {targetIp}:{targetPort}");
Console.WriteLine($"   LOG    : {logDir}");
Console.WriteLine("==============================================");
Console.WriteLine(" Dang doi ket noi tu VOS3000 Client...");
Console.WriteLine(" (Client dang nhap voi Server = 127.0.0.1)");
Console.WriteLine("");

while (true)
{
    TcpClient client;
    try { client = await listen.AcceptTcpClientAsync(); }
    catch { break; }

    int id = Interlocked.Increment(ref sessionCounter);
    _ = Task.Run(() => HandleAsync(client, id));
}

return 0;

async Task HandleAsync(TcpClient client, int id)
{
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    string logPath = Path.Combine(logDir, $"session_{id:D2}_{stamp}.log");

    var remote = client.Client.RemoteEndPoint as IPEndPoint;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SESSION #{id} tu {remote}");

    // Chong loop: tu choi ket noi den IP server that (neu co ai lap lai)
    if (remote != null && remote.Address.ToString() == targetIp)
    {
        Console.WriteLine($"[#{id}] TU CHOI: ket noi tu chinh IP target");
        client.Close();
        return;
    }

    using var server = new TcpClient();
    try
    {
        // Ket noi toi server that qua card mang that (binh thuong, khong loop)
        await server.ConnectAsync(targetIp, targetPort);
    }
    catch (Exception e)
    {
        Console.WriteLine($"[#{id}] KHONG NOI DUOC SERVER {targetIp}:{targetPort} - {e.Message}");
        client.Close();
        return;
    }

    Console.WriteLine($"[#{id}] OK -> Server ({targetIp}:{targetPort})");

    // Log text (hex + ascii) + cap raw binary de phan tich chinh xac 100%
    string c2sPath = Path.Combine(logDir, $"s{id:00}_c2s.bin");
    string s2cPath = Path.Combine(logDir, $"s{id:00}_s2c.bin");

    await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
    await log.WriteLineAsync($"# VOS3000 Proxy Session #{id}");
    await log.WriteLineAsync($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    await log.WriteLineAsync($"# Client: {remote}  ->  Server: {targetIp}:{targetPort}");
    await log.WriteLineAsync($"# Raw: {c2sPath} / {s2cPath}");
    await log.WriteLineAsync("#");

    var cStream = client.GetStream();
    var sStream = server.GetStream();

    var t1 = PumpAsync(cStream, sStream, log, "C->S", c2sPath);
    var t2 = PumpAsync(sStream, cStream, log, "S->C", s2cPath);

    await Task.WhenAny(t1, t2);
    try { client.Close(); } catch { }
    try { server.Close(); } catch { }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SESSION #{id} ket thuc -> {Path.GetFileName(logPath)}");
}

async Task PumpAsync(NetworkStream from, NetworkStream to, StreamWriter log, string dir, string rawPath)
{
    var buf = new byte[65536];
    long total = 0;
    FileStream? rawFs = null;
    try
    {
        if (rawPath != null) { try { rawFs = File.Create(rawPath); } catch { } }
        while (true)
        {
            int n = await from.ReadAsync(buf.AsMemory(0, buf.Length));
            if (n <= 0) break;

            total += n;
            if (rawFs != null) { await rawFs.WriteAsync(buf.AsMemory(0, n)); await rawFs.FlushAsync(); }
            await log.WriteLineAsync($"[{DateTime.Now:HH:mm:ss.fff}] {dir} ({n} bytes, total={total})");
            await log.WriteLineAsync($"  HEX: {Convert.ToHexString(buf.AsSpan(0, n))}");
            await log.WriteLineAsync($"  TXT: {ToPrintable(buf, n)}");

            await to.WriteAsync(buf.AsMemory(0, n));
            await to.FlushAsync();
        }
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"[{dir}] KET THUC: {e.Message}");
    }
    finally
    {
        if (rawFs != null) { try { rawFs.Dispose(); } catch { } }
    }
}

static string ToPrintable(byte[] data, int len)
{
    var sb = new StringBuilder(len);
    for (int i = 0; i < len; i++)
    {
        byte b = data[i];
        sb.Append(b >= 32 && b < 127 ? (char)b : '.');
    }
    return sb.ToString();
}
