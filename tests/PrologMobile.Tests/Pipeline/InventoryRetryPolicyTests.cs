using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrologMobile.Api.Clients;
using PrologMobile.Api.Configuration;
using PrologMobile.Api.Resilience;

namespace PrologMobile.Tests.Pipeline;

public sealed class InventoryRetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task GetPageAsync_RetriesTemporaryStatusAndDisposesFailedResponse(HttpStatusCode status)
    {
        var content = new TrackingContent("failure");
        var (policy, handler, _) = CreatePolicy((attempt, _) => Task.FromResult(
            attempt == 1
                ? new HttpResponseMessage(status) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await policy.GetPageAsync(1);

        Assert.Equal(2, handler.Attempts);
        Assert.True(content.Disposed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPageAsync_DoesNotRetryPermanentStatus()
    {
        var content = new TrackingContent("failure");
        var (policy, handler, _) = CreatePolicy((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => policy.GetPageAsync(1));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(1, handler.Attempts);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPageAsync_RetriesNetworkFailureAndRequestTimeout(bool timeout)
    {
        var (policy, handler, _) = CreatePolicy((attempt, _) =>
            attempt == 1
                ? Task.FromException<HttpResponseMessage>(timeout
                    ? new TaskCanceledException("request timeout")
                    : new HttpRequestException("network failure"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await policy.GetPageAsync(1);

        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task GetPageAsync_StopsAfterMaximumAttempts()
    {
        var (policy, handler, _) = CreatePolicy((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => policy.GetPageAsync(1));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task GetPageAsync_DoesNotRetryCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var (policy, handler, _) = CreatePolicy((_, token) =>
        {
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.GetPageAsync(1, cts.Token));
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task GetPageAsync_AppliesSharedCooldownAfterLastAttempt(
        HttpStatusCode status, bool retryAfter)
    {
        var options = new InventoryApiOptions
        {
            MaxAttempts = 1,
            RequestIntervalMilliseconds = 1,
            RateLimitDelaySeconds = 1
        };
        var (policy, handler, limiter) = CreatePolicy((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            }
            return Task.FromResult(response);
        }, options);

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.GetPageAsync(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task GetPageAsync_RetryAfterOverridesRateLimitFallback()
    {
        var (policy, handler, _) = CreatePolicy((attempt, _) =>
        {
            var response = new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (attempt == 1)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            }
            return Task.FromResult(response);
        }, new InventoryApiOptions
        {
            RequestIntervalMilliseconds = 1,
            RateLimitDelaySeconds = 11
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        using var response = await policy.GetPageAsync(1, cts.Token);

        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task GetPageAsync_UsesNormalSpacingForRetries()
    {
        var (policy, handler, _) = CreatePolicy((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            new InventoryApiOptions { RequestIntervalMilliseconds = 1000 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.GetPageAsync(1, cts.Token));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task WaitAsync_RechecksCooldownExtendedDuringWait()
    {
        var options = Options.Create(new InventoryApiOptions { RequestIntervalMilliseconds = 100 });
        var limiter = new InventoryRateLimiter(options, NullLogger<InventoryRateLimiter>.Instance);
        await limiter.WaitAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var waiting = limiter.WaitAsync(cts.Token);
        limiter.ApplyCooldown(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static (InventoryRetryPolicy Policy, StubHandler Handler, InventoryRateLimiter Limiter)
        CreatePolicy(
            Func<int, CancellationToken, Task<HttpResponseMessage>> respond,
            InventoryApiOptions? settings = null)
    {
        var options = Options.Create(settings ?? new InventoryApiOptions
        {
            RequestIntervalMilliseconds = 1
        });
        var handler = new StubHandler(respond);
        var client = new InventoryApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }, options);
        var limiter = new InventoryRateLimiter(options, NullLogger<InventoryRateLimiter>.Instance);
        return (new InventoryRetryPolicy(
            client, limiter, options, NullLogger<InventoryRetryPolicy>.Instance), handler, limiter);
    }
}
