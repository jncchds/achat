using System.Text;
using AChat.Infrastructure;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using AChat.Infrastructure.Security;
using AChat.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

// Background workers
builder.Services.AddHostedService<SummarizationWorker>();
builder.Services.AddHostedService<PersonaEvolutionWorker>();

var host = builder.Build();
host.Run();

