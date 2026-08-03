using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileSyncAuthorityTests
{
    private const string CanonicalHost = "https://xivcraftarchitect.com/api/";
    private const string DevelopmentHost = "https://dev.xivcraftarchitect.com/api/";

    [Fact]
    public async Task LegacyStateMigratesOnceUnderExactAuthorityPath()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var pending = new[] { new ProfileSyncPendingSave(ProfileSyncCollections.TradeOrders, "order-1") };
        var runtime = new SettingsJsRuntime(Settings(
            "https://Example.com/api/A/",
            profileId,
            ("profileHost.profile." + profileId + ".pendingSaves", JsonSerializer.Serialize(pending)),
            ("profileHost.profile." + profileId + ".lastSyncRevision", JsonSerializer.Serialize(42L))));
        var state = State(runtime, "https://example.com/api/A/");

        await state.LoadConnectionSettingsAsync();
        var firstKeys = runtime.Settings.Keys
            .Where(key => key.StartsWith("profileHost.authority.", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        await state.LoadConnectionSettingsAsync();

        Assert.Equal(1, runtime.BatchSaveCount);
        Assert.Equal(2, firstKeys.Length);
        Assert.All(firstKeys, key => Assert.Contains("%2Fapi%2FA%2F.profile.", key));
        Assert.Contains(firstKeys, key => key.EndsWith(".pendingSaves", StringComparison.Ordinal));
        Assert.Contains(firstKeys, key => key.EndsWith(".lastSyncRevision", StringComparison.Ordinal));

        var upper = new HostedProfileConnectionSettings
        {
            HostUrl = "https://example.com/api/A/",
            AccessKey = "key",
            ConnectedProfileId = profileId
        };
        var lower = new HostedProfileConnectionSettings
        {
            HostUrl = "https://example.com/api/a/",
            AccessKey = "key",
            ConnectedProfileId = profileId
        };
        Assert.NotEqual(upper.ConnectionScopeId, lower.ConnectionScopeId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task CanonicalAdoptionIsAuthenticatedAndAdoptsReturnedIdentity(
        HttpStatusCode statusCode,
        bool shouldAdopt)
    {
        var stagingProfileId = Guid.NewGuid().ToString("D");
        var canonicalProfileId = Guid.NewGuid().ToString("D");
        var runtime = new SettingsJsRuntime(Settings(DevelopmentHost, stagingProfileId));
        var options = new ProfileHostClientOptions(CanonicalHost);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(indexedDb, options);
        var client = new ProfileHostClient(
            new HttpClient(new ProfileResponseHandler(statusCode, canonicalProfileId)),
            options);
        var sync = new ProfileSyncService(
            client,
            localState,
            new WebSettingsService(indexedDb),
            new HostedOrderProjectionStore(),
            Array.Empty<IProfileSyncCollectionAdapter>());

        await sync.PrepareAuthorityAsync();
        var settings = await localState.LoadConnectionSettingsAsync();

        Assert.Equal(shouldAdopt ? CanonicalHost : DevelopmentHost, settings.HostUrl);
        Assert.Equal(shouldAdopt ? canonicalProfileId : stagingProfileId, settings.ProfileScopeId);
        Assert.Equal(shouldAdopt ? "Canonical profile" : "Staging profile", settings.ConnectedProfileName);
    }

    [Fact]
    public void CommissionedLocalResidueRemainsVisibleButOutsideCanonicalOrders()
    {
        var companyId = Guid.NewGuid();
        var hostedOrder = new TradeOrder { Id = Guid.NewGuid(), CompanyProfileId = companyId };
        var localResidue = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyId,
            CompanyCommission = (TradeCompanyCommission)RuntimeHelpers.GetUninitializedObject(
                typeof(TradeCompanyCommission))
        };
        var hostedSnapshot = new HostedOrderProjectionSnapshot(
            hostedOrder.Id,
            companyId,
            1,
            1,
            hostedOrder,
            null,
            Deleted: false);

        var deviceOnly = TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders(
            [hostedOrder, localResidue],
            [hostedSnapshot]);

        Assert.Collection(deviceOnly, order => Assert.Equal(localResidue.Id, order.Id));
        Assert.True(TradeOrderWorkspaceCompositionPolicy.IsHostedOrder(
            hostedOrder.Id,
            companyId,
            [hostedSnapshot]));
        Assert.False(TradeOrderWorkspaceCompositionPolicy.IsHostedOrder(
            localResidue.Id,
            companyId,
            [hostedSnapshot]));
    }

    private static ProfileSyncLocalStateService State(
        SettingsJsRuntime runtime,
        string defaultHost) =>
        new(new IndexedDbService(runtime), new ProfileHostClientOptions(defaultHost));

    private static Dictionary<string, string> Settings(
        string host,
        string profileId,
        params (string Key, string Value)[] additional)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId),
            ["profileHost.connectedProfileName"] = JsonSerializer.Serialize("Staging profile")
        };
        foreach (var (key, value) in additional)
        {
            settings[key] = value;
        }
        return settings;
    }

    private sealed class ProfileResponseHandler(
        HttpStatusCode statusCode,
        string canonicalProfileId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(new Uri("https://xivcraftarchitect.com/api/profile-host/profile"), request.RequestUri);
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
            {
                response.Content = JsonContent.Create(new ProfileHostProfileResponse
                {
                    ProfileId = canonicalProfileId,
                    DisplayName = "Canonical profile",
                    ServerRevision = 7
                });
            }
            return Task.FromResult(response);
        }
    }

    private sealed class SettingsJsRuntime(
        Dictionary<string, string> settings) : IJSRuntime
    {
        public Dictionary<string, string> Settings { get; } = settings;
        public int BatchSaveCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(Settings, StringComparer.Ordinal),
                "IndexedDB.loadSetting" => Settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        private bool SaveBatch(Dictionary<string, string> values)
        {
            BatchSaveCount++;
            foreach (var (key, value) in values)
            {
                Settings[key] = value;
            }
            return true;
        }

        private bool SaveSetting(string key, string value)
        {
            Settings[key] = value;
            return true;
        }
    }
}
