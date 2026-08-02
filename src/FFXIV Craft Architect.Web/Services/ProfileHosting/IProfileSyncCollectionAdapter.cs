using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public interface IProfileSyncCollectionAdapter
{
    string Collection { get; }
    Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct);
    Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct);
    Task DeleteLocalObjectAsync(string objectId, CancellationToken ct);
}

public interface IHostedOrderProfileSyncAdapter
{
    Task ApplyRemoteDeletionAsync(
        Guid orderId,
        Guid companyProfileId,
        long revision,
        CancellationToken ct);
}
