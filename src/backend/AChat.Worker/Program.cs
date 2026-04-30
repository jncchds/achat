using System.Text;
using AChat.Infrastructure;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using AChat.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseVector()));

// Encryption
var encryptionKey = builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key is not configured.");
builder.Services.AddSingleton<IEncryptionService>(_ => new AesEncryptionService(encryptionKey));

// LLM factory
builder.Services.AddSingleton<ILLMProviderFactory, LLMProviderFactory>();

// Evolution options
builder.Services.Configure<EvolutionOptions>(builder.Configuration.GetSection("Evolution"));

var host = builder.Build();
host.Run();

