using System.Text;
using AChat.Api.Extensions;
using AChat.Api.Middleware;
using AChat.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<InitialUserOptions>(builder.Configuration.GetSection(InitialUserOptions.Section));
builder.Services.Configure<BotEvolutionOptions>(builder.Configuration.GetSection(BotEvolutionOptions.Section));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));

// JWT Authentication
var jwtSection = builder.Configuration.GetSection(JwtOptions.Section);
var jwtSecret = jwtSection["Secret"] ?? throw new InvalidOperationException("JWT Secret is required");
var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"] ?? "AChat",
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"] ?? "AChat",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddInfrastructure(builder.Configuration);

// CORS for development
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()));

var app = builder.Build();

// Apply migrations and seed data
await app.Services.ApplyMigrationsAsync();
await app.Services.SeedInitialUserAsync(app.Configuration);

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Serve React static files
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
