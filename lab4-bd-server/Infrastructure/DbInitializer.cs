using System.Diagnostics;
using System.Threading;
using Npgsql;

namespace lab4_bd_server.Infrastructure
{
    public static class DbInitializer
    {
        private const string ConnStr = "Host=localhost;Username=admin;Password=password;Database=tictactoe;Port=5432";
        private const string MasterConnStr = "Host=localhost;Username=admin;Password=password;Database=postgres;Port=5432";

        public static void Initialize()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] Проверка доступности базы данных...");
            if (!IsDatabaseReachable())
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] База недоступна. Попытка запустить Docker-контейнер 'tictactoe-db'...");
                EnsurePostgresContainer();
                
                // Ждем поднятия порта
                for (int i = 0; i < 10; i++)
                {
                    if (IsDatabaseReachable()) break;
                    Thread.Sleep(2000);
                }
            }

            CreateSchema();
        }

        private static bool IsDatabaseReachable()
        {
            try
            {
                using var conn = new NpgsqlConnection(MasterConnStr);
                conn.Open();
                return true;
            }
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
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Docker] Контейнер не найден, создание (image: postgres)...");
                    string runArgs = "run --name tictactoe-db -e POSTGRES_USER=admin -e POSTGRES_PASSWORD=password -e POSTGRES_DB=tictactoe -p 5432:5432 -d postgres";
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
                    using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = 'tictactoe'", conn);
                    if (cmd.ExecuteScalar() == null)
                    {
                        using var createCmd = new NpgsqlCommand("CREATE DATABASE tictactoe", conn);
                        createCmd.ExecuteNonQuery();
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
                    using var tableCmd = new NpgsqlCommand(sql, conn);
                    tableCmd.ExecuteNonQuery();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB] Схема таблиц проверена/создана.");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [DB Error] Ошибка инициализации схемы: {ex.Message}"); }
        }
    }
}