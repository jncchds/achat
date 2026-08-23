using AChat.Api.Auth;
using AChat.Api.Hubs;
using AChat.Api.Services;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.Llm;
using AChat.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────────────────────
// All settings live in appsettings.json and are overridable by environment variable
// using the ACHAT_ prefix (§11): ACHAT_Jwt__SigningKey → Jwt:SigningKey, etc.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("ACHAT_");

// ── Database (EF Core + PostgreSQL + pgvector, §6) ────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=achat;Username=achat;Password=achat";

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString, o => o.UseVector()));

// ── API key encryption (Data Protection, key ring in the database, §7.3) ──────
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("achat");
builder.Services.AddScoped<IApiKeyProtector, DataProtectionApiKeyProtector>();

// ── LLM provider layer (§4.1) ──────────────────────────────────────────────────
builder.Services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

// ── Auth: JWT bearer (§7.1) ────────────────────────────────────────────────────
var tokenService = new JwtTokenService(builder.Configuration);
builder.Services.AddSingleton(tokenService);
builder.Services.AddSingleton<PasswordHasher>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim names as issued (sub / name / role) instead of remapping to long URIs.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = tokenService.ValidationParameters;
        // 401/403 instead of redirect (SPA, §7.1)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Browsers can't set Authorization headers on WebSockets / SSE, so
                // SignalR app clients pass the token as an ?access_token=… query param.
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });
// Authorization: require an authenticated, *active* user by default (§7.4). A
// disabled account is refused on every authorized request, not just at login.
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ActiveUserAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new ActiveUserRequirement())
        .Build();
});

// ── Shared JSON: enums as strings, cycles ignored (shared by MVC + SignalR) ────
void ConfigureJsonSerializer(System.Text.Json.JsonSerializerOptions opts)
{
    opts.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
}

builder.Services.AddSignalR()
    .AddJsonProtocol(o => ConfigureJsonSerializer(o.PayloadSerializerOptions));

builder.Services.AddHttpContextAccessor();

// ── Chat + prompt assembly (§5.5, §5.9) ─────────────────────────────────────────
builder.Services.AddScoped<ChatService>();

// ── Rolling summary worker (§5.3) — background, never in the request path ────
builder.Services.AddHostedService<SummaryService>();

// ── Memory retrieval + extraction (§5.3–5.4) ────────────────────────────────
builder.Services.AddSingleton(MemoryRetriever.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<MemoryExtractionService>();

// ── Personality evolution (§2.4) — slow background worker, never in the request path
builder.Services.AddHostedService<PersonaEvolutionService>();

// ── Interaction-log retention (§4.4, Phase 8) — periodic pruning of the audit log
builder.Services.AddHostedService<InteractionRetentionService>();

// ── API ─────────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o => ConfigureJsonSerializer(o.JsonSerializerOptions));

var app = builder.Build();

// ── Migrations on startup — the database is never upgraded by hand (§6) ────────
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<AppDbContext>();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AChat.Bootstrap");

    log.LogInformation("Applying database migrations…");
    await db.Database.MigrateAsync();
    log.LogInformation("Database migrations applied.");

    // ── Bootstrap admin (§7.2) ──────────────────────────────────────────────────
    var loginName = app.Configuration["BootstrapAdmin:LoginName"];
    var bootstrapPassword = app.Configuration["BootstrapAdmin:Password"];
    var forceSetPassword = app.Configuration.GetValue("BootstrapAdmin:ForceSetPassword", false);

    if (!string.IsNullOrWhiteSpace(loginName) && !string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        var hasher = sp.GetRequiredService<PasswordHasher>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == loginName);
        if (user is null)
        {
            user = new User
            {
                Username = loginName,
                DisplayName = loginName,
                PasswordHash = hasher.Hash(bootstrapPassword),
                IsAdmin = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            log.LogInformation("Created bootstrap admin user '{LoginName}'.", loginName);
        }
        else if (forceSetPassword)
        {
            user.PasswordHash = hasher.Hash(bootstrapPassword);
            user.IsAdmin = true;
            await db.SaveChangesAsync();
            log.LogInformation("Force-set password for bootstrap admin '{LoginName}'.", loginName);
        }
    }
    else
    {
        log.LogInformation("No BootstrapAdmin credentials configured — skipping bootstrap admin.");
    }
}

app.UseAuthentication();
app.UseAuthorization();

// ── Static files + SPA fallback (§10) ──────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Health (§12, Phase 8 polish) ───────────────────────────────────────────────
app.MapGet("/health", (AppDbContext db) =>
    db.Database.CanConnect() ? Results.Ok(new { status = "ok", database = "ok" }) : Results.StatusCode(503))
    .ExcludeFromDescription();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

// React SPA fallback — serve index.html for all non-API, non-hub routes.
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("AChat starting on {Urls}", string.Join(", ", app.Urls));
app.Run();
