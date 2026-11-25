using Microsoft.Extensions.Options;
using PrologMobile.Api.Configuration;

namespace PrologMobile.Api.Clients;

public sealed class InventoryApiClient
{
    private readonly HttpClient _httpClient;
    private readonly InventoryApiOptions _options;

    public InventoryApiClient(
        HttpClient httpClient,
        IOptions<InventoryApiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    // The caller owns and must dispose the returned response.
    public Task<HttpResponseMessage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = $"/api/records?page={page}&pageSize={_options.PageSize}";
        return _httpClient.GetAsync(requestUrl, cancellationToken);
    }
}
