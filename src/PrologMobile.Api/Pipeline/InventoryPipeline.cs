using System.Diagnostics;
using System.Globalization;
using System.Numerics;
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
    private readonly ILogger<InventoryPipeline> _logger;

    public InventoryPipeline(
        InventoryRetriever retriever,
        InventoryValidator validator,
        InventoryDeduplicator deduplicator,
        InventoryAggregator aggregator,
        IOptions<InventoryApiOptions> options,
        ILogger<InventoryPipeline> logger)
    {
        _retriever = retriever;
        _validator = validator;
        _deduplicator = deduplicator;
        _aggregator = aggregator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CategorySummary>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope("ExecutionId:{ExecutionId}", Guid.NewGuid());
        var pipelineTimer = Stopwatch.StartNew();
        var phaseTimer = new Stopwatch();
        var phaseNumber = 0;
        var phaseName = "Pipeline setup";
        _logger.LogInformation("[PIPELINE] Inventory pipeline execution started.");

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
            phaseNumber = 1;
            phaseName = "Retrieve Initial Inventory Page";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 1] START - Retrieving page {Page} with page size {PageSize}.",
                1, _options.PageSize);
            var firstPage = await _retriever.GetPageAsync(
                1,
                pipelineToken
            );
            _logger.LogInformation(
                "[PHASE 1] COMPLETE - Page {Page} returned {RecordCount} records; " +
                "page size {PageSize}, reported pages {TotalPages}, total records {TotalRecords}. " +
                "Elapsed: {ElapsedMilliseconds} ms.",
                firstPage.Page, firstPage.Records.Count, firstPage.PageSize,
                firstPage.TotalPages, firstPage.TotalRecords, phaseTimer.ElapsedMilliseconds);

            // PHASE 2: Establish expected dataset dimensions.
            phaseNumber = 2;
            phaseName = "Establish Expected Dataset Dimensions";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 2] START - Establishing dimensions from page {Page}.", firstPage.Page);
            var expectedTotalRecords = firstPage.TotalRecords;

            var expectedTotalPages = _validator.CalculateTotalPages(
                expectedTotalRecords
            );
            _logger.LogInformation(
                "[PHASE 2] COMPLETE - Expected records: {ExpectedTotalRecords}; " +
                "reported pages: {ReportedTotalPages}; calculated pages: {CalculatedTotalPages}; " +
                "page count discrepancy: {PageCountDiscrepancy}. Elapsed: {ElapsedMilliseconds} ms.",
                expectedTotalRecords, firstPage.TotalPages, expectedTotalPages,
                firstPage.TotalPages - expectedTotalPages, phaseTimer.ElapsedMilliseconds);

            // PHASE 3: Retrieve all remaining inventory pages.
            phaseNumber = 3;
            phaseName = "Retrieve Remaining Inventory Pages";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 3] START - Retrieving {RemainingPageCount} remaining pages.",
                Math.Max(0, expectedTotalPages - 1));
            var pages = await _retriever.RetrieveRemainingPagesAsync(
                firstPage,
                expectedTotalPages,
                pipelineToken
            );

            pipelineToken.ThrowIfCancellationRequested();

            long rawRecordCount = 0;
            foreach (var page in pages)
            {
                rawRecordCount += page.Records.Count;
            }
            _logger.LogInformation(
                "[PHASE 3] COMPLETE - Retrieved {PageCount} pages containing {RawRecordCount} " +
                "raw records. Elapsed: {ElapsedMilliseconds} ms.",
                pages.Count, rawRecordCount, phaseTimer.ElapsedMilliseconds);

            // PHASE 4: Verify cross-page metadata consistency.
            phaseNumber = 4;
            phaseName = "Verify Cross-Page Metadata Consistency";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 4] START - Validating {PageCount} pages for {ExpectedTotalRecords} records.",
                pages.Count, expectedTotalRecords);
            _validator.ValidateDatasetMetadata(
                pages,
                expectedTotalRecords
            );
            _logger.LogInformation(
                "[PHASE 4] COMPLETE - Metadata consistency verified across {PageCount} pages. " +
                "Elapsed: {ElapsedMilliseconds} ms.",
                pages.Count, phaseTimer.ElapsedMilliseconds);

            // PHASE 5: Verify and remove identical duplicates.
            phaseNumber = 5;
            phaseName = "Verify and Remove Identical Duplicates";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 5] START - Verifying duplicates in {RawRecordCount} raw records.",
                rawRecordCount);
            var uniqueRecords = _deduplicator.Deduplicate(pages);
            _logger.LogInformation(
                "[PHASE 5] COMPLETE - Removed {DuplicateCount} identical duplicates; " +
                "{UniqueRecordCount} unique records remain. Elapsed: {ElapsedMilliseconds} ms.",
                rawRecordCount - uniqueRecords.Count, uniqueRecords.Count, phaseTimer.ElapsedMilliseconds);

            // PHASE 6: Verify dataset completeness.
            phaseNumber = 6;
            phaseName = "Verify Dataset Completeness";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 6] START - Expected {ExpectedRecordCount} unique records; actual " +
                "{ActualRecordCount}; difference (expected minus actual): {RecordDifference}.",
                expectedTotalRecords, uniqueRecords.Count, (long)expectedTotalRecords - uniqueRecords.Count);
            _validator.ValidateCompleteness(
                uniqueRecords,
                expectedTotalRecords
            );
            _logger.LogInformation(
                "[PHASE 6] COMPLETE - Dataset completeness verified. Expected: {ExpectedRecordCount}; " +
                "actual: {ActualRecordCount}; difference: {RecordDifference}. Elapsed: {ElapsedMilliseconds} ms.",
                expectedTotalRecords, uniqueRecords.Count, (long)expectedTotalRecords - uniqueRecords.Count,
                phaseTimer.ElapsedMilliseconds);

            pipelineToken.ThrowIfCancellationRequested();

            // PHASE 7: Aggregate the verified inventory records.
            phaseNumber = 7;
            phaseName = "Aggregate Verified Inventory Records";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 7] START - Aggregating {VerifiedRecordCount} verified records.",
                uniqueRecords.Count);
            var summaries = _aggregator.Aggregate(uniqueRecords);

            long availableRecords = 0;
            foreach (var summary in summaries)
            {
                availableRecords += summary.AvailableRecordCount;
                _logger.LogInformation(
                    "[AGGREGATION] Category {Category}: value {InventoryValue}, " +
                    "available records {AvailableRecordCount}, latest record ID {LatestRecordId}.",
                    summary.Category, summary.TotalInventoryValue,
                    summary.AvailableRecordCount, summary.LatestRecord.Id);
            }
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "[AGGREGATION] Excluded {ExcludedRecordCount} zero-quantity records; " +
                    "{AvailableRecordCount} available records; {CategoryCount} categories; " +
                    "overall inventory value {OverallInventoryValue}.",
                    uniqueRecords.Count - availableRecords, availableRecords, summaries.Count,
                    FormatOverallInventoryValue(summaries));
            }
            _logger.LogInformation(
                "[PHASE 7] COMPLETE - Generated {CategoryCount} category summaries. " +
                "Elapsed: {ElapsedMilliseconds} ms.",
                summaries.Count, phaseTimer.ElapsedMilliseconds);

            // PHASE 8: Return the final category summaries.
            phaseNumber = 8;
            phaseName = "Return Final Category Summaries";
            phaseTimer.Restart();
            _logger.LogInformation(
                "[PHASE 8] START - Preparing {CategoryCount} category summaries.", summaries.Count);
            pipelineToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "[PHASE 8] COMPLETE - Returning {CategoryCount} category summaries with " +
                "{AvailableRecordCount} available records. Elapsed: {ElapsedMilliseconds} ms.",
                summaries.Count, availableRecords, phaseTimer.ElapsedMilliseconds);
            _logger.LogInformation(
                "[PIPELINE] Execution completed successfully in {ElapsedMilliseconds} ms.",
                pipelineTimer.ElapsedMilliseconds);
            return summaries;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                  && timeoutSource.IsCancellationRequested)
        {
            try
            {
                throw new TimeoutException(
                    $"Inventory pipeline exceeded the overall timeout " +
                    $"of {_options.OverallTimeoutSeconds} seconds.",
                    exception
                );
            }
            catch (TimeoutException timeoutException)
            {
                _logger.LogError(
                    timeoutException,
                    "[PIPELINE] Failed during phase {PhaseNumber} ({PhaseName}): " +
                    "{ExceptionType}: {ExceptionMessage}. Elapsed: {ElapsedMilliseconds} ms.",
                    phaseNumber, phaseName, timeoutException.GetType().FullName,
                    timeoutException.Message, pipelineTimer.ElapsedMilliseconds);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "[PIPELINE] Caller cancelled during phase {PhaseNumber} ({PhaseName}) " +
                "after {ElapsedMilliseconds} ms.",
                phaseNumber, phaseName, pipelineTimer.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "[PIPELINE] Failed during phase {PhaseNumber} ({PhaseName}): " +
                "{ExceptionType}: {ExceptionMessage}. Elapsed: {ElapsedMilliseconds} ms.",
                phaseNumber, phaseName, exception.GetType().FullName,
                exception.Message, pipelineTimer.ElapsedMilliseconds);
            throw;
        }
    }

    private static string FormatOverallInventoryValue(IReadOnlyList<CategorySummary> summaries)
    {
        var total = BigInteger.Zero;
        foreach (var summary in summaries)
        {
            var bits = decimal.GetBits(summary.TotalInventoryValue);
            var value = ((BigInteger)(uint)bits[2] << 64)
                | ((BigInteger)(uint)bits[1] << 32)
                | (uint)bits[0];
            var scale = (bits[3] >> 16) & 0x7f;
            if ((bits[3] & int.MinValue) != 0)
            {
                value = -value;
            }
            total += value * BigInteger.Pow(10, 28 - scale);
        }

        var whole = BigInteger.DivRem(BigInteger.Abs(total), BigInteger.Pow(10, 28), out var fraction);
        var fractionalText = fraction.ToString("D28", CultureInfo.InvariantCulture).TrimEnd('0');
        var sign = total.Sign < 0 ? "-" : "";
        var wholeText = whole.ToString(CultureInfo.InvariantCulture);
        return fractionalText.Length == 0
            ? sign + wholeText
            : sign + wholeText + "." + fractionalText;
    }
}