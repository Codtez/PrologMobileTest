using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;

namespace PrologMobile.Api.Clients;

public sealed class InventoryRateLimiter
{
    // Allow only one caller at a time to calculate its request timing.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Minimum required interval between requests.
    private readonly TimeSpan _requestInterval;

    // Earliest permitted time for the next request.
    private DateTimeOffset _nextRequestTime = DateTimeOffset.MinValue;

    public InventoryRateLimiter(
        IOptions<InventoryApiOptions> options)
    {
        // Retrieve the request interval from our configuration.
        _requestInterval = TimeSpan.FromMilliseconds(
            options.Value.RequestIntervalMilliseconds
        );
    }

    // Wait until another outgoing API request is permitted.
    public async Task WaitAsync(
        CancellationToken cancellationToken = default)
    {
        // Acquire exclusive access to the request schedule.
        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Calculate how long we must wait before sending a request.
            var currentTime = DateTimeOffset.UtcNow;
            var delay = _nextRequestTime - currentTime;

            // Wait only if the next permitted request time is in the future.
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            // Reserve the next available request time.
            _nextRequestTime =
                DateTimeOffset.UtcNow.Add(_requestInterval);
        }
        finally
        {
            // Always release the gate, even if cancellation occurs.
            _gate.Release();
        }
    }
}