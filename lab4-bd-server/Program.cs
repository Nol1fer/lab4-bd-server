using lab4_bd_server.Services;
using lab4_bd_server.Infrastructure;
using Consul;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue<int>("port", 5131);
var consulAddr = builder.Configuration.GetValue<string>("ConsulAddress") ?? "http://localhost:8500";

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
});

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
        var hostIp = GetLocalIPAddress();
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

            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                string sessionId = "";
                try
                {
                    // LockDelay = Zero для мгновенного перехвата
                    var sResp = await consul.Session.Create(new SessionEntry { 
                        Name = $"orm-leader-{port}", 
                        TTL = TimeSpan.FromSeconds(10),
                        LockDelay = TimeSpan.Zero,
                        Behavior = SessionBehavior.Delete 
                    });
                    sessionId = sResp.Response;

                    var kv = new KVPair(leaderKey) { Session = sessionId, Value = Encoding.UTF8.GetBytes(myUrl) };
                    
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader Search] Попытка захватить лидерство ORM...");
                    bool acquired = (await consul.KV.Acquire(kv)).Response;

                    if (acquired)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader] УСПЕХ: Я - ЛИДЕР ORM.");
                        while (acquired && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
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
                        string currentLeader = res.Response?.Value != null ? Encoding.UTF8.GetString(res.Response.Value) : "неизвестен";
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Follower] Лидер ORM уже существует ({currentLeader}).");
                        await Task.Delay(2000); 
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[Leader Loop Error] {ex.Message}"); await Task.Delay(2000); }
                finally { if (!string.IsNullOrEmpty(sessionId)) await consul.Session.Destroy(sessionId); }
            }
        });
    }
    catch (Exception ex) { Console.WriteLine($"[Consul Error] {ex.Message}"); }
});

app.Run();

static string GetLocalIPAddress() {
    var host = Dns.GetHostEntry(Dns.GetHostName());
    return host.AddressList.FirstOrDefault(i => i.AddressFamily == AddressFamily.InterNetwork && !i.ToString().StartsWith("127."))?.ToString() ?? "127.0.0.1";
}