using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Security.Claims;
using AChat.Api.Hubs;
using AChat.Api.Services;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using AChat.Infrastructure.Security;
using AChat.Infrastructure.Telegram;
using AChat.Api.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseVector()));

// Encryption
var encryptionKey = builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key is not configured.");
builder.Services.AddSingleton<IEncryptionService>(_ => new AesEncryptionService(encryptionKey));

// Authentication (JWT Bearer)
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;
var keyBytes = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
        };
        // Allow SignalR to pass token via query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireClaim("role", "admin"));
});
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Connection registry (singleton — tracks live SignalR connections per user)
builder.Services.AddSingleton<IChatConnectionRegistry, ChatConnectionRegistry>();

// Bot-initiated message service
builder.Services.AddScoped<IBotInitiatedMessageService, BotInitiatedMessageService>();

// Evolution options
builder.Services.Configure<EvolutionOptions>(builder.Configuration.GetSection("Evolution"));

// LLM factory
builder.Services.AddSingleton<ILLMProviderFactory, LLMProviderFactory>();
builder.Services.AddScoped<ILLMUsageStatsRecorder, LLMUsageStatsRecorder>();

// Telegram webhook service
builder.Services.AddHttpClient<ITelegramWebhookService, TelegramWebhookService>();

// Telegram rate limiting and dispatch queue
builder.Services.Configure<TelegramRateLimitingOptions>(builder.Configuration.GetSection("Telegram:RateLimiting"));
builder.Services.AddSingleton<ITelegramRequestDispatcher, TelegramRequestDispatcher>();
builder.Services.AddHostedService(sp => (TelegramRequestDispatcher)sp.GetRequiredService<ITelegramRequestDispatcher>());

// Telegram message handler (scoped — uses DbContext)
builder.Services.AddScoped<TelegramHandlerService>(sp =>
{
    var db = sp.GetRequiredService<AppDbContext>();
    var factory = sp.GetRequiredService<ILLMProviderFactory>();
    var usageStatsRecorder = sp.GetRequiredService<ILLMUsageStatsRecorder>();
    var opts = sp.GetRequiredService<IOptions<EvolutionOptions>>();
    var dispatcher = sp.GetRequiredService<ITelegramRequestDispatcher>();
    return new TelegramHandlerService(db, factory, usageStatsRecorder, dispatcher, opts, opts.Value.RagTopK, opts.Value.RecentMessageWindowSize);
});

// Background workers
builder.Services.AddHostedService<SummarizationWorker>();
builder.Services.AddHostedService<PersonaEvolutionWorker>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS for local dev frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

// ── Startup: migrate DB and seed admin user ───────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var adminEmail = app.Configuration["Admin:Email"]?.Trim();
    var adminPassword = app.Configuration["Admin:Password"];
    var forceUpdateFirstUser = app.Configuration.GetValue("Admin:ForceUpdateFirstUser", false);

    if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
    {
        var firstNonStubUser = await db.Users
            .Where(u => !u.IsStubAccount)
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .FirstOrDefaultAsync();

        if (firstNonStubUser is null)
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = adminEmail,
                PasswordHash = SeedHashPassword(adminPassword),
                IsAdmin = true,
                IsStubAccount = false,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            app.Logger.LogInformation("Seeded initial admin user from Admin configuration.");
        }
        else if (forceUpdateFirstUser)
        {
            var emailConflict = await db.Users.AnyAsync(u => u.Id != firstNonStubUser.Id && u.Email == adminEmail);
            if (emailConflict)
            {
                throw new InvalidOperationException(
                    "Admin:ForceUpdateFirstUser is enabled, but Admin:Email conflicts with an existing user email.");
            }

            firstNonStubUser.Email = adminEmail;
            firstNonStubUser.PasswordHash = SeedHashPassword(adminPassword);
            firstNonStubUser.IsAdmin = true;
            firstNonStubUser.IsStubAccount = false;
            await db.SaveChangesAsync();
            app.Logger.LogWarning("Force-updated first non-stub user from Admin configuration.");
        }
    }
}

// ── Middleware pipeline ───────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("DevCors");
}

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var startedAt = Stopwatch.StartNew();
    try
    {
        await next();
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        app.Logger.LogInformation(
            "Action {Method} {Path} returned {StatusCode} for user {UserId} in {ElapsedMs}ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            userId,
            startedAt.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        startedAt.Stop();
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        app.Logger.LogError(
            ex,
            "Action {Method} {Path} failed for user {UserId} after {ElapsedMs}ms",
            context.Request.Method,
            context.Request.Path,
            userId,
            startedAt.ElapsedMilliseconds);
        throw;
    }
});

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapFallbackToFile("index.html");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────
static string SeedHashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password), salt, 350_000,
        HashAlgorithmName.SHA256, 32);
    return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
}
