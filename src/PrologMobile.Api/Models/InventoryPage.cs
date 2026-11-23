namespace PrologMobile.Api.Models;

public sealed record InventoryPage(
    IReadOnlyList<InventoryRecord> Records,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalRecords
);