using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrologMobile.Api.Clients;
using PrologMobile.Api.Configuration;
using PrologMobile.Api.Models;
using PrologMobile.Api.Pipeline;
using PrologMobile.Api.Resilience;

namespace PrologMobile.Tests.Pipeline;

public sealed class InventoryRetrieverTests
{
    private const string ValidRecord =
        """{"id":1,"name":" Item ","category":" Category ","price":1.5,"quantity":2,"lastUpdated":"2026-01-01T00:00:00Z"}""";

    [Fact]
    public async Task GetPageAsync_AllowsOverlappingRecordsAndPreservesOriginalValues()
    {
        var content = new TrackingContent(PageJson($"{ValidRecord},{ValidRecord}", totalPages: 9));
        var (retriever, handler) = CreateRetriever(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var page = await retriever.GetPageAsync(1);

        Assert.Equal(2, page.Records.Count);
        Assert.Equal(" Item ", page.Records[0].Name);
        Assert.Equal(" Category ", page.Records[0].Category);
        Assert.Equal("/api/records?page=1&pageSize=1", handler.RequestUri!.PathAndQuery);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"records":null,"page":1,"pageSize":1,"totalPages":1,"totalRecords":1}""")]
    [InlineData("""{"records":[{"id":1,"name":null,"category":"x","price":1,"quantity":1,"lastUpdated":"2026-01-01T00:00:00Z"}],"page":1,"pageSize":1,"totalPages":1,"totalRecords":1}""")]
    public async Task GetPageAsync_RejectsInvalidJsonContracts(string body)
    {
        var content = new TrackingContent(body);
        var (retriever, _) = CreateRetriever(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        await Assert.ThrowsAsync<JsonException>(() => retriever.GetPageAsync(1));
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"id":1,"name":" ","category":"x","price":1,"quantity":1,"lastUpdated":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":1,"name":"x","category":" ","price":1,"quantity":1,"lastUpdated":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":1,"name":"x","category":"x","price":-1,"quantity":1,"lastUpdated":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":1,"name":"x","category":"x","price":1,"quantity":-1,"lastUpdated":"2026-01-01T00:00:00Z"}""")]
    public async Task GetPageAsync_RejectsInvalidRecords(string record)
    {
        var (retriever, _) = CreateRetriever(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PageJson(record))
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => retriever.GetPageAsync(1));
    }

    [Theory]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 2, 1, 1)]
    [InlineData(1, 1, -1, 1)]
    [InlineData(1, 1, 1, -1)]
    public async Task GetPageAsync_RejectsInvalidMetadata(
        int page, int pageSize, int totalPages, int totalRecords)
    {
        var body = $$"""{"records":[],"page":{{page}},"pageSize":{{pageSize}},"totalPages":{{totalPages}},"totalRecords":{{totalRecords}}}""";
        var (retriever, _) = CreateRetriever(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        await Assert.ThrowsAsync<InvalidDataException>(() => retriever.GetPageAsync(1));
    }

    [Fact]
    public async Task GetPageAsync_UsesCalculatedPageCountForBounds()
    {
        var body = """{"records":[],"page":2,"pageSize":1,"totalPages":9,"totalRecords":1}""";
        var (retriever, _) = CreateRetriever(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        await Assert.ThrowsAsync<InvalidDataException>(() => retriever.GetPageAsync(2));
    }

    [Fact]
    public void ValidatePage_RejectsNullCollection()
    {
        var validator = new InventoryValidator(
            Options.Create(new InventoryApiOptions { PageSize = 1 }),
            NullLogger<InventoryValidator>.Instance);
        var page = new InventoryPage(null!, 1, 1, 0, 0);

        Assert.Throws<InvalidDataException>(() => validator.ValidatePage(page, 1));
    }

    [Fact]
    public void ValidatePage_LogsWarningForIncorrectReportedPageCount()
    {
        var logger = new RecordingLogger<InventoryValidator>();
        var validator = new InventoryValidator(
            Options.Create(new InventoryApiOptions { PageSize = 1 }), logger);
        var page = new InventoryPage([], 1, 1, 9, 1);

        validator.ValidatePage(page, 1);

        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public async Task GetPageAsync_RejectsInvalidPageBeforeRequest()
    {
        var (retriever, handler) = CreateRetriever(
            _ => throw new InvalidOperationException("Unexpected request"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => retriever.GetPageAsync(0));
        Assert.Equal(0, handler.Attempts);
    }

    private static string PageJson(string records, int totalPages = 1) =>
        $$"""{"records":[{{records}}],"page":1,"pageSize":1,"totalPages":{{totalPages}},"totalRecords":1}""";

    private static (InventoryRetriever Retriever, StubHandler Handler) CreateRetriever(
        Func<int, HttpResponseMessage> respond)
    {
        var options = Options.Create(new InventoryApiOptions
        {
            PageSize = 1,
            RequestIntervalMilliseconds = 1
        });
        var handler = new StubHandler((attempt, _) => Task.FromResult(respond(attempt)));
        var client = new InventoryApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }, options);
        var policy = new InventoryRetryPolicy(
            client, new InventoryRateLimiter(options), options, NullLogger<InventoryRetryPolicy>.Instance);
        return (new InventoryRetriever(
            policy, new InventoryPageParser(),
            new InventoryValidator(options, NullLogger<InventoryValidator>.Instance),
            NullLogger<InventoryRetriever>.Instance), handler);
    }
}

internal sealed class StubHandler(
    Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Attempts { get; private set; }
    public Uri? RequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        return respond(++Attempts, cancellationToken);
    }
}

internal sealed class TrackingContent(string body) : StringContent(body)
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Levels.Add(logLevel);
    }
}
