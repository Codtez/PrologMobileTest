using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;
using PrologMobile.Api.Models;

namespace PrologMobile.Api.Pipeline;

public sealed class InventoryValidator
{
    private readonly InventoryApiOptions _options;
    private readonly ILogger<InventoryValidator> _logger;

    public InventoryValidator(
        IOptions<InventoryApiOptions> options,
        ILogger<InventoryValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void ValidatePage(InventoryPage inventoryPage, int requestedPage)
    {
        if (inventoryPage.Page != requestedPage)
        {
            throw new InvalidDataException(
                $"Requested page {requestedPage}, but the API " +
                $"returned page {inventoryPage.Page}.");
        }

        if (inventoryPage.PageSize != _options.PageSize)
        {
            throw new InvalidDataException(
                $"Requested page size {_options.PageSize}, but the API " +
                $"returned page size {inventoryPage.PageSize}.");
        }

        if (inventoryPage.TotalPages < 0 || inventoryPage.TotalRecords < 0)
        {
            throw new InvalidDataException(
                "The API returned invalid pagination totals.");
        }

        var expectedTotalPages = (int)Math.Ceiling(
            (decimal)inventoryPage.TotalRecords / _options.PageSize);

        if (inventoryPage.TotalPages != expectedTotalPages)
        {
            _logger.LogWarning(
                "Inconsistent pagination metadata on page {Page}. " +
                "Calculated {ExpectedTotalPages} pages, but the API " +
                "reported {ReportedTotalPages}",
                requestedPage, expectedTotalPages, inventoryPage.TotalPages);
        }

        if (inventoryPage.TotalRecords > 0 && requestedPage > expectedTotalPages)
        {
            throw new InvalidDataException(
                $"Requested page {requestedPage} exceeds the " +
                $"reported total of {inventoryPage.TotalPages} pages.");
        }

        if (inventoryPage.Records is null)
        {
            throw new InvalidDataException(
                $"Page {requestedPage} contains a null record collection.");
        }
    }

    public void ValidateRecord(InventoryRecord? record, int page)
    {
        if (record is null)
        {
            throw new InvalidDataException(
                $"Page {page} contains a null inventory record.");
        }

        if (string.IsNullOrWhiteSpace(record.Name))
        {
            throw new InvalidDataException(
                $"Record {record.Id} on page {page} has an invalid name.");
        }

        if (string.IsNullOrWhiteSpace(record.Category))
        {
            throw new InvalidDataException(
                $"Record {record.Id} on page {page} has an invalid category.");
        }

        if (record.Price < 0)
        {
            throw new InvalidDataException(
                $"Record {record.Id} on page {page} has a negative price.");
        }

        if (record.Quantity < 0)
        {
            throw new InvalidDataException(
                $"Record {record.Id} on page {page} has a negative quantity.");
        }
    }
}
