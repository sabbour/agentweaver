using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Creates an isolated SqliteDb backed by a unique temp file for each test.
/// Implements IAsyncDisposable so the file is removed after the test completes.
/// </summary>
public sealed class TestSqliteDb : IAsyncDisposable
{
    private readonly string _filePath;

    public SqliteDb Db { get; }

    /// <summary>Absolute path of the SQLite file, suitable for building <c>Database:Path</c> config.</summary>
    public string FilePath => _filePath;

    private TestSqliteDb(string filePath, SqliteDb db)
    {
        _filePath = filePath;
        Db = db;
    }

    public static async Task<TestSqliteDb> CreateAsync()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"agentweaver-test-{Guid.NewGuid():N}.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = filePath })
            .Build();

        var db = new SqliteDb(config);
        await db.EnsureCreatedAsync();

        return new TestSqliteDb(filePath, db);
    }

    public async ValueTask DisposeAsync()
    {
        // Wait briefly to let any pooled connections drain before deleting the file.
        await Task.Delay(50);

        foreach (var path in new[] { _filePath, _filePath + "-wal", _filePath + "-shm" })
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
