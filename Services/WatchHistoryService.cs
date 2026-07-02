using Microsoft.Data.Sqlite;

namespace FlameStreamBackend.Services;

public sealed record WatchHistoryEntry(string Path, double PositionSeconds, double DurationSeconds, string LastWatchedUtc);

public class WatchHistoryService
{
    private readonly string _connectionString;

    public WatchHistoryService(ServerSettings settings)
    {
        var dbPath = Path.Combine(settings.CacheRoot, "watchhistory.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task EnsureSchemaAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS WatchHistory (
                Path            TEXT PRIMARY KEY,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL NOT NULL,
                LastWatchedUtc  TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertAsync(string path, double positionSeconds, double durationSeconds)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WatchHistory (Path, PositionSeconds, DurationSeconds, LastWatchedUtc)
            VALUES ($path, $position, $duration, $now)
            ON CONFLICT(Path) DO UPDATE SET
                PositionSeconds = excluded.PositionSeconds,
                DurationSeconds = excluded.DurationSeconds,
                LastWatchedUtc  = excluded.LastWatchedUtc;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$position", positionSeconds);
        cmd.Parameters.AddWithValue("$duration", durationSeconds);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<WatchHistoryEntry?> GetAsync(string path)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path, PositionSeconds, DurationSeconds, LastWatchedUtc FROM WatchHistory WHERE Path = $path;";
        cmd.Parameters.AddWithValue("$path", path);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new WatchHistoryEntry(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetString(3));
    }

    public async Task<List<WatchHistoryEntry>> GetContinueWatchingAsync(int limit = 20)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Path, PositionSeconds, DurationSeconds, LastWatchedUtc
            FROM WatchHistory
            WHERE DurationSeconds <= 0 OR PositionSeconds < DurationSeconds * 0.95
            ORDER BY LastWatchedUtc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<WatchHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new WatchHistoryEntry(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetString(3)));
        return result;
    }
}
