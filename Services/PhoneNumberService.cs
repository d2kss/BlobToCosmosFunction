using System.Text.RegularExpressions;
using BlobToCosmosFunction.Models;
using Microsoft.Extensions.Logging;

namespace BlobToCosmosFunction.Services;

public interface IPhoneNumberService
{
    List<PhoneNumber> ExtractPhoneNumbers(string content, string sourceFile);
    string NormalizePhoneNumber(string phoneNumber);
}

public class PhoneNumberService : IPhoneNumberService
{
    private readonly ILogger<PhoneNumberService> _logger;

    public PhoneNumberService(ILogger<PhoneNumberService> logger)
    {
        _logger = logger;
    }

    public List<PhoneNumber> ExtractPhoneNumbers(string content, string sourceFile)
    {
        var phoneNumbers = new HashSet<string>(); // Use HashSet to avoid duplicates in same file
        var extractedNumbers = new List<PhoneNumber>();

        try
        {
            // Extract every line/sequence that contains at least one digit
            // No validation - store whatever is received
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var phoneNumber = line.Trim();
                
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(phoneNumber))
                    continue;

                // Check if line contains any digit - if yes, store it (no other validation)
                if (phoneNumber.Any(char.IsDigit))
                {
                    var normalized = NormalizePhoneNumber(phoneNumber);
                    
                    // Use normalized number for duplicate detection, but store original as-is
                    if (phoneNumbers.Add(normalized))
                    {
                        extractedNumbers.Add(new PhoneNumber
                        {
                            Number = phoneNumber, // Store original format as received - no validation
                            NormalizedNumber = normalized, // Normalized for duplicate detection only
                            SourceFile = sourceFile,
                            FirstSeenAt = DateTime.UtcNow,
                            LastSeenAt = DateTime.UtcNow,
                            OccurrenceCount = 1,
                            SourceFiles = new List<string> { sourceFile }
                        });
                    }
                }
            }

            _logger.LogInformation(
                "Extracted {Count} unique numbers from {FileName} (no validation applied)",
                extractedNumbers.Count,
                sourceFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting phone numbers from {FileName}", sourceFile);
        }

        return extractedNumbers;
    }

    public string NormalizePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return string.Empty;

        // Simple normalization: extract all digits only (for duplicate detection)
        // No validation, no length requirements, no format restrictions
        var normalized = Regex.Replace(phoneNumber, @"[^\d]", "");
        
        return normalized;
    }
}
