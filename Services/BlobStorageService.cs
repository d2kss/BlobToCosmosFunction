using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IBlobStorageService
{
    Task<Stream> ReadBlobAsync(string containerName, string blobName);
    Task<bool> DeleteBlobAsync(string containerName, string blobName);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<BlobStorageService> _logger;
    private readonly IConfiguration _configuration;

    public BlobStorageService(
        IConfiguration configuration,
        ILogger<BlobStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Get connection string from configuration
        var connectionString = configuration["AzureWebJobsStorage"];
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("AzureWebJobsStorage connection string must be configured");
        }

        // Use connection string (supports both full connection string and UseDevelopmentStorage=true)
        _blobServiceClient = new BlobServiceClient(connectionString);
        _logger.LogInformation("Using connection string authentication for blob storage");
    }

    public async Task<Stream> ReadBlobAsync(string containerName, string blobName)
    {
        try
        {
            _logger.LogInformation("Reading blob: {ContainerName}/{BlobName}", containerName, blobName);

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (!await blobClient.ExistsAsync())
            {
                throw new FileNotFoundException($"Blob {blobName} not found in container {containerName}");
            }

            var response = await blobClient.DownloadStreamingAsync();
            return response.Value.Content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading blob: {ContainerName}/{BlobName}", containerName, blobName);
            throw;
        }
    }

    public async Task<bool> DeleteBlobAsync(string containerName, string blobName)
    {
        try
        {
            _logger.LogInformation("Deleting blob: {ContainerName}/{BlobName}", containerName, blobName);

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            var result = await blobClient.DeleteIfExistsAsync();

            if (result.Value)
            {
                _logger.LogInformation("Successfully deleted blob: {ContainerName}/{BlobName}", containerName, blobName);
                return true;
            }
            else
            {
                _logger.LogWarning("Blob does not exist: {ContainerName}/{BlobName}", containerName, blobName);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting blob: {ContainerName}/{BlobName}", containerName, blobName);
            return false;
        }
    }
}
