using Grpc.Core;
using lab4_bd_server;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace lab4_bd_server.Services
{
    public class OrmService : Orm.OrmBase
    {
        private const string ConnStr = "Host=localhost;Username=postgres;Password=mysecretpassword;Database=tictactoe;Port=5433";

        private readonly ILogger<OrmService> _logger;
        public OrmService(ILogger<OrmService> logger)
        {
            _logger = logger;
        }

        public override async Task<CheckResponse> CheckSession(CheckRequest r, ServerCallContext c)
        {
            using var conn = new NpgsqlConnection(ConnStr);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT game_id FROM games WHERE player_x = @p OR player_o = @p LIMIT 1", conn);
            cmd.Parameters.AddWithValue("p", r.PlayerId);
            var result = await cmd.ExecuteScalarAsync();

            return new CheckResponse
            {
                Exists = result != null,
                GameId = result?.ToString() ?? ""
            };
        }

        public override async Task<ExitResponse> ExitGame(ExitRequest r, ServerCallContext c)
        {
            var g = await Load(new LoadRequest { GameId = r.GameId }, c);
            if (g.Success)
            {
                if (g.Game.PlayerX == r.PlayerId) g.Game.PlayerX = "";
                else if (g.Game.PlayerO == r.PlayerId) g.Game.PlayerO = "";

                if (string.IsNullOrEmpty(g.Game.PlayerX) && string.IsNullOrEmpty(g.Game.PlayerO))
                {
                    using var conn = new NpgsqlConnection(ConnStr);
                    await conn.OpenAsync();
                    using var cmd = new NpgsqlCommand("DELETE FROM games WHERE game_id = @id", conn);
                    cmd.Parameters.AddWithValue("id", r.GameId);
                    await cmd.ExecuteNonQueryAsync();
                }
                else await Save(new SaveRequest
                {
                    Game = new Game
                    {
                        Cells = g.Game.Cells,
                        SmallWinners = g.Game.SmallWinners,
                        ActiveBoardX = g.Game.ActiveBoardX,
                        ActiveBoardY = g.Game.ActiveBoardY,
                        PlayerX = g.Game.PlayerX,
                        PlayerO = g.Game.PlayerO,
                        IsXTurn = g.Game.IsXTurn,
                        Status = g.Game.Status
                    },
                    GameId = r.GameId
                }, c);
            }
            return new ExitResponse { Success = true };
        }

        public override async Task<LoadResponse> Load(LoadRequest r, ServerCallContext c)
        {
            try
            {
                using var conn = new NpgsqlConnection(ConnStr);
                await conn.OpenAsync();
                using var cmd = new NpgsqlCommand("SELECT cells, small_winners, active_board_x, active_board_y, player_x, player_o, is_x_turn, status FROM games WHERE game_id=@id", conn);
                cmd.Parameters.AddWithValue("id", r.GameId);
                using var dr = await cmd.ExecuteReaderAsync();
                if (await dr.ReadAsync()) return new LoadResponse
                {
                    Success = true,
                    Game = new Game
                    {
                        Cells = dr.GetString(0), // grpc 
                        SmallWinners = dr.GetString(1),
                        ActiveBoardX = dr.GetInt32(2),
                        ActiveBoardY = dr.GetInt32(3),
                        PlayerX = dr.GetString(4),
                        PlayerO = dr.GetString(5),
                        IsXTurn = dr.GetBoolean(6),
                        Status = dr.GetString(7)
                    }
                };
            }
            catch { }
            return new LoadResponse { Success = false };
        }

        public override async Task<SaveResponse> Save(SaveRequest r, ServerCallContext c)
        {
            try
            {
                using var conn = new NpgsqlConnection(ConnStr);
                await conn.OpenAsync();
                var sql = @"INSERT INTO games (game_id, cells, small_winners, active_board_x, active_board_y, player_x, player_o, is_x_turn, status)
                        VALUES (@id, @c, @sw, @ax, @ay, @px, @po, @t, @s)
                        ON CONFLICT (game_id) DO UPDATE SET cells=@c, small_winners=@sw, active_board_x=@ax, active_board_y=@ay, player_x=@px, player_o=@po, is_x_turn=@t, status=@s";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("id", r.GameId);
                cmd.Parameters.AddWithValue("c", new string(r.Game.Cells));
                cmd.Parameters.AddWithValue("sw", new string(r.Game.SmallWinners));
                cmd.Parameters.AddWithValue("ax", r.Game.ActiveBoardX);
                cmd.Parameters.AddWithValue("ay", r.Game.ActiveBoardY);
                cmd.Parameters.AddWithValue("px", r.Game.PlayerX ?? "");
                cmd.Parameters.AddWithValue("po", r.Game.PlayerO ?? "");
                cmd.Parameters.AddWithValue("t", r.Game.IsXTurn);
                cmd.Parameters.AddWithValue("s", r.Game.Status);
                await cmd.ExecuteNonQueryAsync();
                return new SaveResponse { Success = true };
            }
            catch { }
            return new SaveResponse { Success = false };
        }
    }
}
