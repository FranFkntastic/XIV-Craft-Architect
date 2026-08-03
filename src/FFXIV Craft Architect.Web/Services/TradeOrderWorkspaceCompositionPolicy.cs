using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeOrderWorkspaceCompositionPolicy
{
    public static bool IsHostedOrder(
        Guid orderId,
        Guid companyProfileId,
        IEnumerable<HostedOrderProjectionSnapshot> hostedOrders)
    {
        ArgumentNullException.ThrowIfNull(hostedOrders);
        return hostedOrders.Any(snapshot =>
            snapshot.OrderId == orderId &&
            snapshot.CompanyProfileId == companyProfileId &&
            !snapshot.Deleted &&
            snapshot.Order != null);
    }

    public static IReadOnlyList<TradeOrder> GetDeviceOnlyOrders(
        IEnumerable<TradeOrder> localOrders,
        IEnumerable<HostedOrderProjectionSnapshot> hostedOrders)
    {
        ArgumentNullException.ThrowIfNull(localOrders);
        ArgumentNullException.ThrowIfNull(hostedOrders);

        var hostedOrderIds = hostedOrders
            .Where(snapshot => !snapshot.Deleted && snapshot.Order != null)
            .Select(snapshot => snapshot.OrderId)
            .ToHashSet();
        return localOrders
            .Where(order => !hostedOrderIds.Contains(order.Id))
            .OrderByDescending(order => order.CommissionedAtUtc)
            .ToArray();
    }
}
