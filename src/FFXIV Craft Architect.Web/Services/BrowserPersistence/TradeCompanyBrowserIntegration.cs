using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.BrowserPersistence;

public sealed record TradeCompanyBrowserConnection(
    CompanyId CompanyId,
    string ServiceUrl,
    string AccessKey,
    Guid GrantId,
    TradeCompanyRole Role)
{
    public TradeCompanyAccessContext Access =>
        new(CompanyId, GrantId, Role);
}

public sealed record TradeCompanyConnectionSession(
    TradeCompanyBrowserConnection Connection,
    TradeCompanyIdentity Company);

public sealed class TradeCompanyConnectionStore(IndexedDbService indexedDb)
{
    private const string SettingPrefix = "tradeCompany.connection.";

    public async Task<TradeCompanyBrowserConnection?> LoadAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await indexedDb.EnsureSpecializedStorageAsync();
        var connection = await indexedDb.LoadSettingAsync<TradeCompanyBrowserConnection?>(
            BuildKey(companyId));
        if (connection is null)
        {
            return null;
        }

        Validate(connection);
        if (connection.CompanyId != companyId)
        {
            throw new InvalidOperationException(
                "The stored Trade Company credential belongs to another company.");
        }

        return connection;
    }

    public async Task SaveAsync(
        TradeCompanyBrowserConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(connection);
        await indexedDb.EnsureSpecializedStorageAsync();
        if (!await indexedDb.SaveSettingAsync(BuildKey(connection.CompanyId), connection))
        {
            throw new InvalidOperationException(
                "The browser could not persist the Trade Company connection.");
        }
    }

    private static string BuildKey(CompanyId companyId) =>
        SettingPrefix + companyId;

    private static void Validate(TradeCompanyBrowserConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.AccessKey))
        {
            throw new InvalidOperationException("A Trade Company access key is required.");
        }

        if (connection.GrantId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A verified Trade Company grant is required.");
        }

        if (!Uri.TryCreate(connection.ServiceUrl.Trim(), UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(serviceUri.Query) ||
            !string.IsNullOrEmpty(serviceUri.Fragment))
        {
            throw new InvalidOperationException(
                "The Trade Company service URL must be an absolute HTTP or HTTPS origin.");
        }

        if (serviceUri.Scheme == "http" &&
            !serviceUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Trade Company credentials require HTTPS outside local development.");
        }
    }
}

public sealed class TradeCompanyConnectionService(
    HttpClient http,
    TradeCompanyConnectionStore connections,
    TradeCompanyBrowserPersistence browser,
    PortableOperatorSettingsStore portableSettings,
    WebSettingsService settings,
    DurableTradeCompanyClient durableClient)
{
    private const string AccessKeyHeader = "X-Trade-Company-Key";

    public async Task<TradeCompanyConnectionSession> ConnectAsync(
        CompanyId companyId,
        string serviceUrl,
        string accessKey,
        CancellationToken cancellationToken = default)
    {
        var baseUri = new Uri(serviceUrl.Trim().TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                baseUri,
                $"trade-company/v1/companies/{companyId}/session"));
        request.Headers.Add(AccessKeyHeader, accessKey.Trim());
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<TradeCompanySession>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "The Trade Company service returned an empty session.");
        if (session.Company.CompanyId != companyId ||
            session.Access.CompanyId != companyId ||
            session.Access.GrantId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The Trade Company service returned a mismatched session.");
        }

        var connection = new TradeCompanyBrowserConnection(
            companyId,
            serviceUrl.Trim().TrimEnd('/'),
            accessKey.Trim(),
            session.Access.GrantId,
            session.Access.Role);
        await connections.SaveAsync(connection, cancellationToken);
        await browser.SaveIdentityAsync(session.Company, cancellationToken);
        await durableClient.GetChangesAsync(
            companyId,
            CompanyRevision.None,
            cancellationToken);
        var canonicalSettings = await portableSettings.HydrateCanonicalAsync(
            connection.Access,
            cancellationToken);
        if (canonicalSettings == null &&
            connection.Role is not TradeCompanyRole.ReadOnly)
        {
            canonicalSettings = await portableSettings.MigrateLegacyAsync(
                connection.Access,
                cancellationToken);
        }
        if (canonicalSettings != null)
        {
            await settings.ApplyPortableSettingsAsync(
                canonicalSettings.Settings,
                cancellationToken);
        }
        if (connection.Role is not TradeCompanyRole.ReadOnly)
        {
            await durableClient.ReplayPendingAsync(companyId, cancellationToken);
        }
        return new TradeCompanyConnectionSession(connection, session.Company);
    }

    private sealed record TradeCompanySession(
        TradeCompanyIdentity Company,
        TradeCompanyAccessContext Access);
}

