using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Required for Cosmos DB emulator: bypass SSL validation and use TLS 1.2 (avoids "unexpected EOF" / SSL handshake failure)
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Register services
        services.AddSingleton<IBlobStorageService, BlobStorageService>();
        services.AddSingleton<IFileParserService, FileParserService>();
        services.AddSingleton<IPhoneNumberService, PhoneNumberService>();
        services.AddSingleton<ICosmosDbService, CosmosDbService>();

        // Initialize CosmosDB on startup
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
logger.LogInformation("========================================");
logger.LogInformation("Azure Function Starting...");
logger.LogInformation("Blob Trigger Configuration:");
logger.LogInformation("  - Container: input-files");
logger.LogInformation("  - Pattern: input-files/{{name}}");
logger.LogInformation("  - Polling Interval: 1 second");
logger.LogInformation("  - Connection: AzureWebJobsStorage");
logger.LogInformation("========================================");

host.Run();

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
