using System.Collections.Concurrent;
using System.Text.Json;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

/// <summary>
/// Development-only storage that uses local JSON files (no SSL, no Cosmos DB).
/// Use when corporate firewall blocks SSL to the Cosmos DB emulator.
/// Set "UseLocalStorage": "true" in local.settings.json.
/// </summary>
public class LocalStorageCosmosDbService : ICosmosDbService
{
    private readonly string _basePath;
    private readonly ILogger<LocalStorageCosmosDbService> _logger;
    private readonly ConcurrentDictionary<string, PhoneNumber> _phoneNumbers = new();
    private bool _initialized;
    private readonly object _fileLock = new();

    public LocalStorageCosmosDbService(IConfiguration configuration, ILogger<LocalStorageCosmosDbService> logger)
    {
        _logger = logger;
        var path = configuration["LocalStoragePath"] ?? "LocalCosmosData";
        _basePath = Path.Combine(Path.GetTempPath(), path);
    }

    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        try
        {
            Directory.CreateDirectory(Path.Combine(_basePath, "ProcessedFiles"));
            var phonePath = Path.Combine(_basePath, "PhoneNumbers.json");
            if (File.Exists(phonePath))
            {
                var json = File.ReadAllText(phonePath);
                var list = JsonSerializer.Deserialize<List<PhoneNumber>>(json);
                if (list != null)
                    foreach (var p in list)
                        _phoneNumbers[p.NormalizedNumber] = p;
            }
            _initialized = true;
            _logger.LogInformation("Local storage initialized (no SSL). Data path: {Path}", _basePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local storage init failed");
            throw;
        }
        return Task.CompletedTask;
    }

    public Task<FileData> SaveFileDataAsync(FileData fileData)
    {
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");
        if (string.IsNullOrEmpty(fileData.Id)) fileData.Id = Guid.NewGuid().ToString();
        var filePath = Path.Combine(_basePath, "ProcessedFiles", $"{fileData.Id}.json");
        lock (_fileLock)
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(fileData, new JsonSerializerOptions { WriteIndented = false }));
        }
        _logger.LogInformation("Saved file data locally: {FileName}", fileData.FileName);
        return Task.FromResult(fileData);
    }

    public Task<List<PhoneNumber>> SavePhoneNumbersAsync(List<PhoneNumber> phoneNumbers, string sourceFile)
    {
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");
        var saved = new List<PhoneNumber>();
        foreach (var p in phoneNumbers)
        {
            if (string.IsNullOrEmpty(p.NormalizedNumber)) continue;
            var key = p.NormalizedNumber;
            if (_phoneNumbers.TryGetValue(key, out var existingP))
            {
                existingP.LastSeenAt = DateTime.UtcNow;
                existingP.OccurrenceCount++;
                if (!existingP.SourceFiles.Contains(sourceFile)) existingP.SourceFiles.Add(sourceFile);
                saved.Add(existingP);
            }
            else
            {
                p.SourceFiles ??= new List<string>();
                if (!p.SourceFiles.Contains(sourceFile)) p.SourceFiles.Add(sourceFile);
                _phoneNumbers[key] = p;
                saved.Add(p);
            }
        }
        var phonePath = Path.Combine(_basePath, "PhoneNumbers.json");
        lock (_fileLock)
        {
            File.WriteAllText(phonePath, JsonSerializer.Serialize(_phoneNumbers.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        _logger.LogInformation("Saved {Count} phone numbers locally (no SSL)", saved.Count);
        return Task.FromResult(saved);
    }

    public Task<PhoneNumber?> GetPhoneNumberByNormalizedAsync(string normalizedNumber)
    {
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");
        return Task.FromResult(_phoneNumbers.TryGetValue(normalizedNumber, out var p) ? p : null);
    }
}
