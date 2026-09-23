using System.Net.Http.Json;
using System.Text.Json;

namespace Contoso.Lending.Workflow;

/// <summary>
/// The only way layer 3 reaches behavior: HTTP calls to the layer-2 service API
/// (docs/api-contract.md). No database client, no domain library, no rules — a transport.
/// </summary>
public sealed class ServiceApiClient : IDisposable
{
    public const string DefaultBaseAddress = "http://localhost:5080";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;

    public ServiceApiClient(string baseAddress)
    {
        http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string BaseAddressFromEnvironment() =>
        Environment.GetEnvironmentVariable("SERVICE_API_URL") ?? DefaultBaseAddress;

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, request, Json, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ServiceApiException($"{path} returned {(int)response.StatusCode}: {body}");
        }
        return JsonSerializer.Deserialize<TResponse>(body, Json)
            ?? throw new ServiceApiException($"{path} returned an empty body");
    }

    public void Dispose() => http.Dispose();
}

public sealed class ServiceApiException(string message) : Exception(message);
