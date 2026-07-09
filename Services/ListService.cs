using Microsoft.Data.Sqlite;

namespace FlameStreamBackend.Services;

/// <summary>
/// "Minha lista" — the user's saved-titles list. SQLite-backed, mirroring
/// <see cref="WatchHistoryService"/> so the list persists across devices and the Cast receiver.
/// Stores library-relative paths (same identifier the media tree / watch-history use).
/// </summary>
public class ListService
{
    private readonly string _connectionString;

    public ListService(ServerSettings settings)
    {
        var dbPath = Path.Combine(settings.CacheRoot, "mylist.db");
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
            CREATE TABLE IF NOT EXISTS MyList (
                Path     TEXT PRIMARY KEY,
                AddedUtc TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddAsync(string path)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MyList (Path, AddedUtc) VALUES ($path, $now)
            ON CONFLICT(Path) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveAsync(string path)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MyList WHERE Path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM MyList ORDER BY AddedUtc DESC;";

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }
}
