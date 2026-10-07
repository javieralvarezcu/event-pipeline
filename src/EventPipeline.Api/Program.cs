using EventPipeline.Core.Common;
using EventPipeline.Core.Data;
using EventPipeline.Core.Features.Calendar;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Core.Llm;
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

// Factory used by the audit logger and the post registry to write on their own
// contexts, without touching the request-scoped context's pending changes.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString), ServiceLifetime.Scoped);

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

// --- Services (the domain pipeline lives in EventPipeline.Core) ---
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

// --- Controllers & Swagger ---
// REST puro: sin cookie auth, sin Hangfire, sin Blazor. Las mutaciones con LLM
// exigen el header X-DeepSeek-API-Key (barrera de coste para llamadores externos).
builder.Services.AddControllers(options => options.Filters.Add<ApiExceptionFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Event Pipeline API",
        Version = "v1",
        Description = """
            API de la agenda de muxo jaleo: reconocimiento de posts, scraping y cruce
            con muxojaleo.com y calendario. La aplicación web (EventPipeline.Web) y sus
            trabajos Hangfire orquestan estos endpoints.

            **Importante:** los endpoints que llaman al LLM requieren el header
            `X-DeepSeek-API-Key` con un token válido de API de DeepSeek.
            """
    });

    c.AddSecurityDefinition("DeepSeekApiKey", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-DeepSeek-API-Key",
        Description = "Token de API de DeepSeek"
    });

    c.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "DeepSeekApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// The Web owns the schema: it creates the database and applies the migrations at
// startup. The Api never migrates, to avoid competing with it during startup.
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.MapGet("/healthz", () => Results.Ok());

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
