using PrologMobile.Api.Models;

namespace PrologMobile.Api.Pipeline;

public sealed class InventoryDeduplicator
{
    private readonly ILogger<InventoryDeduplicator> _logger;

    public InventoryDeduplicator(
        ILogger<InventoryDeduplicator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<InventoryRecord> Deduplicate(
        IReadOnlyList<InventoryPage> pages)
    {
        // PHASE 1: Initialize the unique record dictionary.
        var uniqueRecords = new Dictionary<int, InventoryRecord>();

        var duplicateCount = 0;
        var rawRecordCount = 0;

        // PHASE 2: Process every record across all retrieved pages.
        foreach (var page in pages)
        {
            foreach (var record in page.Records)
            {
                rawRecordCount++;

                // Check whether this record ID already exists.
                if (uniqueRecords.TryGetValue(
                    record.Id, out var existingRecord))
                {
                    // Verify that the duplicate contains identical data.
                    if (existingRecord != record)
                    {
                        _logger.LogError(
                            "[DEDUPLICATION] Conflicting duplicate record {RecordId} detected on page {Page}.",
                            record.Id, page.Page);
                        throw new InvalidDataException(
                            $"Conflicting duplicate record {record.Id} " +
                            $"detected on page {page.Page}."
                        );
                    }

                    // Track and log the verified identical duplicate.
                    duplicateCount++;

                    _logger.LogWarning(
                        "[DEDUPLICATION] Identical duplicate record {RecordId} " +
                        "detected on page {Page}. Ignoring extra copy.",
                        record.Id,
                        page.Page
                    );

                    continue;
                }

                // Store the record if its ID has not been encountered.
                uniqueRecords.Add(record.Id, record);
            }
        }

        // PHASE 3: Convert the dictionary into an ordered collection.
        var records = uniqueRecords.Values
            .OrderBy(record => record.Id)
            .ToList();

        // PHASE 4: Log the completed deduplication results.
        _logger.LogInformation(
            "[DEDUPLICATION] Deduplication completed. Raw records: {RawRecordCount}. " +
            "Identical duplicates: {DuplicateCount}. " +
            "Unique records: {UniqueRecordCount}.",
            rawRecordCount,
            duplicateCount,
            records.Count
        );

        // Return the verified collection of unique records.
        return records;
    }
}