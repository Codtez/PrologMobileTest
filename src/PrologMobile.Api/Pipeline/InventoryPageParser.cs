using System.Text.Json;
using PrologMobile.Api.Models;

namespace PrologMobile.Api.Pipeline;

public sealed class InventoryPageParser
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true
        };

    public async Task<InventoryPage> ParseAsync(
        HttpResponseMessage response,
        int page,
        CancellationToken cancellationToken = default)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<InventoryPage>(responseBody, JsonOptions)
            ?? throw new JsonException($"Inventory page {page} returned a null response.");
    }
}