internal sealed class HttpTradeCompanyTransport(
    HttpClient http,
    TradeCompanyConnectionStore connections) : ITradeCompanyClient
{
    private const string AccessKeyHeader = "X-Trade-Company-Key";

    public async Task<TradeCompanyIdentity?> GetCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            companyId,
            $"trade-company/v1/companies/{companyId}",
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var identity = await response.Content.ReadFromJsonAsync<TradeCompanyIdentity>(
            cancellationToken: cancellationToken);
        if (identity?.CompanyId != companyId)
        {
            throw new InvalidOperationException(
                "The Trade Company service returned a mismatched company identity.");
        }

        return identity;
    }

    public async Task<TradeCompanyChangeSet> GetChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            companyId,
            $"trade-company/v1/companies/{companyId}/changes?afterRevision={afterRevision.Value}",
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var changes = await response.Content.ReadFromJsonAsync<TradeCompanyChangeSet>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "The Trade Company service returned an empty change set.");
        if (changes.CompanyId != companyId ||
            changes.CompanyRevision.Value < afterRevision.Value ||
            changes.Records.Any(record => record.CompanyId != companyId))
        {
            throw new InvalidOperationException(
                "The Trade Company service returned an invalid cross-company change set.");
        }

        return changes;
    }

    public async Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyMutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Put,
            mutation.CompanyId,
            $"trade-company/v1/companies/{mutation.CompanyId}/records/" +
            $"{Uri.EscapeDataString(mutation.RecordKind)}/{Uri.EscapeDataString(mutation.RecordId)}",
            cancellationToken);
        request.Content = JsonContent.Create(new TradeCompanyRecordPutBody(
            mutation.PayloadJson,
            mutation.ExpectedRecordRevision,
            mutation.ExpectedCompanyRevision,
            mutation.IdempotencyKey,
            mutation.ProtocolVersion));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(
                "The Trade Company access key is missing, invalid, or cannot mutate this company.");
        }

        var result = await response.Content.ReadFromJsonAsync<TradeCompanyMutationResult>(
            cancellationToken: cancellationToken);
        if (result is null)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException(
                "The Trade Company service returned an empty mutation result.");
        }

        if (result.Record?.CompanyId != mutation.CompanyId ||
            result.CurrentRecord?.CompanyId != mutation.CompanyId)
        {
            throw new InvalidOperationException(
                "The Trade Company service returned a mutation from another company.");
        }

        return result;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        CompanyId companyId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var connection = await connections.LoadAsync(companyId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Connect this browser to the Trade Company before synchronizing it.");
        var baseUri = new Uri(connection.ServiceUrl.Trim().TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Add(AccessKeyHeader, connection.AccessKey);
        return request;
    }

    private sealed record TradeCompanyRecordPutBody(
        string PayloadJson,
        CompanyRecordRevision ExpectedRecordRevision,
        CompanyRevision ExpectedCompanyRevision,
        string IdempotencyKey,
        int ProtocolVersion);
}

public static class TradeCompanyBrowserIntegrationRegistration
{
    public static IServiceCollection AddTradeCompanyBrowserIntegration(
        this IServiceCollection services)
    {
        services.AddScoped<TradeCompanyConnectionStore>();
        services.AddScoped<TradeCompanyConnectionService>();
        services.AddScoped<TradeCompanyBrowserPersistence>();
        services.AddScoped<PortableOperatorSettingsStore>();
        services.AddScoped<HttpTradeCompanyTransport>();
        services.AddScoped<DurableTradeCompanyClient>(provider =>
            new DurableTradeCompanyClient(
                provider.GetRequiredService<HttpTradeCompanyTransport>(),
                provider.GetRequiredService<TradeCompanyBrowserPersistence>()));
        services.AddScoped<ITradeCompanyClient>(provider =>
            provider.GetRequiredService<DurableTradeCompanyClient>());
        return services;
    }
}
