using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public static class TradeOrderArchiveSummaryProjector
{
    public static ProfileSyncObjectEnvelope Apply(ProfileSyncObjectEnvelope envelope)
    {
        if (envelope.Deleted ||
            !string.Equals(
                envelope.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
        {
            return envelope;
        }

        var summary = TradeOrderArchiveSummaryCodec.TryCreate(
            envelope.PayloadJson,
            envelope.ObjectId);
        if (summary == null)
        {
            return envelope;
        }

        return new ProfileSyncObjectEnvelope
        {
            Collection = envelope.Collection,
            ObjectId = envelope.ObjectId,
            PayloadJson = string.Empty,
            SummaryJson = TradeOrderArchiveSummaryCodec.Serialize(summary),
            Revision = envelope.Revision,
            UpdatedAtUtc = envelope.UpdatedAtUtc
        };
    }
}
