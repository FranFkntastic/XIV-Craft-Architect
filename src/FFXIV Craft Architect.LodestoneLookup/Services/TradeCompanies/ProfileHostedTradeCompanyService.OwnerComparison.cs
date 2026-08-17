using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed partial class ProfileHostedTradeCompanyService
{
    public async Task<IReadOnlyDictionary<Guid, TradeCompanyRecordEnvelope>>
        LoadOrderRecordsAsync(
            TradeCompanyAccessContext access,
            IReadOnlyCollection<Guid> orderIds,
            CancellationToken cancellationToken = default)
    {
        var hostProfileId = RequireHostProfile(access);
        var ids = NormalizeOrderIds(orderIds);
        var records = await profiles.LoadProfileObjectsAsync(
            hostProfileId,
            ProfileSyncCollections.TradeOrders,
            ids.Select(id => id.ToString("D")).ToArray(),
            cancellationToken);
        var found = new Dictionary<Guid, TradeCompanyRecordEnvelope>();
        foreach (var hosted in records)
        {
            if (!Guid.TryParse(hosted.Object.ObjectId, out var orderId) ||
                hosted.Object.Deleted ||
                !PayloadBelongsToCompany(
                    access.CompanyId,
                    TradeCompanyRecordKinds.Order,
                    hosted.Object.PayloadJson))
            {
                continue;
            }

            found[orderId] = ToRecord(
                access.CompanyId,
                TradeCompanyRecordKinds.Order,
                hosted.Object.ObjectId,
                hosted.Object);
        }
        return found;
    }

    public async Task<IReadOnlyDictionary<Guid, ProfileSyncObjectEnvelope>>
        LoadGrantOrderMirrorsAsync(
            TradeCompanyAccessContext access,
            IReadOnlyCollection<Guid> orderIds,
            CancellationToken cancellationToken = default)
    {
        RequireHostProfile(access);
        var ids = NormalizeOrderIds(orderIds);
        var records = await profiles.LoadProfileObjectsAsync(
            access.GrantId.ToString("D"),
            ProfileSyncCollections.TradeOrders,
            ids.Select(id => id.ToString("D")).ToArray(),
            cancellationToken);
        return records
            .Where(record => Guid.TryParse(record.Object.ObjectId, out _))
            .ToDictionary(
                record => Guid.Parse(record.Object.ObjectId),
                record => record.Object);
    }

    private static Guid[] NormalizeOrderIds(IReadOnlyCollection<Guid> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        var ids = orderIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderIds),
                "A company owner comparison cannot exceed 50 orders.");
        }
        return ids;
    }
}
