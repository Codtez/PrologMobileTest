using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;
using PrologMobile.Api.Models;

namespace PrologMobile.Api.Pipeline;

public sealed class InventoryPipeline
{
    private readonly InventoryRetriever _retriever;
    private readonly InventoryValidator _validator;
    private readonly InventoryDeduplicator _deduplicator;
    private readonly InventoryAggregator _aggregator;
    private readonly InventoryApiOptions _options;

    public InventoryPipeline(
        InventoryRetriever retriever,
        InventoryValidator validator,
        InventoryDeduplicator deduplicator,
        InventoryAggregator aggregator,
        IOptions<InventoryApiOptions> options)
    {
        _retriever = retriever;
        _validator = validator;
        _deduplicator = deduplicator;
        _aggregator = aggregator;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<CategorySummary>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        // Establish the overall pipeline execution deadline.
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );

        timeoutSource.CancelAfter(
            TimeSpan.FromSeconds(_options.OverallTimeoutSeconds)
        );

        var pipelineToken = timeoutSource.Token;

        try
        {
            // PHASE 1: Retrieve the initial inventory page.
            var firstPage = await _retriever.GetPageAsync(
                1,
                pipelineToken
            );

            // PHASE 2: Establish expected dataset dimensions.
            var expectedTotalRecords = firstPage.TotalRecords;

            var expectedTotalPages = _validator.CalculateTotalPages(
                expectedTotalRecords
            );

            // PHASE 3: Retrieve all remaining inventory pages.
            var pages = await _retriever.RetrieveRemainingPagesAsync(
                firstPage,
                expectedTotalPages,
                pipelineToken
            );

            pipelineToken.ThrowIfCancellationRequested();

            // PHASE 4: Verify cross-page metadata consistency.
            _validator.ValidateDatasetMetadata(
                pages,
                expectedTotalRecords
            );

            // PHASE 5: Verify and remove identical duplicates.
            var uniqueRecords = _deduplicator.Deduplicate(pages);

            // PHASE 6: Verify dataset completeness.
            _validator.ValidateCompleteness(
                uniqueRecords,
                expectedTotalRecords
            );

            pipelineToken.ThrowIfCancellationRequested();

            // PHASE 7: Aggregate the verified inventory records.
            var summaries = _aggregator.Aggregate(uniqueRecords);

            // PHASE 8: Return the final category summaries.
            pipelineToken.ThrowIfCancellationRequested();

            return summaries;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                  && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Inventory pipeline exceeded the overall timeout " +
                $"of {_options.OverallTimeoutSeconds} seconds.",
                exception
            );
        }
    }
}