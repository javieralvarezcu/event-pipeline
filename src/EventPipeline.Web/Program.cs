using EventPipeline.Core.Data;
using EventPipeline.Core.Features.Calendar;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Core.Llm;
using EventPipeline.Web.Components;
using EventPipeline.Web.Data;
using EventPipeline.Web.Features.Auth;
using EventPipeline.Web.Features.BrightData;
using EventPipeline.Web.Features.Dev;
using EventPipeline.Web.Features.IgAccounts;
using EventPipeline.Web.Features.Jobs;
using EventPipeline.Web.Features.Settings;
using EventPipeline.Web.Features.Ui;
using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database (own dedicated EventPipelineDb, served by the compose SQL Server) ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string not found. Set ConnectionStrings__DefaultConnection environment variable " +
        "or add a ConnectionStrings.DefaultConnection entry in appsettings.Development.json.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// Factory used by the audit logger, the post registry, the UI query services and
// the job activity hub to write/read on their own contexts, without touching the
// request-scoped context. Scoped: the hub (singleton) resolves it through its own
// scope per call.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString), ServiceLifetime.Scoped);

// --- Data Protection: key ring persisted in the shared DB (survives container
// recreation) so the encrypted AppSettings secrets stay readable ---
builder.Services.AddDataProtection()
    .SetApplicationName("EventPipeline")
    .PersistKeysToDbContext<AppDbContext>();

// --- Hangfire (scheduler runs in this same process) ---
// Schema "HangFire" is its own SQL Server schema, created idempotently at startup
// (PrepareSchemaIfNecessary).
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new Hangfire.SqlServer.SqlServerStorageOptions
    {
        SchemaName = "HangFire",
        PrepareSchemaIfNecessary = true,
        EnableHeavyMigrations = false,
        QueuePollInterval = TimeSpan.FromSeconds(15),
    }));
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
    // Configurables para poder lanzar una instancia de diagnóstico en paralelo
    // (otra cola y otro nombre de servidor, p. ej. Hangfire__Queues=debug).
    options.ServerName = builder.Configuration["Hangfire:ServerName"] ?? "eventpipeline-web";
    options.Queues = (builder.Configuration["Hangfire:Queues"] ?? "default")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
});

// --- Auth (custom lightweight cookie auth, single admin) ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
            : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// --- HTTP clients ---
builder.Services.AddHttpClient("DeepSeek", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient("MuxoJaleo", client =>
{
    client.BaseAddress = new Uri("https://muxojaleo.com");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
});

builder.Services.AddHttpClient("BrightData", client =>
{
    // Snapshot downloads can be slow; base URL comes from AppSettings per call.
    client.Timeout = TimeSpan.FromMinutes(10);
});

// --- Services (Core: domain pipeline; Web: settings, auth, accounts, jobs, UI) ---
builder.Services.AddScoped<IDeepSeekService, DeepSeekService>();
builder.Services.AddScoped<IDeepSeekAuditService, DeepSeekAuditService>();
builder.Services.AddScoped<IMuxoScraperService, MuxoScraperService>();
builder.Services.AddScoped<IPostRegistryService, PostRegistryService>();
// Singleton: the process-wide analysis turn.
builder.Services.AddSingleton<AnalysisTurn>();
builder.Services.AddSingleton<IcsCalendarBuilder>();

builder.Services.AddScoped<IRecognitionService, RecognitionService>();
builder.Services.AddScoped<IEventQueryService, EventQueryService>();
builder.Services.AddScoped<IEventCrudService, EventCrudService>();
builder.Services.AddScoped<ICleanupService, CleanupService>();
builder.Services.AddScoped<IMuxoSyncService, MuxoSyncService>();
builder.Services.AddScoped<ICrossCheckService, CrossCheckService>();
builder.Services.AddScoped<IMuxoQueryService, MuxoQueryService>();

builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IIgAccountService, IgAccountService>();
builder.Services.AddScoped<IBrightDataService, BrightDataService>();
builder.Services.AddScoped<IJobRunService, JobRunService>();
builder.Services.AddScoped<UiQueryService>();
builder.Services.AddScoped<JobsService>();
builder.Services.AddScoped<IgScrapeJob>();
builder.Services.AddScoped<MuxoJob>();
builder.Services.AddScoped<CleanupJob>();

// Singleton: log en vivo de los jobs (broadcast en proceso + persistencia).
builder.Services.AddSingleton<IJobActivityHub, JobActivityHub>();

// --- Controllers (only AuthController with antiforgery) & Blazor Server UI ---
builder.Services.AddControllersWithViews();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// The Web owns the schema: it creates the database and applies the migrations at
// startup (dev and prod — the DB is exclusively ours). The Api never migrates.
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Solo en Desarrollo: /api/* y /swagger* se reenvían a la Api local (5200),
// igual que el enrutado por path de cloudflared en producción.
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevApiProxyMiddleware>();
}

app.UseStaticFiles();

// Required by .NET 8 for the static SSR login form (renders/validates the
// antiforgery token); MVC's [ValidateAntiForgeryToken] keeps working as well.
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();

// Seed (IG accounts, admin user, default settings) — fails fast with a clear log
// message when the AddWebAppTables migration has not been applied yet.
await SeedHost.EnsureSeededAsync(app.Services, app.Logger);

// Register the recurring jobs with their configured (or default) crons.
using (var scope = app.Services.CreateScope())
{
    var jobs = scope.ServiceProvider.GetRequiredService<JobsService>();
    await jobs.EnsureRecurringJobsAsync();
}

app.Run();
