using System.Text;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IFileParserService
{
    Task<FileData> ParseBlobContentAsync(Stream blobStream, string fileName);
}

public class FileParserService : IFileParserService
{
    private readonly ILogger<FileParserService> _logger;

    public FileParserService(ILogger<FileParserService> logger)
    {
        _logger = logger;
    }

    public async Task<FileData> ParseBlobContentAsync(Stream blobStream, string fileName)
    {
        var fileData = new FileData
        {
            Id = Guid.NewGuid().ToString(), // Ensure Id is explicitly set
            FileName = fileName,
            FileType = Path.GetExtension(fileName).ToLowerInvariant()
        };

        try
        {
            using var reader = new StreamReader(blobStream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            fileData.Content = content;

            // Count lines for record count
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            fileData.RecordCount = lines.Length;

            fileData.Status = "Processed";
            _logger.LogInformation("Successfully read file: {FileName}, Size: {Size} bytes, Lines: {LineCount}", 
                fileName, content.Length, fileData.RecordCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {FileName}", fileName);
            fileData.Status = "Error";
        }

        return fileData;
    }
}
