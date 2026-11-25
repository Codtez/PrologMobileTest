using System.Net;
using System.Diagnostics;
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
                "[HTTP] Requesting inventory page {Page} with page size " +
                "{PageSize}. Attempt {Attempt} of {MaxAttempts}",
                page, _options.PageSize, attempt, _options.MaxAttempts);

            var requestTimer = Stopwatch.StartNew();
            try
            {
                var response = await _client.GetPageAsync(page, cancellationToken);
                _logger.LogInformation(
                    "[HTTP] Page {Page}, attempt {Attempt} of {MaxAttempts}, returned HTTP " +
                    "{StatusCode} in {ElapsedMilliseconds} ms.",
                    page, attempt, _options.MaxAttempts, (int)response.StatusCode,
                    requestTimer.ElapsedMilliseconds);

                if (IsRetryableStatusCode(response.StatusCode))
                {
                    var delay = GetRetryDelay(response);
                    _rateLimiter.ApplyCooldown(delay);

                    if (attempt < _options.MaxAttempts)
                    {
                        _logger.LogWarning(
                            "[RETRY] Retrying inventory page {Page} after HTTP " +
                            "{StatusCode}. Attempt {Attempt} failed. " +
                            "Additional cooldown: {DelaySeconds} seconds",
                            page, (int)response.StatusCode, attempt, delay.TotalSeconds);
                        response.Dispose();
                        continue;
                    }

                    _logger.LogWarning(
                        "[RETRY] Exhausted {MaxAttempts} attempts for page {Page} after HTTP " +
                        "{StatusCode}. Applied cooldown: {DelaySeconds} seconds.",
                        _options.MaxAttempts, page, (int)response.StatusCode, delay.TotalSeconds);
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (!IsRetryableStatusCode(response.StatusCode))
                    {
                        _logger.LogWarning(
                            "[HTTP] Page {Page} failed permanently with HTTP {StatusCode} " +
                            "on attempt {Attempt} of {MaxAttempts}.",
                            page, (int)response.StatusCode, attempt, _options.MaxAttempts);
                    }
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
                    "[RETRY] Network failure retrieving page {Page}. Attempt {Attempt} " +
                    "of {MaxAttempts} failed after {ElapsedMilliseconds} ms. Retrying.",
                    page, attempt, _options.MaxAttempts, requestTimer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested &&
                      attempt < _options.MaxAttempts)
            {
                _logger.LogWarning(
                    exception,
                    "[RETRY] Request timed out retrieving page {Page}. Attempt {Attempt} " +
                    "of {MaxAttempts} failed after {ElapsedMilliseconds} ms. Retrying.",
                    page, attempt, _options.MaxAttempts, requestTimer.ElapsedMilliseconds);
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode is null && attempt == _options.MaxAttempts)
            {
                _logger.LogError(
                    exception,
                    "[RETRY] Exhausted {MaxAttempts} attempts for page {Page} after a " +
                    "network failure taking {ElapsedMilliseconds} ms.",
                    _options.MaxAttempts, page, requestTimer.ElapsedMilliseconds);
                throw;
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested &&
                      attempt == _options.MaxAttempts)
            {
                _logger.LogError(
                    exception,
                    "[RETRY] Exhausted {MaxAttempts} attempts for page {Page} after a " +
                    "request timeout taking {ElapsedMilliseconds} ms.",
                    _options.MaxAttempts, page, requestTimer.ElapsedMilliseconds);
                throw;
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
