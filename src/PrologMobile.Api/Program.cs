using PrologMobile.Api.Configuration;
using PrologMobile.Api.Clients;
using PrologMobile.Api.Logging;
using PrologMobile.Api.Pipeline;
using PrologMobile.Api.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

var diagnosticsEnabled = builder.Configuration.GetValue<bool>("Diagnostics:Enabled");
var diagnosticLevel = diagnosticsEnabled ? LogLevel.Debug : LogLevel.Warning;
builder.Logging.AddFilter("PrologMobile.Api", diagnosticLevel);
builder.Logging.AddFilter<ConsoleLoggerProvider>("PrologMobile.Api", diagnosticLevel);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddConsole(options => options.FormatterName = InventoryConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<InventoryConsoleFormatter, ConsoleFormatterOptions>();

//////// VALIDATE CONFIGURATION AT START ////////

// Register and validate external inventory API configuration.
builder.Services
    .AddOptions<InventoryApiOptions>()
    .BindConfiguration(InventoryApiOptions.SectionName)

    // Verify that the base URL is a valid absolute HTTPS URL.
    .Validate(
        options => Uri.TryCreate(
            options.BaseUrl, UriKind.Absolute, out var uri
        ) && uri.Scheme == Uri.UriSchemeHttps,
        "Inventory API BaseUrl must be a valid HTTPS URL."
    )

    // Verify that an authentication key has been supplied.
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Inventory API key is required."
    )

    // Verify that the page size is supported by the external API.
    .Validate(
        options => options.PageSize >= 1 && options.PageSize <= 25,
        "Inventory API PageSize must be between 1 and 25."
    )

    // Verify that the individual request timeout is positive.
    .Validate(
        options => options.RequestTimeoutSeconds > 0,
        "RequestTimeoutSeconds must be greater than zero."
    )

    // Verify that at least one request attempt is permitted.
    .Validate(
        options => options.MaxAttempts >= 1,
        "MaxAttempts must be at least one."
    )

    // Verify that the request interval is positive.
    .Validate(
        options => options.RequestIntervalMilliseconds > 0,
        "RequestIntervalMilliseconds must be greater than zero."
    )

    // Verify that the fallback rate limit delay is positive.
    .Validate(
        options => options.RateLimitDelaySeconds > 0,
        "RateLimitDelaySeconds must be greater than zero."
    )

    // Verify that the overall timeout accommodates individual requests.
    .Validate(
        options => options.OverallTimeoutSeconds >=
                   options.RequestTimeoutSeconds,
        "OverallTimeoutSeconds must be at least RequestTimeoutSeconds."
    )

    // Execute configuration validation during application startup.
    .ValidateOnStart();
//////// VALIDATION END////////

/////// HTTP START ///////////
// Register one shared rate limiter for all inventory API clients.
builder.Services.AddSingleton<InventoryRateLimiter>(); //Scoped(per scope)/Transient(per resolution)/Singleton
builder.Services.AddTransient<InventoryRetryPolicy>();
builder.Services.AddTransient<InventoryPageParser>();
builder.Services.AddTransient<InventoryValidator>();
builder.Services.AddTransient<InventoryRetriever>();
builder.Services.AddTransient<InventoryAggregator>();
builder.Services.AddTransient<InventoryDeduplicator>();
builder.Services.AddTransient<InventoryPipeline>();

//// Register a typed HTTP client for the external inventory API using config.
builder.Services.AddHttpClient<InventoryApiClient>(
    (serviceProvider, client) =>
    {
        // Retrieve the validated API configuration.
        var options = serviceProvider
            .GetRequiredService<IOptions<InventoryApiOptions>>()
            .Value;

        // Configure the external API base address.
        client.BaseAddress = new Uri(options.BaseUrl);

        // Configure the individual HTTP request timeout.
        client.Timeout = TimeSpan.FromSeconds(
            options.RequestTimeoutSeconds
        );

        // Supply the required API authentication header.
        client.DefaultRequestHeaders.Add(
            "X-Api-Key", options.ApiKey
        );

        // Request responses in JSON format.
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/json"
        );
    }
).RedactLoggedHeaders(_ => true);



// Build the application using our registered services.
var app = builder.Build();

// Expose the inventory summary endpoint.
app.MapGet("/api/inventory/summary", async (
    InventoryPipeline pipeline,
    CancellationToken cancellationToken) =>
{
    // Execute the complete inventory processing pipeline.
    var summaries = await pipeline.ExecuteAsync(
        cancellationToken
    );

    // Return the category summaries as a JSON response.
    return Results.Ok(summaries);
});

// Start the HTTP server.
app.Run();