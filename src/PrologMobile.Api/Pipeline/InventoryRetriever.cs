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

        using var scope = _logger.BeginScope("Page:{Page}", page);
        var operation = "HTTP request";
        try
        {
            _logger.LogInformation("[HTTP] Beginning retrieval of page {Page}.", page);
            using var response = await _retryPolicy.GetPageAsync(page, cancellationToken);
            operation = "JSON parsing";
            _logger.LogInformation("[PARSER] Beginning JSON parsing for page {Page}.", page);
            var inventoryPage = await _parser.ParseAsync(response, page, cancellationToken);
            _logger.LogInformation("[PARSER] Page {Page} successfully deserialized.", page);
            operation = "page metadata validation";
            _logger.LogInformation("[VALIDATION] Beginning metadata validation for page {Page}.", page);
            _validator.ValidatePage(inventoryPage, page);

            operation = "record validation";
            _logger.LogInformation(
                "[VALIDATION] Beginning validation of {RecordCount} records on page {Page}.",
                inventoryPage.Records.Count, page);
            foreach (var record in inventoryPage.Records)
            {
                _validator.ValidateRecord(record, page);
            }

            _logger.LogInformation(
                "[VALIDATION] Page {Page} validated. Reported pages: {TotalPages}. Records: " +
                "{RecordCount}. Expected dataset total: {TotalRecords}",
                inventoryPage.Page, inventoryPage.TotalPages,
                inventoryPage.Records.Count, inventoryPage.TotalRecords);

            return inventoryPage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "[PIPELINE] Page {Page} failed during {Operation} with {ExceptionType}.",
                page, operation, exception.GetType().FullName);
            throw;
        }
    }

    // Retrieve all remaining inventory pages and preserve
    // the original page responses for subsequent processing.
    public async Task<List<InventoryPage>> RetrieveRemainingPagesAsync(
        InventoryPage firstPage,
        int expectedTotalPages,
        CancellationToken cancellationToken)
    {
        // Initialize a collection containing the first page.
        var pages = new List<InventoryPage>
        {
            firstPage
        };

        // Retrieve each remaining page sequentially.
        for (int page = 2; page <= expectedTotalPages; page++)
        {
            // Stop retrieval if cancellation has been requested.
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "[PHASE 3] Retrieving page {Page} of {ExpectedTotalPages}.",
                page, expectedTotalPages);
            // Retrieve and validate the next inventory page.
            var inventoryPage = await GetPageAsync(
                page,
                cancellationToken
            );

            // Preserve the complete page response.
            pages.Add(inventoryPage);
            _logger.LogInformation(
                "[PHASE 3] Retrieved page {Page} of {ExpectedTotalPages} with {RecordCount} records.",
                page, expectedTotalPages, inventoryPage.Records.Count);
        }

        // Return all retrieved inventory pages.
        return pages;
    }
}
