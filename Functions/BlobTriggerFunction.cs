using BlobToCosmosFunction.Models;
using BlobToCosmosFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Functions;

public class BlobTriggerFunction
{
    private readonly IBlobStorageService _blobStorageService;
    private readonly IFileParserService _fileParserService;
    private readonly ICosmosDbService _cosmosDbService;
    private readonly IPhoneNumberService _phoneNumberService;
    private readonly ILogger<BlobTriggerFunction> _logger;

    public BlobTriggerFunction(
        IBlobStorageService blobStorageService,
        IFileParserService fileParserService,
        ICosmosDbService cosmosDbService,
        IPhoneNumberService phoneNumberService,
        ILogger<BlobTriggerFunction> logger)
    {
        _blobStorageService = blobStorageService;
        _fileParserService = fileParserService;
        _cosmosDbService = cosmosDbService;
        _phoneNumberService = phoneNumberService;
        _logger = logger;
    }

    [Function("BlobTriggerFunction")]
    public async Task Run(
        [BlobTrigger("input-files/{name}", Connection = "AzureWebJobsStorage")] byte[] blobContent,
        string name,
        FunctionContext context)
    {
        var blobName = name;
        _logger.LogInformation("=== BLOB TRIGGER FIRED ===");
        _logger.LogInformation("Blob trigger function processed blob: {Name} from container: input-files", blobName);
        _logger.LogInformation("Blob content size: {Size} bytes", blobContent?.Length ?? 0);

        try
        {
            const string containerName = "input-files";

            // Read blob using BlobStorageService (same approach as ReadBlobFunction)
            // This allows us to use SAS tokens or connection strings consistently
            using var blobStream = await _blobStorageService.ReadBlobAsync(containerName, blobName);

            // Parse the blob content
            var fileData = await _fileParserService.ParseBlobContentAsync(blobStream, blobName);

            // Initialize CosmosDB if needed
            await _cosmosDbService.InitializeAsync();

            // Save file metadata to CosmosDB
            await _cosmosDbService.SaveFileDataAsync(fileData);

            // Extract and save phone numbers (with duplicate checking)
            var phoneNumbers = _phoneNumberService.ExtractPhoneNumbers(fileData.Content, blobName);
            if (phoneNumbers.Any())
            {
                var savedPhoneNumbers = await _cosmosDbService.SavePhoneNumbersAsync(phoneNumbers, blobName);
                var newNumbers = savedPhoneNumbers.Count(p => p.OccurrenceCount == 1);
                var duplicates = savedPhoneNumbers.Count - newNumbers;
                
                _logger.LogInformation(
                    "Processed phone numbers. New: {NewCount}, Duplicates ignored: {DuplicateCount}, Total saved: {TotalCount}",
                    newNumbers,
                    duplicates,
                    savedPhoneNumbers.Count);
            }
            else
            {
                _logger.LogInformation("No phone numbers found in file: {Name}", blobName);
            }

            // Delete the blob after successful processing
            var deleted = await _blobStorageService.DeleteBlobAsync(containerName, blobName);
            if (deleted)
            {
                _logger.LogInformation("Successfully deleted blob after processing: {Name}", blobName);
            }
            else
            {
                _logger.LogWarning("Blob was already deleted or does not exist: {Name}", blobName);
            }

            _logger.LogInformation("Successfully processed and removed blob: {Name}", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing blob: {Name}", blobName);
            // Don't delete blob if processing failed - allows for retry
            throw;
        }
    }
}
