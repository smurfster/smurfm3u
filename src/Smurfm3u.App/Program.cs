using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Components;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Diagnostics;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? throw new InvalidOperationException(
                           "No database connection string. Set ConnectionStrings__Default.");

// A factory rather than a scoped context: background workers and Blazor circuits both need
// their own context, and neither maps onto a request scope.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("Smurfm3u.Data")));

builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// Registered before anything else so the log page has the startup lines too. The console
// provider is left alone; this is a second copy, not a replacement.
builder.Services.AddSingleton<LogRing>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new MemoryLoggerProvider(sp.GetRequiredService<LogRing>(), sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ProxyProvider>();
builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddSingleton<RefreshProgress>();
builder.Services.AddSingleton<SpeedLimitService>();
builder.Services.AddSingleton<SmtpNotifier>();
builder.Services.AddSingleton<NotificationService>();

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<DownloadService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<SeriesBackfill>();
builder.Services.AddScoped<SabnzbdHandler>();
builder.Services.AddScoped<XtreamClient>();
builder.Services.AddScoped<M3uRefreshService>();
builder.Services.AddScoped<FileDownloader>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<StartupRecoveryService>();
builder.Services.AddScoped<SearchHistoryService>();
builder.Services.AddScoped<ProxyTester>();

builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<RefreshScheduler>();
builder.Services.AddHostedService<MaintenanceWorker>();

// Playlists can be large and slow; downloads run for hours, so they get no overall timeout.
builder.Services.AddHttpClient("playlist", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Smurfm3u/1.0");
}).UseConfiguredProxy();

builder.Services.AddHttpClient("download", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Smurfm3u/1.0");
}).UseConfiguredProxy();

// Without this the keys live inside the container, so every rebuild or recreate invalidates
// every auth cookie and antiforgery token and signs everyone out.
var keyRingPath = builder.Configuration["DataProtection:KeyPath"] ?? "/config/keys";
try
{
    Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("Smurfm3u");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // Unwritable path (no volume mounted): fall back to the default in-container store
    // rather than refusing to start. Sessions then end when the container is replaced.
    Console.Error.WriteLine($"Could not persist data protection keys to '{keyRingPath}': {ex.Message}");
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Cookie.Name = "smurfm3u.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddControllers();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Behind a reverse proxy the original scheme and host matter: they end up in the links
// we hand Prowlarr inside search results.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Unauthenticated on purpose: the container healthcheck and any reverse proxy need it,
// and it reveals nothing beyond whether the database is reachable.
app.MapGet("/health", async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem("Database unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();

    // Before the first request, so nothing goes out direct while the settings are still unread.
    await ProxyProvider.PrimeAsync(scope.ServiceProvider);

    // Anything the previous process left half-done is put right before the first request.
    var recovery = scope.ServiceProvider.GetRequiredService<StartupRecoveryService>();
    await recovery.RecoverAsync();
}

app.Run();
