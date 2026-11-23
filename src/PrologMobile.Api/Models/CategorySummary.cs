
namespace PrologMobile.Api.Models;

public sealed record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    InventoryRecord LatestRecord,
    IReadOnlyList<InventoryRecord> Items
);
