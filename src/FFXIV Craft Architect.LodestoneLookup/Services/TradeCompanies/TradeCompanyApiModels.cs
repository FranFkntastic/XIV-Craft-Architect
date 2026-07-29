using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record TradeCompanyMetaResponse(
    string Service,
    string EnvironmentId,
    bool Enabled,
    int MinimumProtocolVersion,
    int CurrentProtocolVersion,
    int SchemaVersion);

public sealed class TradeCompanyCreateRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = TradeCompanyProtocol.CurrentVersion;
}

public sealed record TradeCompanyProvisionResponse(
    TradeCompanyIdentity Company,
    TradeCompanyGrantRecord OwnerGrant,
    string AccessKey);

public sealed record TradeCompanySessionResponse(
    TradeCompanyIdentity Company,
    TradeCompanyAccessContext Access);

public sealed record TradeCompanyGrantRecord(
    Guid GrantId,
    CompanyId CompanyId,
    TradeCompanyRole Role,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime? RevokedAtUtc);

public sealed class TradeCompanyGrantCreateRequest
{
    public TradeCompanyRole Role { get; set; } = TradeCompanyRole.ReadOnly;
    public int ProtocolVersion { get; set; } = TradeCompanyProtocol.CurrentVersion;
}

public sealed record TradeCompanyGrantCreateResponse(
    TradeCompanyGrantRecord Grant,
    string AccessKey);

public sealed class TradeCompanyRecordPutRequest
{
    public string PayloadJson { get; set; } = "{}";
    public CompanyRecordRevision ExpectedRecordRevision { get; set; } = CompanyRecordRevision.None;
    public CompanyRevision ExpectedCompanyRevision { get; set; } = CompanyRevision.None;
    public string IdempotencyKey { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = TradeCompanyProtocol.CurrentVersion;
}

public sealed record TradeCompanyProblem(
    string Code,
    string Message,
    int MinimumProtocolVersion = TradeCompanyProtocol.MinimumSupportedVersion,
    int CurrentProtocolVersion = TradeCompanyProtocol.CurrentVersion);

public sealed record ProvisionedTradeCompany(
    TradeCompanyIdentity Company,
    TradeCompanyGrantRecord OwnerGrant);

public enum TradeCompanyGrantRevokeStatus
{
    Revoked,
    NotFound,
    LastOwner
}
