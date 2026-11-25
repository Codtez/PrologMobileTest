using System.Text.Json;
using PrologMobile.Api.Pipeline;

namespace PrologMobile.Api.Endpoints;

public static class InventoryEndpoint
{
    public static async Task<IResult> GetSummaryAsync(
        InventoryPipeline pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            // Execute the complete inventory processing pipeline.
            var summaries = await pipeline.ExecuteAsync(
                cancellationToken);

            // Return the successfully aggregated inventory.
            return Results.Ok(summaries);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled the request.
            throw;
        }
        catch (HttpRequestException)
        {
            // The upstream API request failed.
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Upstream API Failure",
                detail: "Unable to retrieve inventory data from the upstream API.");
        }
        catch (JsonException)
        {
            // The upstream API returned invalid JSON.
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Invalid Upstream Response",
                detail: "The upstream API returned invalid inventory data.");
        }
        catch (InvalidDataException)
        {
            // The retrieved dataset failed our validation checks.
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Inventory Validation Failure",
                detail: "The retrieved inventory dataset could not be verified.");
        }
        catch (TimeoutException)
        {
            // The overall pipeline exceeded its execution deadline.
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Pipeline Timeout",
                detail: "Inventory processing exceeded its execution deadline.");
        }
        catch (OperationCanceledException)
        {
            // An upstream request timed out without caller cancellation.
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Upstream Request Timeout",
                detail: "The upstream API did not respond within the permitted time.");
        }
        catch (Exception)
        {
            // Handle unexpected internal application failures.
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error",
                detail: "An unexpected error occurred while processing inventory.");
        }
    }
}