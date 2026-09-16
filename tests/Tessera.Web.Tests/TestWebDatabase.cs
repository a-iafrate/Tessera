using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;
using Tessera.Data;

namespace Tessera.Web.Tests;

// Same shape as tests/Tessera.Data.Tests/TestDatabase.cs, duplicated rather than shared across
// projects — a small, self-contained helper, not worth a new shared test-infrastructure project
// for this first F3 batch (docs/13-piano-miglioramenti.md).
internal sealed class TestWebDatabase : IDisposable
{
    private readonly SqliteConnection connection;

    public TesseraDbContext Db { get; }

    // Fresh per test instance, same reasoning as Tessera.Data.Tests/TestDatabase.cs — ExpenseService's
    // category cache (5-minute TTL) must never leak staleness between tests.
    public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    public TestWebDatabase()
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

    // Every ShoppingHandlers test exercises the handler logic itself, not the permission
    // matrix — AccessPolicyTests already owns that. Always-allow keeps every test focused on
    // one behavior.
    internal sealed class AllowAllAccessPolicy : IAccessPolicy
    {
        public Task<bool> CanAsync(Guid userId, Guid spaceId, ResourceKind resource, AccessLevel required, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
