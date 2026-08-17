using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed partial class HostedCompanyCommissionService
{
    public async Task<CompanyCommissionOwnerComparisonResponse> CompareOwnersAsync(
        TradeCompanyAccessContext access,
        CompanyCommissionOwnerComparisonRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireCompanyOperator(access);
        ArgumentNullException.ThrowIfNull(request);
        var requested = request.Items.ToArray();
        if (requested.Length is < 1 or > 50 ||
            requested.Any(item => item.OrderId == Guid.Empty || item.CommissionId == Guid.Empty) ||
            requested.Select(item => item.OrderId).Distinct().Count() != requested.Length)
        {
            throw new ArgumentException(
                "Owner comparison requires between one and 50 unique order identities.",
                nameof(request));
        }

        var orderIds = requested.Select(item => item.OrderId).ToArray();
        var companyRevision = await companies.LoadCompanyRevisionAsync(
            access,
            cancellationToken);
        var canonical = await companies.LoadOrderRecordsAsync(
            access,
            orderIds,
            cancellationToken);
        var mirrors = await companies.LoadGrantOrderMirrorsAsync(
            access,
            orderIds,
            cancellationToken);
        var verifiedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var results = new List<CompanyCommissionOwnerComparisonResult>(requested.Length);
        foreach (var item in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!canonical.TryGetValue(item.OrderId, out var record))
            {
                results.Add(Missing(item));
                continue;
            }

            TradeOrder order;
            try
            {
                order = DeserializeCanonicalOrder(record, access.CompanyId, item.CommissionId);
            }
            catch (InvalidOperationException)
            {
                results.Add(Missing(item));
                continue;
            }
            if (order.Id != item.OrderId ||
                order.CompanyCommission?.CommissionId != item.CommissionId)
            {
                results.Add(Missing(item));
                continue;
            }

            var mirrorMatches = mirrors.TryGetValue(item.OrderId, out var mirror) &&
                !mirror.Deleted &&
                string.Equals(
                    mirror.PayloadJson,
                    record.PayloadJson,
                    StringComparison.Ordinal);
            var profileObjectRevision = mirrorMatches
                ? new CompanyRecordRevision(mirror!.Revision)
                : await companies.MirrorOrderToGrantAsync(
                    access,
                    order,
                    cancellationToken);
            var receipt = new CompanyCommissionOwnerReceipt
            {
                OrderId = order.Id,
                CompanyId = access.CompanyId,
                CommissionId = item.CommissionId,
                ProfileObjectRevision = profileObjectRevision,
                ObjectRevision = record.RecordRevision,
                CompanyRevision = companyRevision,
                VerifiedAtUtc = verifiedAtUtc
            };
            var unchanged = mirrorMatches &&
                item.ProfileObjectRevision == profileObjectRevision &&
                (item.ObjectRevision == CompanyRecordRevision.None ||
                 item.ObjectRevision == record.RecordRevision) &&
                (item.CompanyRevision == CompanyRecordRevision.None ||
                 item.CompanyRevision == companyRevision);
            results.Add(new CompanyCommissionOwnerComparisonResult
            {
                OrderId = order.Id,
                CommissionId = item.CommissionId,
                Status = unchanged
                    ? CompanyCommissionOwnerComparisonStatus.Unchanged
                    : CompanyCommissionOwnerComparisonStatus.Changed,
                Receipt = receipt,
                Projection = unchanged
                    ? null
                    : new CompanyCommissionOwnerProjection
                    {
                        Order = order,
                        ObjectRevision = record.RecordRevision,
                        CompanyRevision = companyRevision,
                        ProfileObjectRevision = profileObjectRevision
                    }
            });
        }

        return new CompanyCommissionOwnerComparisonResponse
        {
            CompanyId = access.CompanyId,
            CompanyRevision = companyRevision,
            VerifiedAtUtc = verifiedAtUtc,
            Items = results
        };
    }

    private static CompanyCommissionOwnerComparisonResult Missing(
        CompanyCommissionOwnerComparisonItem item) =>
        new()
        {
            OrderId = item.OrderId,
            CommissionId = item.CommissionId,
            Status = CompanyCommissionOwnerComparisonStatus.Missing
        };
}
