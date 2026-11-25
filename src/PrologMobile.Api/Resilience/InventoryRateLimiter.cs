using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;

namespace PrologMobile.Api.Resilience;

public sealed class InventoryRateLimiter
{
    // Allow only one caller at a time to calculate its request timing.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Minimum required interval between requests.
    private readonly TimeSpan _requestInterval;
    private readonly ILogger<InventoryRateLimiter> _logger;

    // Earliest permitted time for the next request.
    private DateTimeOffset _nextRequestTime = DateTimeOffset.MinValue;
    
    // Earliest permitted request time following a server-imposed cooldown.
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    // Protect the shared cooldown timestamp from concurrent modifications.
    private readonly object _cooldownLock = new();

    public InventoryRateLimiter(
        IOptions<InventoryApiOptions> options,
        ILogger<InventoryRateLimiter> logger)
    {
        // Retrieve the request interval from our configuration.
        _requestInterval = TimeSpan.FromMilliseconds(
            options.Value.RequestIntervalMilliseconds
        );
        _logger = logger;
    }

    // Wait until another outgoing API request is permitted.
    public async Task WaitAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[RATE LIMIT] Waiting for request permission. Normal spacing: {SpacingMilliseconds} ms.",
            _requestInterval.TotalMilliseconds);
        // Acquire exclusive access to the request schedule.
        await _gate.WaitAsync(cancellationToken);

        var permissionGranted = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan delay;
                TimeSpan cooldownRemaining;

                lock (_cooldownLock)
                {
                    // Start with our normal request interval.
                    var permittedTime = _nextRequestTime;

                    // Use the cooldown instead if it expires later.
                    if (_cooldownUntil > permittedTime)
                    {
                        permittedTime = _cooldownUntil;
                    }

                    // Calculate the remaining waiting period.
                    var currentTime = DateTimeOffset.UtcNow;
                    delay = permittedTime - currentTime;
                    cooldownRemaining = _cooldownUntil - currentTime;

                    // If no waiting is required, reserve the next request time.
                    if (delay <= TimeSpan.Zero)
                    {
                        _nextRequestTime =
                            currentTime.Add(_requestInterval);
                        permissionGranted = true;
                        return;
                    }
                }

                _logger.LogInformation(
                    "[RATE LIMIT] Waiting {DelayMilliseconds} ms. Normal spacing: " +
                    "{SpacingMilliseconds} ms; server cooldown remaining: {CooldownMilliseconds} ms.",
                    delay.TotalMilliseconds, _requestInterval.TotalMilliseconds,
                    Math.Max(0, cooldownRemaining.TotalMilliseconds));
                // Wait asynchronously without holding the cooldown lock.
                await Task.Delay(delay, cancellationToken);

                // Recheck both timestamps in case another caller
                // extended the cooldown while we were waiting.
            }
        }
        finally
        {
            // Always release the request scheduling gate.
            _gate.Release();
            if (permissionGranted)
            {
                _logger.LogInformation("[RATE LIMIT] Request permission granted.");
            }
        }
    }

    // Apply a shared cooldown to all outgoing inventory API requests.
    public void ApplyCooldown(TimeSpan delay)
    {
        // Ignore delays that do not require additional waiting.
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        lock (_cooldownLock)
        {
            // Calculate when the cooldown should expire.
            var cooldownUntil = DateTimeOffset.UtcNow.Add(delay);

            // Extend the existing cooldown only if the new one ends later.
            if (cooldownUntil > _cooldownUntil)
            {
                _cooldownUntil = cooldownUntil;
            }
        }

        _logger.LogInformation(
            "[RATE LIMIT] Server cooldown applied or already exceeded: {CooldownMilliseconds} ms.",
            delay.TotalMilliseconds);
    }
}