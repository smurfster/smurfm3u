using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Smurfm3u.Data;

/// <summary>
/// Used only by the EF tooling when creating migrations. The connection string never
/// has to be reachable: migrations are generated from the model, not from a live database.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SMURFM3U_DESIGN_CONNECTION")
                               ?? "Host=localhost;Database=smurfm3u;Username=smurfm3u;Password=smurfm3u";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
