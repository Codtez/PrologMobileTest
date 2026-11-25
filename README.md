# Inventory Aggregation Service

A C# / ASP.NET Core service that retrieves paginated inventory records from the Prolog Mobile Inventory API, verifies dataset completeness and consistency, and returns inventory summaries by category.

## Prerequisites

- .NET 10 SDK
- Git
- The Inventory API key provided with the assignment
- Network access to the upstream Inventory API

## Setup and run

Clone the repository and restore dependencies:

```powershell
git clone https://github.com/Codtez/PrologMobileTest.git
cd PrologMobileTest
dotnet restore
```

Configure the supplied API key using .NET user secrets (replace the placeholder with your own key):

```powershell
dotnet user-secrets set "InventoryApi:ApiKey" "YOUR_API_KEY" --project src/PrologMobile.Api
```

The project already contains a user-secrets identifier. Alternatively, set the environment variable `InventoryApi__ApiKey`. Do not commit the key. The upstream base URL is configured in `src/PrologMobile.Api/appsettings.json`.

Build and run the application locally in the Development environment:

```powershell
dotnet build
dotnet run --project src/PrologMobile.Api -- --environment Development --urls http://localhost:5077
```

In a separate terminal, call the endpoint:

```powershell
Invoke-RestMethod -Uri "http://localhost:5077/api/inventory/summary" -TimeoutSec 100
```

Alternatively:

```bash
curl --max-time 100 http://localhost:5077/api/inventory/summary
```

A successful live retrieval took approximately 13 seconds during development. The configured overall pipeline deadline is 90 seconds.

## Running tests

From the repository root:

```powershell
dotnet test
```

The xUnit suite covers aggregation, page parsing and validation, HTTP retry behavior, cancellation, request spacing, and shared cooldown behavior. HTTP tests use simulated responses rather than requiring live upstream credentials.

## Endpoint

`GET /api/inventory/summary`

Returns HTTP 200 with a JSON array of category summaries. Each summary contains:

- `category`: normalized category name.
- `totalInventoryValue`: sum of unit price multiplied by quantity, in USD.
- `availableRecordCount`: number of records with positive quantity, not the total number of units.
- `latestRecord`: full record with the most recent update timestamp.
- `items`: full available records used to calculate the summary.

The response uses a top-level array because there is no additional response-level metadata to expose. The `latestRecord` property represents the assignment's most recently updated record and includes the full original record for consistency with `items`.

Upstream HTTP, transport, parsing, and validation failures return HTTP 502 with a Problem Details response. Pipeline and upstream request timeouts return HTTP 504; unexpected application failures return HTTP 500. Caller cancellation is propagated. Internal exception details are not returned to callers.

## Architecture

The application uses a layered architecture with a central orchestration pipeline. Each component has a focused responsibility:

- **Endpoint:** maps pipeline results and failures to HTTP responses.
- **InventoryPipeline:** coordinates the retrieval, validation, deduplication, and aggregation stages in sequence and enforces the overall deadline.
- **InventoryRetriever / InventoryApiClient:** retrieve pages and perform HTTP communication using a typed client registered through `IHttpClientFactory`.
- **InventoryRetryPolicy / InventoryRateLimiter:** handle transient failures, request spacing, and upstream cooldowns. The rate limiter is a singleton so concurrent requests share the same upstream allowance within the application process.
- **InventoryPageParser / InventoryValidator:** deserialize responses and check record validity, page metadata, and dataset completeness.
- **InventoryDeduplicator / InventoryAggregator:** verify duplicate consistency and compute category summaries. Aggregation is independent of HTTP and can be tested directly.

The pipeline executes eight phases: retrieve the initial page, establish dataset dimensions, retrieve remaining pages, validate cross-page metadata, deduplicate records, verify completeness, aggregate records, and return results. Separating these responsibilities makes failures easier to isolate and business rules easier to modify without changing the HTTP client.

## Configuration and diagnostics

