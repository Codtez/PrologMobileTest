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
        var expectedTotalPages = CalculateTotalPages(
            inventoryPage.TotalRecords
        );

        if (inventoryPage.Page != requestedPage)
        {
            throw new InvalidDataException(
                $"Requested page {requestedPage} exceeds the " +
                $"calculated total of {expectedTotalPages} pages."
            );
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

    // Independently calculate the number of pages required
    // to retrieve the complete inventory dataset.
    public int CalculateTotalPages(int totalRecords)
    {
        return (int)Math.Ceiling(
            (decimal)totalRecords / _options.PageSize
        );
    }

    // Verify that pagination metadata remains consistent
    // across the complete collection of retrieved pages.
    public void ValidateDatasetMetadata(
        IReadOnlyList<InventoryPage> pages,
        int expectedTotalRecords)
    {
        // Calculate the expected number of pages.
        var expectedTotalPages = CalculateTotalPages(
            expectedTotalRecords
        );

        // Account for the initial page request when the dataset is empty.
        var expectedRetrievedPages = Math.Max(1, expectedTotalPages);

        // Verify that all required pages were retrieved.
        if (pages.Count != expectedRetrievedPages)
        {
            throw new InvalidDataException(
                $"Expected {expectedRetrievedPages} pages, " +
                $"but retrieved {pages.Count}."
            );
        }

        // Verify metadata across every retrieved page.
        foreach (var page in pages)
        {
            // Ensure the expected dataset size has not changed.
            if (page.TotalRecords != expectedTotalRecords)
            {
                throw new InvalidDataException(
                    $"Inconsistent total record count on page {page.Page}. " +
                    $"Expected {expectedTotalRecords}, " +
                    $"but received {page.TotalRecords}."
                );
            }
        }

        // Log successful cross-page metadata validation.
        _logger.LogInformation(
            "Dataset metadata validated across {PageCount} pages. " +
            "Expected dataset size: {TotalRecords} records.",
            pages.Count,
            expectedTotalRecords
        );
    }

    // Verify that the deduplicated dataset contains the
    // expected number of unique inventory records.
    public void ValidateCompleteness(
        IReadOnlyList<InventoryRecord> records,
        int expectedTotalRecords)
    {
        // Determine the actual number of unique records.
        var actualTotalRecords = records.Count;

        // Calculate the difference between expected and actual totals.
        var recordDifference = expectedTotalRecords - actualTotalRecords;

        // Reject incomplete datasets.
        if (recordDifference > 0)
        {
            throw new InvalidDataException(
                $"Incomplete inventory dataset. " +
                $"Expected {expectedTotalRecords} unique records, " +
                $"but received {actualTotalRecords}. " +
                $"Missing {recordDifference} records."
            );
        }

        // Reject datasets containing unexpected additional records.
        if (recordDifference < 0)
        {
            throw new InvalidDataException(
                $"Inventory dataset exceeds expected size. " +
                $"Expected {expectedTotalRecords} unique records, " +
                $"but received {actualTotalRecords}. " +
                $"Found {Math.Abs(recordDifference)} additional records."
            );
        }

        // Log successful dataset completeness verification.
        _logger.LogInformation(
            "Dataset completeness verified. " +
            "Expected records: {ExpectedTotalRecords}. " +
            "Actual unique records: {ActualTotalRecords}.",
            expectedTotalRecords,
            actualTotalRecords
        );
    }


}


