using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.BrowserPersistence;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class BrowserPersistenceContractTests
{
    private static readonly CompanyId Company =
        new(Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a1111"));

    [Fact]
    public void PortableSettings_IncludePreferencesAndExcludeSecretsOrSessionState()
    {
        Assert.Contains("market.default_datacenter", PortableOperatorSettingKeys.All);
        Assert.Contains("procurement.travel_priority", PortableOperatorSettingKeys.All);
        Assert.Contains("ui.accent_color", PortableOperatorSettingKeys.All);

        Assert.DoesNotContain("profileHost.accessKey", PortableOperatorSettingKeys.All);
        Assert.DoesNotContain("marketmafioso.api_key", PortableOperatorSettingKeys.All);
        Assert.DoesNotContain("marketmafioso.pending_submission", PortableOperatorSettingKeys.All);
        Assert.DoesNotContain("debug.secret_tools_enabled", PortableOperatorSettingKeys.All);
    }

    [Fact]
    public async Task DurableClient_WritesAheadBeforeTransportAndCompletesAfterCanonicalResult()
    {
        var calls = new List<string>();
        var request = Mutation("write-ahead");
        var record = new TradeCompanyRecordEnvelope(
            Company,
            TradeCompanyRecordKinds.Order,
            "order-1",
            "{}",
            new CompanyRecordRevision(1),
            new CompanyRevision(2),
            DateTime.UnixEpoch);
        var result = new TradeCompanyMutationResult(
            TradeCompanyMutationStatus.Applied,
            record);
        var runtime = new RecordingRuntime(calls, request);
        var transport = new RecordingTransport(calls, result);
        var client = new DurableTradeCompanyClient(
            transport,
            new TradeCompanyBrowserPersistence(runtime));

        var actual = await client.MutateAsync(request);

        Assert.Same(result, actual);
        Assert.Equal(
            ["browser-enqueue", "browser-attempt", "transport-mutate", "browser-complete"],
            calls);
    }

    [Fact]
    public async Task DurableClient_LeavesOutboxPendingWhenTransportFails()
    {
        var calls = new List<string>();
        var request = Mutation("offline");
        var runtime = new RecordingRuntime(calls, request);
        var transport = new RecordingTransport(calls, result: null);
        var client = new DurableTradeCompanyClient(
            transport,
            new TradeCompanyBrowserPersistence(runtime));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.MutateAsync(request));

        Assert.Equal(["browser-enqueue", "browser-attempt", "transport-mutate"], calls);
    }

    [Fact]
    public async Task PortableSettings_RejectBrowserLocalKeyBeforeStorage()
    {
        var calls = new List<string>();
        var runtime = new RecordingRuntime(calls, Mutation("unused"));
        var browser = new TradeCompanyBrowserPersistence(runtime);
        var store = new PortableOperatorSettingsStore(runtime, browser);
        var access = new TradeCompanyAccessContext(
            Company,
            Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a2222"),
            TradeCompanyRole.Operator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetAsync(access, "profileHost.accessKey", "secret"));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task PortableSettings_RequireOperatorOrOwnerGrant()
    {
        var calls = new List<string>();
        var runtime = new RecordingRuntime(calls, Mutation("unused"));
        var store = new PortableOperatorSettingsStore(
            runtime,
            new TradeCompanyBrowserPersistence(runtime));
        var access = new TradeCompanyAccessContext(
            Company,
            Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a2222"),
            TradeCompanyRole.ReadOnly);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetAsync(access, "ui.accent_color", "#fff"));

        Assert.Empty(calls);
    }

    [Fact]
    public void IndexedDbModule_DeclaresAuthorityScopedDatabases()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "indexedDB.js"));

        Assert.Contains("FFXIVCraftArchitect.Personal", script, StringComparison.Ordinal);
        Assert.Contains("FFXIVCraftArchitect.Market", script, StringComparison.Ordinal);
        Assert.Contains("FFXIVCraftArchitect.Engine", script, StringComparison.Ordinal);
        Assert.Contains("FFXIVCraftArchitect.Company", script, StringComparison.Ordinal);
        Assert.Contains("companyMutationOutbox", script, StringComparison.Ordinal);
        Assert.Contains("portableOperatorSettings", script, StringComparison.Ordinal);
        Assert.DoesNotContain("${location.hostname}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("${window.location.host}", script, StringComparison.Ordinal);
    }

    private static TradeCompanyMutationRequest Mutation(string idempotencyKey) => new(
        Company,
        TradeCompanyRecordKinds.Order,
        "order-1",
        "{}",
        CompanyRecordRevision.None,
        new CompanyRevision(1),
        idempotencyKey);

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RecordingTransport(
        List<string> calls,
        TradeCompanyMutationResult? result) : ITradeCompanyClient
    {
        public Task<TradeCompanyIdentity?> GetCompanyAsync(
            CompanyId companyId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TradeCompanyIdentity?>(null);

        public Task<TradeCompanyChangeSet> GetChangesAsync(
            CompanyId companyId,
            CompanyRevision afterRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TradeCompanyMutationResult> MutateAsync(
            TradeCompanyMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("transport-mutate");
            return result is null
                ? Task.FromException<TradeCompanyMutationResult>(
                    new HttpRequestException("offline"))
                : Task.FromResult(result);
        }
    }

    private sealed class RecordingRuntime(
        List<string> calls,
        TradeCompanyMutationRequest request) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object value = identifier switch
            {
                "IndexedDB.enqueueTradeCompanyMutation" => Enqueue(),
                "IndexedDB.markTradeCompanyMutationAttempt" => Attempt(),
                "IndexedDB.completeTradeCompanyMutation" => Complete(),
                _ => throw new InvalidOperationException(
                    $"Unexpected JS invocation '{identifier}'.")
            };
            return ValueTask.FromResult((TValue)value);
        }

        private BrowserTradeCompanyMutationOutboxEntry Attempt()
        {
            calls.Add("browser-attempt");
            return new BrowserTradeCompanyMutationOutboxEntry(
                $"{Company}|{request.IdempotencyKey}",
                Company,
                "pending",
                request,
                DateTime.UnixEpoch,
                DateTime.UnixEpoch,
                1,
                null);
        }

        private BrowserTradeCompanyMutationOutboxEntry Enqueue()
        {
            calls.Add("browser-enqueue");
            return new BrowserTradeCompanyMutationOutboxEntry(
                $"{Company}|{request.IdempotencyKey}",
                Company,
                "pending",
                request,
                DateTime.UnixEpoch,
                DateTime.UnixEpoch,
                0,
                null);
        }

        private bool Complete()
        {
            calls.Add("browser-complete");
            return true;
        }
    }
}