Defaults in `appsettings.json`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `InventoryApi:PageSize` | 25 | Reduce upstream request count |
| `InventoryApi:RequestTimeoutSeconds` | 15 | Individual HTTP request timeout |
| `InventoryApi:MaxAttempts` | 3 | Maximum attempts per page, including initial attempt |
| `InventoryApi:RequestIntervalMilliseconds` | 2200 | Minimum spacing between upstream request permissions |
| `InventoryApi:RateLimitDelaySeconds` | 11 | Fallback cooldown for HTTP 429 without Retry-After |
| `InventoryApi:OverallTimeoutSeconds` | 90 | Overall pipeline deadline |
| `Diagnostics:Enabled` | true | Enable detailed application diagnostics |

Set `Diagnostics:Enabled` to `false` and restart the application to suppress routine application diagnostics while retaining warnings and errors. Logs identify pipeline phases, page retrievals, retry behavior, duplicate records, and validation failures. API credentials are not intentionally logged.

## API observations and verification

Before implementing the service, I examined the upstream API using PowerShell with page sizes of 25 and 20.

| Page size | Pages | Raw records | Unique records | Extra duplicate copies |
| --- | ---: | ---: | ---: | ---: |
| 25 | 7 | 160 | 157 | 3 |
| 20 | 8 | 161 | 157 | 4 |

Duplicate records were compared across all six record fields. Both retrievals produced the same unique dataset. Rapid requests also produced HTTP 429 responses indicating a limit of five requests per ten seconds. Category names contained inconsistent casing and surrounding whitespace.

The implemented service was checked against the independently calculated PowerShell results:

| Category | Available records | Inventory value (USD) | Latest record ID |
| --- | ---: | ---: | ---: |
| electronics | 25 | 18,253.18 | 1037 |
| home & kitchen | 27 | 17,151.51 | 1019 |
| office supplies | 30 | 15,996.70 | 1091 |
| outdoor | 27 | 17,822.93 | 1073 |
| toys | 27 | 15,879.52 | 1055 |
| **Total** | **136** | **85,103.84** | — |

Of the 157 unique records, 21 had zero quantity and were excluded. These figures are verification results, not hardcoded expectations in the application.

## Assumptions and design decisions

- The upstream dataset is static, as specified in the assignment. Record IDs are unique identifiers but are not assumed to be consecutive.
- Identical duplicate records are ignored. Duplicate IDs with conflicting deserialized field values cause validation to fail rather than silently selecting one version.
- Category names are trimmed and converted to invariant lowercase for grouping. Original record values are preserved in the response items.
- Monetary calculations use `decimal`. Negative prices and quantities and missing or blank names or categories are rejected.
- When multiple records have the same latest timestamp, the lowest ID wins. Categories are sorted by name and items by ID for deterministic output.
- Expected page count is calculated from `totalRecords` and the configured page size. Inconsistent reported `totalPages` values are logged as warnings. Changing record totals, incorrect page identifiers or sizes, and a final unique record count that differs from the expected total cause validation to fail.
- Retryable failures include HTTP 408, 429, 500, 502, 503, and 504, transport failures, and individual request timeouts. Retry-After is respected when present. Other failures are not retried indiscriminately. Caller cancellation and the overall deadline stop retries.
- The observed upstream rate limit is conservatively treated as shared by all requests using the configured API access. Its exact enforcement scope was not independently established.

## Known limitations and future improvements

- Completeness checks depend on the upstream `totalRecords` being accurate. They cannot independently prove completeness if the API reports an incorrect but internally consistent total.
- Invalid or incomplete datasets fail explicitly rather than returning partial summaries. A bounded full-dataset retry or alternate page-size retrieval could be explored for recoverable pagination anomalies.
- Rate-limiting state is process-local. Multiple application instances sharing upstream credentials would require distributed coordination. Already-dispatched requests cannot be recalled when a cooldown is received.
- Each incoming request retrieves the dataset again. A short-lived cache of verified summaries, combined with coordination of concurrent cache misses, could reduce upstream traffic if freshness requirements permit.
- The service retains the dataset in memory during processing, which is suitable for the assignment's dataset size but would need reconsideration for substantially larger datasets.
- Unit tests exercise transformation and client resilience components. Additional end-to-end automated tests could cover the complete pipeline, cross-page conflicts, completeness failures, and HTTP error mappings. Successful live retrieval and a controlled upstream connection failure were checked manually.

The implementation deliberately avoids persistence, caching, and distributed infrastructure to keep the assignment focused on reliable retrieval and aggregation.
