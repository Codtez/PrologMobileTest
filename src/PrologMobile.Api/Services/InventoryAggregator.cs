
using PrologMobile.Api.Models;
using System.Text.Json;

namespace PrologMobile.Api.Services;

public sealed class InventoryAggregator
{
    public IReadOnlyList<CategorySummary> Aggregate(
        IEnumerable<InventoryRecord> records)
    {
        // Ensure the input collection is not null.
        ArgumentNullException.ThrowIfNull(records);

        // Initialize a dictionary to store records by normalized category.
        var groups = new Dictionary<string, List<InventoryRecord>>();

        // Process each inventory record into its trimmed category.
        foreach (var record in records)
        {
            // Skip records that have no available inventory.
            if (record.Quantity <= 0)
            {
                continue;
            }

            // Normalize the category by removing surrounding whitespace
            // and converting it to lowercase.
            var category = record.Category.Trim().ToLowerInvariant();

            // Create a new group if this category does not already exist.
            if (!groups.ContainsKey(category))
            {
                groups.Add(category, new List<InventoryRecord>());
            }

            // Add the current record to its corresponding category group.
            groups[category].Add(record);
        }

        // DEBUG: Print the completed category dictionary.
        // Uncomment to inspect normalized groups and their associated records.
        // Console.WriteLine(JsonSerializer.Serialize(groups,
        //     new JsonSerializerOptions { WriteIndented = true }));

        /////////////////////////////////////////////////////////////////////////////
        /// Aggregate records to categories (CATEGORY: TOTAL VALUE, AVAILABLE RECORD COUNT; Latest Record; Original Records)
        
        var summaries = new List<CategorySummary>();

        // Process each category group individually.
        foreach (var group in groups)
        {
            // Pass the category name and its associated records.
            var summary = CreateCategorySummary(group.Key, group.Value);

            // Add the completed summary to our list.
            summaries.Add(summary);
        }

        // Sort the completed summaries alphabetically by category.
        summaries = summaries
            .OrderBy(summary => summary.Category, StringComparer.Ordinal)
            .ToList();

        // Return the completed collection of category summaries.
        return summaries;
    }

    private static CategorySummary CreateCategorySummary(
        string category,
        List<InventoryRecord> records)
    {
        // Calculate the combined monetary value of available inventory.
        var totalInventoryValue = records.Sum(
            record => record.Price * record.Quantity
        );

        // Count the number of available inventory records.
        var availableRecordCount = records.Count;

        // Find the most recently updated record.
        // If timestamps match, select the record with the lowest ID.
        var latestRecord = records
            .OrderByDescending(record => record.LastUpdated)
            .ThenBy(record => record.Id)
            .First();

        // Preserve the original records in a consistent ID order.
        var items = records
            .OrderBy(record => record.Id)
            .ToList();

        // Construct and return the completed category summary.
        return new CategorySummary(
            Category: category,
            TotalInventoryValue: totalInventoryValue,
            AvailableRecordCount: availableRecordCount,
            LatestRecord: latestRecord,
            Items: items
        );
    }
}
