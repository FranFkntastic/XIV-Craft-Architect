using System.Net.Http.Json;
using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed class WorkshopHostAcquisitionClient : IWorkshopHostAcquisitionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public WorkshopHostAcquisitionClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<WorkshopHostCapabilityResponse> GetCapabilitiesAsync(
        WorkshopHostConnectionOptions connection,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkshopHostCapabilityResponse>(
            connection,
            HttpMethod.Get,
            "capabilities",
            body: null,
            "capability discovery",
            cancellationToken);

    public Task<WorkshopHostAcquisitionRequestView> CreateBatchAsync(
        WorkshopHostConnectionOptions connection,
        WorkshopHostAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkshopHostAcquisitionRequestView>(
            connection,
            HttpMethod.Post,
            "acquisition/batches",
            request,
            "acquisition handoff",
            cancellationToken);

    public Task<WorkshopHostAcquisitionTimeline> GetTimelineAsync(
        WorkshopHostConnectionOptions connection,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Acquisition request id is required.", nameof(requestId));

        return SendAsync<WorkshopHostAcquisitionTimeline>(
            connection,
            HttpMethod.Get,
            $"acquisition/requests/{Uri.EscapeDataString(requestId)}/timeline",
            body: null,
            "acquisition timeline",
            cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        WorkshopHostConnectionOptions connection,
        HttpMethod method,
        string relativePath,
        object? body,
        string operation,
        CancellationToken cancellationToken)
    {
        var apiBase = ResolveApiBase(connection);
        using var request = new HttpRequestMessage(method, new Uri(apiBase, relativePath));
        request.Headers.Add("X-Api-Key", connection.ApiKey.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        if (body != null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Workshop Host {operation} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {details}",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workshop Host {operation} returned an empty response.");
    }

    private Uri ResolveApiBase(WorkshopHostConnectionOptions connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.ApiBaseUrl))
            throw new ArgumentException("Workshop Host API URL is required.", nameof(connection));
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
            throw new ArgumentException("Workshop Host API key is required.", nameof(connection));

        var baseUri = Uri.TryCreate(connection.ApiBaseUrl.Trim(), UriKind.Absolute, out var absolute)
            ? absolute
            : _httpClient.BaseAddress == null
                ? throw new ArgumentException("A relative Workshop Host URL requires an HTTP client base address.", nameof(connection))
                : new Uri(_httpClient.BaseAddress, connection.ApiBaseUrl.Trim());
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/inventory", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/inventory".Length];
        if (!path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path = $"{path}/api";
        return new UriBuilder(baseUri) { Path = $"{path.TrimEnd('/')}/" }.Uri;
    }
}
