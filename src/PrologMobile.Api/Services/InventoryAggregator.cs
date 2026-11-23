
using PrologMobile.Api.Models;

namespace PrologMobile.Api.Services;

public sealed class InventoryAggregator
{
    public IReadOnlyList<CategorySummary> Aggregate(
        IEnumerable<InventoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var groups = records
            .Where(record => record.Quantity > 0)
            .GroupBy(
                record => record.Category.Trim().ToLowerInvariant()
            );

        return [];
    }
}
