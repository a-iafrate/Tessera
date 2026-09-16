using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Tessera.Data.Tests;

// A ":memory:" SQLite database is destroyed the instant its connection closes, so the
// connection — not the context — is what has to survive for the test's lifetime. One instance
// per test class (xUnit constructs a fresh instance per [Fact]/[Theory] case by default), so
// tests never share state, and a fresh MemoryCache alongside it means MembershipRepository's
// 5-minute permission cache (docs/05-ottimizzazioni.md) can never leak staleness between tests
// the way it would if a single cache instance were reused (docs/13-piano-miglioramenti.md, F4).
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection connection;

    public TesseraDbContext Db { get; }

    public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    public TestDatabase()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TesseraDbContext>().UseSqlite(connection).Options;
        Db = new TesseraDbContext(options);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
    }
}
