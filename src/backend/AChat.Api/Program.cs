using System.Text;
using AChat.Api.Hubs;
using AChat.Infrastructure;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using AChat.Infrastructure.Security;
using AChat.Infrastructure.Telegram;
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

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Evolution options
builder.Services.Configure<EvolutionOptions>(builder.Configuration.GetSection("Evolution"));

// LLM factory
builder.Services.AddSingleton<ILLMProviderFactory, LLMProviderFactory>();

// Telegram webhook service
builder.Services.AddHttpClient<ITelegramWebhookService, TelegramWebhookService>();

// Telegram message handler (scoped — uses DbContext)
builder.Services.AddScoped<TelegramHandlerService>(sp =>
{
    var db = sp.GetRequiredService<AppDbContext>();
    var factory = sp.GetRequiredService<ILLMProviderFactory>();
    var enc = sp.GetRequiredService<IEncryptionService>();
    var opts = sp.GetRequiredService<IOptions<EvolutionOptions>>().Value;
    return new TelegramHandlerService(db, factory, enc, opts.RagTopK, opts.RecentMessageWindowSize);
});

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("DevCors");
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
