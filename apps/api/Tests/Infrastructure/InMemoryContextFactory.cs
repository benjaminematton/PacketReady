using Microsoft.EntityFrameworkCore;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Tests.Infrastructure;

/// <summary>
/// Hand-rolled <see cref="IDbContextFactory{TContext}"/> for tests. Each call returns
/// a fresh context bound to the same in-memory database name, so two contexts produced
/// by the same factory see each other's writes. Mirrors the prod factory contract
/// without spinning a real Postgres container in unit tests.
/// </summary>
internal sealed class InMemoryContextFactory : IDbContextFactory<PacketReadyDbContext>
{
    private readonly string _dbName;

    public InMemoryContextFactory(string dbName) => _dbName = dbName;

    public PacketReadyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PacketReadyDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new PacketReadyDbContext(options);
    }
}
