using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// TLS 1.2/1.3 for Cosmos DB
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

// When using emulator, bypass SSL process-wide (Cosmos SDK may use HTTP paths that ignore CosmosClientOptions)
TryEnableEmulatorSslBypass();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Register services
        services.AddSingleton<IBlobStorageService, BlobStorageService>();
        services.AddSingleton<IFileParserService, FileParserService>();
        services.AddSingleton<IPhoneNumberService, PhoneNumberService>();

        // Use local JSON storage (no SSL) when corporate firewall blocks Cosmos DB emulator
        var useLocalStorage = context.Configuration["UseLocalStorage"];
        if (string.Equals(useLocalStorage, "true", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ICosmosDbService, LocalStorageCosmosDbService>();
        }
        else
        {
            services.AddSingleton<ICosmosDbService, CosmosDbService>();
        }

        // Initialize CosmosDB or local storage on startup
        services.AddSingleton<IHostedService, CosmosDbInitializationService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

// Log startup information
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("Startup");
var config = host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
var useLocal = config["UseLocalStorage"];
logger.LogInformation("========================================");
logger.LogInformation("Azure Function Starting...");
logger.LogInformation("  - Storage: {Mode}", string.Equals(useLocal, "true", StringComparison.OrdinalIgnoreCase) ? "Local (no SSL)" : "Cosmos DB");
logger.LogInformation("Blob Trigger: input-files/{{name}}, Polling: 1s");
logger.LogInformation("========================================");

host.Run();

static void TryEnableEmulatorSslBypass()
{
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        var isEmulator = json.Contains("localhost:8081", StringComparison.OrdinalIgnoreCase)
                         || json.Contains("127.0.0.1:8081", StringComparison.OrdinalIgnoreCase);
        if (!isEmulator) return;
        ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
    }
    catch { /* ignore */ }
}

// Service to initialize CosmosDB on startup
public class CosmosDbInitializationService : IHostedService
{
    private readonly ICosmosDbService _cosmosDbService;
    private readonly ILogger<CosmosDbInitializationService> _logger;

    public CosmosDbInitializationService(
        ICosmosDbService cosmosDbService,
        ILogger<CosmosDbInitializationService> logger)
    {
        _cosmosDbService = cosmosDbService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Initializing CosmosDB connection...");
            await _cosmosDbService.InitializeAsync();
            _logger.LogInformation("CosmosDB initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing CosmosDB");
            // Don't throw - allow function to start even if CosmosDB init fails
            // It will retry on first blob trigger
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
