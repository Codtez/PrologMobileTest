using System.Net;
using Microsoft.Extensions.Options;
using PrologMobile.Api.Clients;
using PrologMobile.Api.Configuration;

namespace PrologMobile.Api.Resilience;

public sealed class InventoryRetryPolicy
{
    private readonly InventoryApiClient _client;
    private readonly InventoryRateLimiter _rateLimiter;
    private readonly InventoryApiOptions _options;
    private readonly ILogger<InventoryRetryPolicy> _logger;

    public InventoryRetryPolicy(
        InventoryApiClient client,
        InventoryRateLimiter rateLimiter,
        IOptions<InventoryApiOptions> options,
        ILogger<InventoryRetryPolicy> logger)
    {
        _client = client;
        _rateLimiter = rateLimiter;
        _options = options.Value;
        _logger = logger;
    }

    // The caller owns and must dispose a successful response.
    public async Task<HttpResponseMessage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _rateLimiter.WaitAsync(cancellationToken);

            _logger.LogInformation(
                "Requesting inventory page {Page} with page size " +
                "{PageSize}. Attempt {Attempt} of {MaxAttempts}",
                page, _options.PageSize, attempt, _options.MaxAttempts);

            try
            {
                var response = await _client.GetPageAsync(page, cancellationToken);
                _logger.LogInformation(
                    "Inventory page {Page} returned HTTP status {StatusCode}",
                    page, (int)response.StatusCode);

                if (IsRetryableStatusCode(response.StatusCode))
                {
                    var delay = GetRetryDelay(response);
                    _rateLimiter.ApplyCooldown(delay);

                    if (attempt < _options.MaxAttempts)
                    {
                        _logger.LogWarning(
                            "Retrying inventory page {Page} after HTTP " +
                            "{StatusCode}. Attempt {Attempt} failed. " +
                            "Additional cooldown: {DelaySeconds} seconds",
                            page, (int)response.StatusCode, attempt, delay.TotalSeconds);
                        response.Dispose();
                        continue;
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        response.EnsureSuccessStatusCode();
                    }
                }

                return response;
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode is null && attempt < _options.MaxAttempts)
            {
                _logger.LogWarning(
                    exception,
                    "Network failure retrieving page {Page}. " +
                    "Attempt {Attempt} failed. Retrying",
                    page, attempt);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested &&
                      attempt < _options.MaxAttempts)
            {
                _logger.LogWarning(
                    exception,
                    "Request timed out retrieving page {Page}. " +
                    "Attempt {Attempt} failed. Retrying",
                    page, attempt);
            }
        }

        throw new InvalidOperationException(
            $"Inventory page {page} could not be retrieved.");
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value;
            }

            if (retryAfter.Date.HasValue)
            {
                var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests
            ? TimeSpan.FromSeconds(_options.RateLimitDelaySeconds)
            : TimeSpan.Zero;
    }
}
