using Consul;
using lab4_bd_server.Infrastructure;
using lab4_bd_server.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue<int>("port", 5131);
var consulAddr = builder.Configuration.GetValue<string>("ConsulAddress") ?? "http://localhost:8500";

builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2); });
builder.Services.AddGrpc();
var app = builder.Build();
app.MapGrpcService<OrmService>();

Console.WriteLine("=================================================");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ORM START] Порт: {port}");
DbInitializer.Initialize();

app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var consul = new ConsulClient(c => c.Address = new Uri(consulAddr));
        var hostIp = GetLocalIpAddress();
        var serviceId = $"tictactoe-orm-{port}";

        await consul.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = serviceId,
            Name = "tictactoe-orm-service",
            Address = hostIp,
            Port = port,
            Check = new AgentServiceCheck { TCP = $"{hostIp}:{port}", Interval = TimeSpan.FromSeconds(2) }
        });

        _ = Task.Run(async () =>
        {
            string leaderKey = "service/tictactoe-orm/leader";
            string myUrl = $"http://{hostIp}:{port}";
            string? lastLeaderUrl = null;

            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                string sessionId = "";
                try
                {
                    var sResp = await consul.Session.Create(new SessionEntry { Name = $"orm-leader-{port}", TTL = TimeSpan.FromSeconds(10), LockDelay = TimeSpan.Zero, Behavior = SessionBehavior.Delete });
                    sessionId = sResp.Response;
                    var kv = new KVPair(leaderKey) { Session = sessionId, Value = Encoding.UTF8.GetBytes(myUrl) };

                    bool isLeader = (await consul.KV.Acquire(kv)).Response;

                    if (isLeader)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader] УСПЕХ: Я - ЛИДЕР ORM.");
                        ConfigureAsLeader();

                        while (isLeader && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            await consul.Session.Renew(sessionId);
                            await Task.Delay(1000);
                            var curr = await consul.KV.Get(leaderKey);
                            if (curr.Response == null || curr.Response.Session != sessionId) break;
                        }
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader] Лидерство ПОТЕРЯНО.");
                    }
                    else
                    {
                        var res = await consul.KV.Get(leaderKey);
                        if (res.Response?.Value != null)
                        {
                            string currentLeaderUrl = Encoding.UTF8.GetString(res.Response.Value);
                            if (currentLeaderUrl != lastLeaderUrl)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Follower] Новый лидер: {currentLeaderUrl}. Настройка репликации...");
                                ConfigureAsFollower(currentLeaderUrl);
                                lastLeaderUrl = currentLeaderUrl;
                            }
                        }
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[Error] {ex.Message}"); await Task.Delay(2000); }
                finally { if (!string.IsNullOrEmpty(sessionId)) await consul.Session.Destroy(sessionId); }
            }
        });
    }
    catch (Exception ex) { Console.WriteLine($"[Consul Error] {ex.Message}"); }
});

void ConfigureAsLeader()
{
    try
    {
        using var conn = new NpgsqlConnection("Host=localhost;Username=postgres;Password=mysecretpassword;Database=tictactoe;Port=5433");
        conn.Open();
        // Удаляем подписку, если мы были фолловером
        new NpgsqlCommand("DROP SUBSCRIPTION IF EXISTS tictactoe_sub", conn).ExecuteNonQuery();
        // Создаем публикацию, если её нет
        try { new NpgsqlCommand("CREATE PUBLICATION tictactoe_pub FOR ALL TABLES", conn).ExecuteNonQuery(); }
        catch (PostgresException ex) when (ex.SqlState == "42710") { /* уже есть */ }
        Console.WriteLine("[DB] Режим Лидера: Публикация активна.");
    }
    catch (Exception ex) { Console.WriteLine($"[Leader Config Error] {ex.Message}"); }
}

void ConfigureAsFollower(string leaderUrl)
{
    try
    {
        var uri = new Uri(leaderUrl);
        string leaderIp = uri.Host;

        using var conn = new NpgsqlConnection("Host=localhost;Username=postgres;Password=mysecretpassword;Database=tictactoe;Port=5433");
        conn.Open();

        // 1. Останавливаем и удаляем старую подписку, если она была
        new NpgsqlCommand("DROP SUBSCRIPTION IF EXISTS tictactoe_sub", conn).ExecuteNonQuery();

        // 2. ОЧИСТКА ТАБЛИЦЫ (Ваше требование)
        // TRUNCATE удаляет все данные, чтобы начальная синхронизация прошла без конфликтов PK
        new NpgsqlCommand("TRUNCATE TABLE games", conn).ExecuteNonQuery();
        Console.WriteLine("[DB] Таблица games очищена перед синхронизацией.");

        // 3. Создаем новую подписку
        string connStrForSub = $"host={leaderIp} port=5433 user=postgres password=mysecretpassword dbname=tictactoe";

        // Параметр copy_data = true заставит базу скачать все текущие записи из Лидера после очистки
        string sql = $"CREATE SUBSCRIPTION tictactoe_sub CONNECTION '{connStrForSub}' PUBLICATION tictactoe_pub WITH (copy_data = true)";

        new NpgsqlCommand(sql, conn).ExecuteNonQuery();
        Console.WriteLine($"[DB] Режим Фолловера: Подписан на {leaderIp}. Данные синхронизируются с нуля.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Follower Config Error] {ex.Message}");
    }
}

app.Run();

static string GetLocalIpAddress()
{
    // Ищем среди всех сетевых интерфейсов
    foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
        // Нас интересуют только работающие интерфейсы (Ethernet или Wi-Fi)
        if (netInterface.OperationalStatus == OperationalStatus.Up &&
            (netInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
             netInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            var properties = netInterface.GetIPProperties();
            foreach (var ip in properties.UnicastAddresses)
            {
                // Нас интересует только IPv4 и адрес не должен начинаться с 127. (localhost)
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip.Address))
                {
                    string addr = ip.Address.ToString();

                    // Дополнительная проверка: обычно домашние сети это 192.168.x.x
                    // Мы игнорируем сети Docker (обычно 172.x.x.x или 192.168.224.x в вашем случае)
                    if (addr.StartsWith("192.168.0.") || addr.StartsWith("192.168.1."))
                    {
                        return addr;
                    }
                }
            }
        }
    }
    return "localhost"; // Если ничего не нашли
}