using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;
using PrologMobile.Api.Models;
using System.Text.Json;

namespace PrologMobile.Api.Clients;

public sealed class InventoryApiClient
{
    // HTTP client used to communicate with the external API.
    private readonly HttpClient _httpClient;

    // Validated inventory API configuration.
    private readonly InventoryApiOptions _options;

    // Structured logger used for diagnostics and error reporting.
    private readonly ILogger<InventoryApiClient> _logger;

    // Shared rate limiter for outgoing inventory API requests.
    private readonly InventoryRateLimiter _rateLimiter;

    // Configure JSON deserialization for external API responses.
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            // Reject responses that omit required constructor parameters.
            RespectRequiredConstructorParameters = true,

            // Enforce non-nullable property and constructor declarations.
            RespectNullableAnnotations = true
        };

    // Retrieve and deserialize a single page of inventory records.
    public async Task<InventoryPage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        // Verify that the requested page number is valid.
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        // Construct the request URL using our configured page size.
        var requestUrl =
            $"/api/records?page={page}&pageSize={_options.PageSize}";

        // Wait until the shared rate limiter permits another request.
        await _rateLimiter.WaitAsync(cancellationToken);

        // Log the outgoing request without exposing authentication credentials.
        _logger.LogInformation(
            "Requesting inventory page {Page} with page size {PageSize}",
            page,
            _options.PageSize
        );

        // Send the asynchronous HTTP GET request.
        using var response = await _httpClient.GetAsync(
            requestUrl,
            cancellationToken
        );

        // Log the HTTP status returned by the external API.
        _logger.LogInformation(
            "Inventory page {Page} returned HTTP status {StatusCode}",
            page,
            (int)response.StatusCode
        );

        // Reject unsuccessful HTTP responses.
        response.EnsureSuccessStatusCode();

        // Read the JSON response body.
        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken
        );

        // Convert the JSON response into our InventoryPage model.
        var inventoryPage = JsonSerializer.Deserialize<InventoryPage>(
            responseBody,
            JsonOptions
        );

        // Reject a response that deserializes to null.
        if (inventoryPage is null)
        {
            throw new JsonException(
                $"Inventory page {page} returned a null response."
            );
        }

        // Log the returned pagination metadata and actual record count.
        _logger.LogInformation(
            "Received page {Page} of {TotalPages} containing " +
            "{RecordCount} records. Expected dataset total: {TotalRecords}",
            inventoryPage.Page,
            inventoryPage.TotalPages,
            inventoryPage.Records.Count,
            inventoryPage.TotalRecords
        );

        // Return the deserialized inventory page.
        return inventoryPage;
    }

    // Receive required dependencies through constructor injection.
    public InventoryApiClient(
        HttpClient httpClient,
        IOptions<InventoryApiOptions> options,
        ILogger<InventoryApiClient> logger,
        InventoryRateLimiter rateLimiter)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }
}