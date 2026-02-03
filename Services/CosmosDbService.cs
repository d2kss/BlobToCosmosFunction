using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlobToCosmosFunction.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface ICosmosDbService
{
    Task InitializeAsync();
    Task<FileData> SaveFileDataAsync(FileData fileData);
    Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile);
    Task<PhoneNumber?> GetPhoneNumberByNormalizedAsync(string normalizedNumber);
}

public class CosmosDbService : ICosmosDbService
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _fileDataContainerName;
    private readonly string _phoneNumbersContainerName;
    private readonly ILogger<CosmosDbService> _logger;
    private Database? _database;
    private Container? _fileDataContainer;
    private Container? _phoneNumbersContainer;

    public CosmosDbService(
        IConfiguration configuration,
        ILogger<CosmosDbService> logger)
    {
        _logger = logger;
        var connectionString = configuration["CosmosDBConnection"] 
            ?? throw new InvalidOperationException("CosmosDBConnection is not configured");

        _databaseName = configuration["CosmosDBDatabaseName"] ?? "BlobDataDB";
        _fileDataContainerName = configuration["CosmosDBContainerName"] ?? "ProcessedFiles";
        _phoneNumbersContainerName = configuration["CosmosDBPhoneNumbersContainerName"] ?? "PhoneNumbers";

        // Detect emulator vs Azure Cosmos DB (Azure endpoint contains "documents.azure.com")
        var isEmulator = IsEmulatorConnectionString(connectionString);

        if (isEmulator)
        {
            // Emulator: use 127.0.0.1 to avoid IPv6/SSL issues; enable SSL bypass for self-signed cert
            connectionString = connectionString
                .Replace("https://localhost:8081", "https://127.0.0.1:8081", StringComparison.OrdinalIgnoreCase)
                .Replace("https://localhost:8081/", "https://127.0.0.1:8081/", StringComparison.OrdinalIgnoreCase);
            // Docs require exact "DisableServerCertificateValidation=True" (capital T)
            if (!connectionString.Contains("DisableServerCertificateValidation", StringComparison.OrdinalIgnoreCase))
                connectionString = connectionString.TrimEnd(';') + ";DisableServerCertificateValidation=True;";
            _logger.LogInformation("CosmosDB mode: Emulator (SSL bypass enabled)");
        }
        else
        {
            _logger.LogInformation("CosmosDB mode: Azure (production)");
        }

        var cosmosClientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            RequestTimeout = TimeSpan.FromSeconds(30),
            MaxRetryAttemptsOnRateLimitedRequests = 3,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
        };

        // Only bypass SSL for emulator (Azure Cosmos DB uses valid certificates).
        // Use HttpClientHandler so all Gateway HTTP calls (including CreateDatabaseIfNotExistsAsync) use the same bypass.
        if (isEmulator)
        {
            cosmosClientOptions.ServerCertificateCustomValidationCallback = (X509Certificate2 cert, X509Chain chain, SslPolicyErrors errors) => true;
            cosmosClientOptions.HttpClientFactory = () =>
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    CheckCertificateRevocationList = false
                };
                return new HttpClient(handler);
            };
        }

        _cosmosClient = new CosmosClient(connectionString, cosmosClientOptions);
        _logger.LogInformation("CosmosClient created. Database: {DatabaseName}, Containers: {FileContainer}, {PhoneContainer}",
            _databaseName, _fileDataContainerName, _phoneNumbersContainerName);
    }

    /// <summary>True if connection string points to local emulator (localhost/127.0.0.1:8081).</summary>
    private static bool IsEmulatorConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        return connectionString.Contains("localhost:8081", StringComparison.OrdinalIgnoreCase)
               || connectionString.Contains("127.0.0.1:8081", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing CosmosDB database and container...");
            _logger.LogInformation("Connecting to CosmosDB at: {Endpoint}", _cosmosClient.Endpoint?.ToString() ?? "unknown");

            // Skip ReadAccountAsync (often fails with SSL on emulator). Create database directly with retry.
            const int maxRetries = 5;
            const int delayMs = 3000;
            Exception? lastEx = null;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
                    _logger.LogInformation("Successfully connected to CosmosDB. Database: {DatabaseName}", _databaseName);
                    lastEx = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    _logger.LogWarning(ex, "CosmosDB init attempt {Attempt}/{Max} failed. Retrying in {Delay}ms...", attempt, maxRetries, delayMs);
                    if (attempt < maxRetries)
                        await Task.Delay(delayMs);
                }
            }
            if (lastEx != null || _database == null)
            {
                var ex = lastEx ?? new InvalidOperationException("Database creation returned null");
                _logger.LogError(ex, "Failed to connect to CosmosDB emulator after {Max} attempts. Ensure it's running at https://127.0.0.1:8081/", maxRetries);
                var hint = (lastEx?.Message?.Contains("SSL", StringComparison.OrdinalIgnoreCase) == true ||
                            lastEx?.Message?.Contains("certificate", StringComparison.OrdinalIgnoreCase) == true)
                    ? " If SSL/certificate errors persist (e.g. corporate proxy), set \"UseLocalStorage\": \"true\" in local.settings.json to use local JSON storage instead."
                    : "";
                throw new InvalidOperationException($"Cannot connect to CosmosDB emulator. Please ensure it's running. Error: {ex.Message}.{hint}", ex);
            }

            // Create database already done above; continue with containers
            _logger.LogInformation("Database '{DatabaseName}' is ready", _databaseName);

            // Create FileData container if it doesn't exist
            var fileDataContainerProperties = new ContainerProperties(_fileDataContainerName, "/id")
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths =
                    {
                        new IncludedPath { Path = "/" }  // Required root path
                    }
                }
            };

            _fileDataContainer = await _database.CreateContainerIfNotExistsAsync(fileDataContainerProperties);
            _logger.LogInformation("Container '{ContainerName}' is ready", _fileDataContainerName);

            // Create PhoneNumbers container if it doesn't exist
            // Use NormalizedNumber as partition key for efficient lookups
            var phoneNumbersContainerProperties = new ContainerProperties(_phoneNumbersContainerName, "/NormalizedNumber")
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths =
                    {
                        new IncludedPath { Path = "/" },  // Required root path (must be first)
                        new IncludedPath { Path = "/NormalizedNumber/?" },
                        new IncludedPath { Path = "/Number/?" },
                        new IncludedPath { Path = "/SourceFile/?" }
                    }
                }
            };

            _phoneNumbersContainer = await _database.CreateContainerIfNotExistsAsync(phoneNumbersContainerProperties);
            _logger.LogInformation("Container '{ContainerName}' is ready", _phoneNumbersContainerName);
        }
        catch (CosmosException cosmosEx)
        {
            _logger.LogError(
                cosmosEx,
                "CosmosDB error initializing. StatusCode: {StatusCode}, SubStatusCode: {SubStatusCode}, Message: {Message}, ActivityId: {ActivityId}",
                cosmosEx.StatusCode,
                cosmosEx.SubStatusCode,
                cosmosEx.Message,
                cosmosEx.ActivityId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing CosmosDB: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<FileData> SaveFileDataAsync(FileData fileData)
    {
        if (_fileDataContainer == null)
        {
            await InitializeAsync();
        }

        try
        {
            // Ensure Id is set
            if (string.IsNullOrEmpty(fileData.Id))
            {
                fileData.Id = Guid.NewGuid().ToString();
                _logger.LogWarning("FileData.Id was empty, generated new Id: {Id}", fileData.Id);
            }

            _logger.LogInformation("Saving file data to CosmosDB: {FileName}, Id: {Id}", fileData.FileName, fileData.Id);

            var response = await _fileDataContainer!.CreateItemAsync(
                fileData,
                new PartitionKey(fileData.Id));

            _logger.LogInformation(
                "Successfully saved file data. File: {FileName}, RequestCharge: {RequestCharge}",
                fileData.FileName,
                response.RequestCharge);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Item with id {Id} already exists, updating instead", fileData.Id);
            var response = await _fileDataContainer!.UpsertItemAsync(
                fileData,
                new PartitionKey(fileData.Id));
            return response.Resource;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving file data to CosmosDB: {FileName}", fileData.FileName);
            throw;
        }
    }

    public async Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        if (_phoneNumbersContainer == null)
        {
            await InitializeAsync();
        }

        var savedNumbers = new List<PhoneNumber>();
        var newCount = 0;
        var duplicateCount = 0;
        var updatedCount = 0;

        try
        {
            _logger.LogInformation("Processing {Count} phone numbers from {SourceFile}", phoneNumbers.Count, sourceFile);

            foreach (var phoneNumber in phoneNumbers)
            {
                try
                {
                    // Check if phone number already exists
                    var existing = await GetPhoneNumberByNormalizedAsync(phoneNumber.NormalizedNumber);

                    if (existing != null)
                    {
                        // Phone number exists - update it
                        existing.LastSeenAt = DateTime.UtcNow;
                        existing.OccurrenceCount++;
                        
                        // Add source file if not already in the list
                        if (!existing.SourceFiles.Contains(sourceFile))
                        {
                            existing.SourceFiles.Add(sourceFile);
                        }

                        var response = await _phoneNumbersContainer!.UpsertItemAsync(
                            existing,
                            new PartitionKey(existing.NormalizedNumber));

                        savedNumbers.Add(response.Resource);
                        duplicateCount++;
                        updatedCount++;
                        
                        _logger.LogDebug(
                            "Updated existing phone number: {Number} (seen {Count} times)",
                            phoneNumber.Number,
                            existing.OccurrenceCount);
                    }
                    else
                    {
                        // New phone number - insert it
                        var response = await _phoneNumbersContainer!.CreateItemAsync(
                            phoneNumber,
                            new PartitionKey(phoneNumber.NormalizedNumber));

                        savedNumbers.Add(response.Resource);
                        newCount++;
                        
                        _logger.LogDebug("Inserted new phone number: {Number}", phoneNumber.Number);
                    }
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // Handle race condition - another process might have inserted it
                    _logger.LogWarning("Conflict inserting phone number {Number}, retrying lookup", phoneNumber.Number);
                    var existing = await GetPhoneNumberByNormalizedAsync(phoneNumber.NormalizedNumber);
                    if (existing != null)
                    {
                        existing.LastSeenAt = DateTime.UtcNow;
                        existing.OccurrenceCount++;
                        if (!existing.SourceFiles.Contains(sourceFile))
                        {
                            existing.SourceFiles.Add(sourceFile);
                        }
                        var response = await _phoneNumbersContainer!.UpsertItemAsync(
                            existing,
                            new PartitionKey(existing.NormalizedNumber));
                        savedNumbers.Add(response.Resource);
                        duplicateCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving phone number: {Number}", phoneNumber.Number);
                    // Continue with next phone number
                }
            }

            _logger.LogInformation(
                "Phone number processing completed. New: {NewCount}, Duplicates: {DuplicateCount}, Updated: {UpdatedCount}, Total: {TotalCount}",
                newCount,
                duplicateCount,
                updatedCount,
                savedNumbers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving phone numbers to CosmosDB");
            throw;
        }

        return savedNumbers;
    }

    public async Task<PhoneNumber?> GetPhoneNumberByNormalizedAsync(string normalizedNumber)
    {
        if (_phoneNumbersContainer == null)
        {
            await InitializeAsync();
        }

        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.NormalizedNumber = @normalizedNumber")
                .WithParameter("@normalizedNumber", normalizedNumber);

            var iterator = _phoneNumbersContainer!.GetItemQueryIterator<PhoneNumber>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(normalizedNumber)
                });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                return response.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying phone number: {NormalizedNumber}", normalizedNumber);
        }

        return null;
    }
}
