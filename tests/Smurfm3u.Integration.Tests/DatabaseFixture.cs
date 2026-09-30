using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smurfm3u.App.Services;
using Smurfm3u.Data;
using Testcontainers.PostgreSql;

namespace Smurfm3u.Integration.Tests;

/// <summary>
/// A throwaway Postgres with the real migrations applied, shared by every test in the
/// collection.
/// <para>
/// A real one rather than a substitute, because what is being tested only exists in Postgres:
/// the trigram indexes the matching leans on, <c>ILIKE</c>, array columns, and the
/// set-based updates and deletes. An in-memory provider would answer differently, which would
/// make these tests worse than none at all.
/// </para>
/// <para>
/// Starting a container takes a few seconds, so it is started once and each test is given a
/// clean set of rows rather than a clean database.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // The same image the application runs against, so an answer here is an answer there.
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("smurfm3u")
        .WithUsername("smurfm3u")
        .WithPassword("smurfm3u")
        .Build();

    private ServiceProvider provider = null!;

    public IDbContextFactory<AppDbContext> DbFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient();

        services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(container.GetConnectionString()));

        // Mirrors the application's own registrations. Kept by hand rather than shared,
        // because Program.cs builds a web host these tests have no use for - so a service
        // added there has to be added here too, and a test that cannot resolve one says so
        // plainly rather than failing in some subtler way.
        services.AddSingleton<RefreshProgress>();
        services.AddScoped<SettingsService>();
        services.AddScoped<XtreamClient>();
        services.AddScoped<SeriesBackfill>();
        services.AddScoped<SeasonPackService>();
        services.AddScoped<SearchService>();
        services.AddScoped<SearchHistoryService>();
        services.AddScoped<CacheBrowserService>();
        services.AddScoped<BackupService>();
        services.AddScoped<ReleaseDetailsService>();

        provider = services.BuildServiceProvider();
        DbFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var db = await DbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await container.DisposeAsync();
    }

    /// <summary>A scope of the application's own services, wired to the test database.</summary>
    public IServiceScope Scope() => provider.CreateScope();

    public T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Empties everything a test writes. Settings are left alone: they are read on nearly every
    /// path and are not what any of this is testing.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();

        // Truncate rather than delete: it resets the identity columns too, so ids do not creep
        // up across a run and a test can still reason about what it inserted.
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE "DownloadFiles", "Downloads", "Items", "Series", "Sources", "Searches" RESTART IDENTITY CASCADE;""");
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
