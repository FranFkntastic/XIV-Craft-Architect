using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeCompanyContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CompanyIdentity_UsesStableStringIdAndNumericRevisions()
    {
        var companyId = new CompanyId(Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a1111"));
        var identity = new TradeCompanyIdentity(
            companyId,
            "The Studium",
            new CompanyRevision(7),
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddMinutes(1));

        var json = JsonSerializer.Serialize(identity, JsonOptions);
        var restored = JsonSerializer.Deserialize<TradeCompanyIdentity>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(companyId, restored.CompanyId);
        Assert.Equal(7, restored.Revision.Value);
        Assert.Contains("\"companyId\":\"018fdc85-9b7a-7c31-87ed-6f9bdb4a1111\"", json);
        Assert.Contains("\"revision\":7", json);
    }

    [Fact]
    public void CompanyIdentity_RejectsEmptyOrNegativeValues()
    {
        Assert.Throws<ArgumentException>(() => new CompanyId(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanyRevision(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanyRecordRevision(-1));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CompanyId>("\"\""));
    }

    [Fact]
    public void PublicationOwnership_IsCompanyAndOrderScoped()
    {
        var ownership = new TradeCompanyPublicationOwnership(
            new CompanyId(Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a2222")),
            Guid.Parse("cc58c224-d6e6-402b-bcdd-e7b45dd00b41"),
            new CompanyRecordRevision(12));
        var request = new CommissionBriefCreateRequest
        {
            Brief = new CommissionBriefDocument { CompanyName = "The Studium", Title = "Raid gear" },
            Ownership = ownership
        };

        var restored = JsonSerializer.Deserialize<CommissionBriefCreateRequest>(
            JsonSerializer.Serialize(request, JsonOptions),
            JsonOptions);

        Assert.NotNull(restored?.Ownership);
        Assert.Equal(ownership.CompanyId, restored.Ownership.CompanyId);
        Assert.Equal(ownership.OrderId, restored.Ownership.OrderId);
        Assert.Equal(12, restored.Ownership.OrderRevision.Value);
    }

    [Fact]
    public void CrossLayerInterfaces_ShareTheCanonicalContracts()
    {
        Assert.Equal(typeof(Task<TradeCompanyMutationResult>), GetReturnType<ITradeCompanyClient>("MutateAsync"));
        Assert.Equal(typeof(Task<TradeCompanyMutationResult>), GetReturnType<ITradeCompanyService>("MutateAsync"));
        Assert.Equal(typeof(Task<TradeCompanyMutationResult>), GetReturnType<ITradeCompanyStore>("ApplyMutationAsync"));
        Assert.Equal(typeof(Task<TradeCompanyPublicationOwnership?>),
            GetReturnType<ITradeCompanyService>("ResolvePublicationOwnershipAsync"));
    }

    private static Type GetReturnType<T>(string methodName) =>
        typeof(T).GetMethod(methodName)?.ReturnType
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name}.{methodName}.");
}
