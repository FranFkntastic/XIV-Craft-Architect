using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public interface IProfileSyncCollectionAdapter
{
    string Collection { get; }
    Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct);
    Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct);
    Task DeleteLocalObjectAsync(string objectId, CancellationToken ct);
}

public interface IProfileSyncSingleObjectAdapter
{
    Task<ProfileSyncObjectEnvelope?> LoadLocalObjectAsync(string objectId, CancellationToken ct);
}

public interface IHostedOrderProfileSyncAdapter
{
    Task ApplyRemoteDeletionAsync(
        Guid orderId,
        Guid companyProfileId,
        long revision,
        CancellationToken ct);
}

public enum ProfileSyncObjectReconciliation
{
    PromoteLocalAuthority,
    ProtectedConflict
}

public sealed class ProfileSyncObjectReconciliationException(
    string collection,
    string objectId,
    ProfileSyncObjectReconciliation reconciliation,
    string message) : InvalidOperationException(message)
{
    public string Collection { get; } = collection;
    public string ObjectId { get; } = objectId;
    public ProfileSyncObjectReconciliation Reconciliation { get; } = reconciliation;
}
