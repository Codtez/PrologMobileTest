using PrologMobile.Api.Models;
using PrologMobile.Api.Services;

namespace PrologMobile.Tests.Services;

public sealed class InventoryAggregatorTests
{
    [Fact]
    public void Aggregate_ProducesCorrectCategorySummary()
    {
        // ARRANGE: Create the aggregator and sample inventory records.
        var aggregator = new InventoryAggregator();

        var records = new List<InventoryRecord>
        {
            new InventoryRecord(
                Id: 1001,
                Name: "Keyboard",
                Category: "Electronics",
                Price: 50.00m,
                Quantity: 2,
                LastUpdated: DateTimeOffset.Parse("2026-09-20T00:00:00Z")
            ),
            new InventoryRecord(
                Id: 1002,
                Name: "Mouse",
                Category: " ElectRonics ",
                Price: 25.00m,
                Quantity: 4,
                LastUpdated: DateTimeOffset.Parse("2026-09-22T00:00:00Z")
            ),
            new InventoryRecord(
                Id: 1003,
                Name: "Monitor",
                Category: "Electronics",
                Price: 200.00m,
                Quantity: 0,
                LastUpdated: DateTimeOffset.Parse("2026-09-23T00:00:00Z")
            )
        };

        // ACT: Execute our aggregation logic.
        var summaries = aggregator.Aggregate(records);

        // ASSERT: Verify that exactly one category summary exists.
        var summary = Assert.Single(summaries);

        // Verify the normalized category name.
        Assert.Equal("electronics", summary.Category);

        // Verify the calculated inventory value.
        Assert.Equal(200.00m, summary.TotalInventoryValue);

        // Verify the number of available inventory records.
        Assert.Equal(2, summary.AvailableRecordCount);

        // Verify that the latest available record is selected.
        Assert.Equal(records[1], summary.LatestRecord);

        // Verify that only available records are preserved,
        // including their original category formatting.
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal(records[0], summary.Items[0]);
        Assert.Equal(records[1], summary.Items[1]);

        // Verify that the latest available record is selected.
        Assert.Equal(records[1], summary.LatestRecord);

        // Verify that only available records are preserved,
        // including their original category formatting.
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal(records[0], summary.Items[0]);
        Assert.Equal(records[1], summary.Items[1]);
    }


        [Fact]
    public void Aggregate_SeparatesDifferentCategories()
    {
        // ARRANGE: Create records belonging to different categories.
        var aggregator = new InventoryAggregator();

        var records = new List<InventoryRecord>
        {
            new InventoryRecord(
                Id: 1001,
                Name: "Board Game",
                Category: "Toys",
                Price: 30.00m,
                Quantity: 3,
                LastUpdated: DateTimeOffset.UtcNow
            ),
            new InventoryRecord(
                Id: 1002,
                Name: "Keyboard",
                Category: "Electronics",
                Price: 50.00m,
                Quantity: 2,
                LastUpdated: DateTimeOffset.UtcNow
            )
        };

        // ACT: Generate category summaries.
        var summaries = aggregator.Aggregate(records);

        // ASSERT: Verify separate, alphabetically ordered categories.
        Assert.Equal(2, summaries.Count);

        Assert.Equal("electronics", summaries[0].Category);
        Assert.Equal(100.00m, summaries[0].TotalInventoryValue);

        Assert.Equal("toys", summaries[1].Category);
        Assert.Equal(90.00m, summaries[1].TotalInventoryValue);
    }

        [Fact]
    public void Aggregate_ReturnsEmptyListWhenNoRecordsExist()
    {
        // ARRANGE: Create an empty inventory collection.
        var aggregator = new InventoryAggregator();
        var records = new List<InventoryRecord>();

        // ACT: Execute the aggregation logic.
        var summaries = aggregator.Aggregate(records);

        // ASSERT: Verify that no summaries are generated.
        Assert.Empty(summaries);
    }
}


/*
 * INVENTORY AGGREGATION TESTS
 *
 * 1. Aggregate_ProducesCorrectCategorySummary
 *    A failure indicates that one or more core transformation
 *    operations are incorrect:
 *    - Category normalization (casing and whitespace).
 *    - Total inventory value calculation (price * quantity).
 *    - Available record count.
 *    - Exclusion of zero-quantity records.
 *    - Selection of the most recently updated available record.
 *    - Preservation of original inventory records.
 *
 * 2. Aggregate_SeparatesDifferentCategories
 *    A failure indicates that records are being grouped incorrectly,
 *    inventory values are being calculated incorrectly across
 *    categories, or category summaries are not sorted alphabetically.
 *
 * 3. Aggregate_ReturnsEmptyListWhenNoRecordsExist
 *    A failure indicates that the aggregator cannot correctly
 *    handle an empty input collection without throwing an exception
 *    or generating unexpected category summaries.
 */