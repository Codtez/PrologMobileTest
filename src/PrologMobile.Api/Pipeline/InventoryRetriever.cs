using PrologMobile.Api.Models;
using PrologMobile.Api.Resilience;

namespace PrologMobile.Api.Pipeline;

public sealed class InventoryRetriever
{
    private readonly InventoryRetryPolicy _retryPolicy;
    private readonly InventoryPageParser _parser;
    private readonly InventoryValidator _validator;
    private readonly ILogger<InventoryRetriever> _logger;

    public InventoryRetriever(
        InventoryRetryPolicy retryPolicy,
        InventoryPageParser parser,
        InventoryValidator validator,
        ILogger<InventoryRetriever> logger)
    {
        _retryPolicy = retryPolicy;
        _parser = parser;
        _validator = validator;
        _logger = logger;
    }

    public async Task<InventoryPage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        using var response = await _retryPolicy.GetPageAsync(page, cancellationToken);
        var inventoryPage = await _parser.ParseAsync(response, page, cancellationToken);
        _validator.ValidatePage(inventoryPage, page);

        foreach (var record in inventoryPage.Records)
        {
            _validator.ValidateRecord(record, page);
        }

        _logger.LogInformation(
            "Received page {Page} of {TotalPages} containing " +
            "{RecordCount} records. Expected dataset total: {TotalRecords}",
            inventoryPage.Page, inventoryPage.TotalPages,
            inventoryPage.Records.Count, inventoryPage.TotalRecords);

        return inventoryPage;
    }
}
