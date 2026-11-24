namespace PrologMobile.Api.Configuration;

public sealed class InventoryApiOptions
{
    // Configuration section name in appsettings.json.
    public const string SectionName = "InventoryApi";

    // Base URL of the external inventory API.
    public string BaseUrl { get; set; } = string.Empty;

    // Authentication key supplied through secure configuration.
    public string ApiKey { get; set; } = string.Empty;

    // Maximum number of records requested per page.
    public int PageSize { get; set; } = 25;

    // Maximum duration of an individual HTTP request.
    public int RequestTimeoutSeconds { get; set; } = 15;

    // Maximum attempts per request, including the initial attempt.
    public int MaxAttempts { get; set; } = 3;

    // Minimum interval between outgoing API requests.
    public int RequestIntervalMilliseconds { get; set; } = 2200;

    // Fallback delay when rate limited without a Retry-After header.
    public int RateLimitDelaySeconds { get; set; } = 11;

    // Maximum duration of the complete inventory retrieval operation.
    public int OverallTimeoutSeconds { get; set; } = 90;
}