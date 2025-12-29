using System.Diagnostics;
using System.Threading;
using Npgsql;

namespace lab4_bd_server.Infrastructure
{
    public static class DbInitializer
    {
        private const string ConnStr = "Host=localhost;Username=postgres;Password=mysecretpassword;Database=tictactoe;Port=5433";
        private const string MasterConnStr = "Host=localhost;Username=postgres;Password=mysecretpassword;Database=tictactoe;Port=5433";

        public static void Initialize()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] Инициализация...");
            if (!IsDatabaseReachable())
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] База недоступна. Попытка запустить Docker-контейнер 'tictactoe-db'...");
                EnsurePostgresContainer();
                for (int i = 0; i < 15; i++)
                {
                    if (IsDatabaseReachable()) break;
                    Thread.Sleep(1000);
                }
            }
            CreateSchema();
        }

        private static bool IsDatabaseReachable()
        {
            try { using var conn = new NpgsqlConnection(MasterConnStr); conn.Open(); return true; }
            catch { return false; }
        }

        private static void EnsurePostgresContainer()
        {
            try
            {
                var checkProc = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -a --filter \"name=tictactoe-db\" --format \"{{.Names}}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(checkProc);
                string output = process?.StandardOutput.ReadToEnd() ?? "";
                process?.WaitForExit();

                if (output.Contains("tictactoe-db"))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Docker] Контейнер существует, запуск...");
                    Process.Start("docker", "start tictactoe-db")?.WaitForExit();
                }
                else
                {
                    Console.WriteLine("[Docker] Создание нового контейнера с поддержкой репликации...");
                    // -c wal_level=logical нужен для логической репликации
                    string runArgs = "run --name tictactoe-db " +
                                     "-e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=mysecretpassword -e POSTGRES_DB=tictactoe " +
                                     "-p 5433:5432 -d postgres -c wal_level=logical -c max_replication_slots=10 -c max_wal_senders=10";
                    Process.Start("docker", runArgs)?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Docker Error] Ошибка работы с Docker: {ex.Message}");
            }
        }

        private static void CreateSchema()
        {
            try
            {
                using (var conn = new NpgsqlConnection(MasterConnStr))
                {
                    conn.Open();
                    using var cmdCheck = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = 'tictactoe'", conn);
                    if (cmdCheck.ExecuteScalar() == null)
                    {
                        new NpgsqlCommand("CREATE DATABASE tictactoe", conn).ExecuteNonQuery();
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] База данных 'tictactoe' создана.");
                    }
                }

                using (var conn = new NpgsqlConnection(ConnStr))
                {
                    conn.Open();
                    string sql = @"
                        CREATE TABLE IF NOT EXISTS games (
                            game_id VARCHAR(50) PRIMARY KEY,
                            cells VARCHAR(81),
                            small_winners VARCHAR(9),
                            active_board_x SMALLINT,
                            active_board_y SMALLINT,
                            player_x VARCHAR(100),
                            player_o VARCHAR(100),
                            is_x_turn BOOLEAN,
                            status VARCHAR(20)
                        );";
                    new NpgsqlCommand(sql, conn).ExecuteNonQuery();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] Схема таблиц проверена/создана.");
                }
                Console.WriteLine("[DB] Схема готова.");
            }
            catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB Error] Ошибка инициализации схемы: {ex.Message}"); }
        }
    }
}