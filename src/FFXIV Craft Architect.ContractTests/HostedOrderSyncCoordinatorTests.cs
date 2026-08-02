using System.Reflection;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderSyncCoordinatorTests
{
    [Theory]
    [InlineData(OwnerProjectionScenario.AdoptionRequired)]
    [InlineData(OwnerProjectionScenario.AdoptionForbidden)]
    [InlineData(OwnerProjectionScenario.ValidProjection)]
    [InlineData(OwnerProjectionScenario.InvalidProjection)]
    public void OwnerProjectionAdoptionPreservesCanonicalIdentity(
        OwnerProjectionScenario scenario)
    {
        switch (scenario)
        {
            case OwnerProjectionScenario.AdoptionRequired:
                MissingOrStaleOwnerProjectionRequiresAdoption();
                TabReplayUsesOwnCursorWithoutRegressingSharedCursor();
                break;
            case OwnerProjectionScenario.AdoptionForbidden:
                DeletedAndNonCommissionOrdersNeverRequireAdoption();
                break;
            case OwnerProjectionScenario.ValidProjection:
                MatchingProjectionAtCurrentOrNewerRevisionIsAccepted();
                break;
            case OwnerProjectionScenario.InvalidProjection:
                StaleOrWrongIdentityProjectionIsRejected();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void MissingOrStaleOwnerProjectionRequiresAdoption()
    {
        var order = CreateCommissionOrder();
        var missing = Snapshot(order, objectRevision: 5, owner: null);
        var stale = Snapshot(order, objectRevision: 5, owner: Projection(order, 4, 8));
        var current = Snapshot(order, objectRevision: 5, owner: Projection(order, 5, 8));

        Assert.True(NeedsOwnerAdoption(missing));
        Assert.True(NeedsOwnerAdoption(stale));
        Assert.False(NeedsOwnerAdoption(current));
    }

    private static void DeletedAndNonCommissionOrdersNeverRequireAdoption()
    {
        var commissionOrder = CreateCommissionOrder();
        var ordinaryOrder = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = "Ordinary order"
        };

        Assert.False(NeedsOwnerAdoption(
            Snapshot(commissionOrder, 5, null) with { Deleted = true }));
        Assert.False(NeedsOwnerAdoption(
            Snapshot(ordinaryOrder, 5, null)));
    }

    private static void MatchingProjectionAtCurrentOrNewerRevisionIsAccepted()
    {
        var order = CreateCommissionOrder();
        var expected = Snapshot(order, objectRevision: 5, owner: null);

        ValidateOwnerProjection(
            expected,
            Projection(order, objectRevision: 5, companyRevision: 8));
        ValidateOwnerProjection(
            expected,
            Projection(order, objectRevision: 6, companyRevision: 9));
    }

    private static void StaleOrWrongIdentityProjectionIsRejected()
    {
        var order = CreateCommissionOrder();
        var expected = Snapshot(order, objectRevision: 5, owner: null);
        var wrongOrder = CreateCommissionOrder(
            orderId: Guid.NewGuid(),
            companyProfileId: order.CompanyProfileId,
            companyId: order.CompanyCommission!.CompanyId,
            commissionId: order.CompanyCommission.CommissionId);
        var wrongCommission = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: order.CompanyProfileId,
            companyId: order.CompanyCommission.CompanyId,
            commissionId: Guid.NewGuid());
        var wrongProfile = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: Guid.NewGuid(),
            companyId: order.CompanyCommission.CompanyId,
            commissionId: order.CompanyCommission.CommissionId);
        var wrongCompany = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: order.CompanyProfileId,
            companyId: new CompanyId(Guid.NewGuid()),
            commissionId: order.CompanyCommission.CommissionId);

        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(order, objectRevision: 4, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongOrder, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongCommission, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongProfile, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongCompany, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(order, objectRevision: 5, companyRevision: 0)));
    }

    private static void TabReplayUsesOwnCursorWithoutRegressingSharedCursor()
    {
        Assert.Equal(405L, ResolveSyncStartRevision(446, 405));
        Assert.Equal(400L, ResolveSyncStartRevision(400, 405));
        Assert.Equal(446L, ResolveSyncStartRevision(446, null));
        Assert.False(ShouldAdvancePersistedRevision(446, 405));
        Assert.False(ShouldAdvancePersistedRevision(446, 446));
        Assert.True(ShouldAdvancePersistedRevision(446, 447));
    }

    private static bool NeedsOwnerAdoption(HostedOrderProjectionSnapshot snapshot) =>
        (bool)InvokePolicy(nameof(NeedsOwnerAdoption), snapshot)!;

    private static void ValidateOwnerProjection(
        HostedOrderProjectionSnapshot expected,
        CompanyCommissionOwnerProjection projection) =>
        InvokePolicy(nameof(ValidateOwnerProjection), expected, projection);

    private static long ResolveSyncStartRevision(
        long persistedRevision,
        long? replayAfterRevision) =>
        (long)InvokeSyncPolicy(
            nameof(ResolveSyncStartRevision),
            persistedRevision,
            replayAfterRevision)!;

    private static bool ShouldAdvancePersistedRevision(
        long persistedRevision,
        long candidateRevision) =>
        (bool)InvokeSyncPolicy(
            nameof(ShouldAdvancePersistedRevision),
            persistedRevision,
            candidateRevision)!;

    private static object? InvokePolicy(string name, params object[] arguments)
    {
        var method = typeof(HostedOrderSyncCoordinator).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(HostedOrderSyncCoordinator).FullName, name);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private static object? InvokeSyncPolicy(string name, params object?[] arguments)
    {
        var method = typeof(ProfileSyncService).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ProfileSyncService).FullName, name);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private static HostedOrderProjectionSnapshot Snapshot(
        TradeOrder order,
        long objectRevision,
        CompanyCommissionOwnerProjection? owner) =>
        new(
            order.Id,
            order.CompanyProfileId,
            objectRevision,
            owner?.CompanyRevision.Value,
            order,
            owner,
            Deleted: false);

    private static CompanyCommissionOwnerProjection Projection(
        TradeOrder order,
        long objectRevision,
        long companyRevision) =>
        new()
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(objectRevision),
            CompanyRevision = new CompanyRecordRevision(companyRevision)
        };

    private static TradeOrder CreateCommissionOrder(
        Guid? orderId = null,
        Guid? companyProfileId = null,
        CompanyId? companyId = null,
        Guid? commissionId = null)
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        return new TradeOrder
        {
            Id = orderId ?? Guid.NewGuid(),
            CompanyProfileId = companyProfileId ?? Guid.NewGuid(),
            Title = "Canonical commission",
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = commissionId ?? Guid.NewGuid(),
                CompanyId = companyId ?? new CompanyId(Guid.NewGuid()),
                CommissionerActorId = "commissioner",
                Reference = "TEST-001",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CurrentTermsVersion = 1,
                PublicMetadata = new CompanyCommissionPublicMetadata
                {
                    PublicBriefId = "test-001",
                    ViewState = CompanyCommissionPublicViewState.Published
                },
                ActiveClaimCapabilityRevision = 1,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(CompanyCommissionClearanceState.NotRequired),
                    new CompanyCommissionPaymentClearance(CompanyCommissionClearanceState.NotRequired),
                    new CompanyCommissionMaterialClearance(
                        CompanyCommissionClearanceState.NotRequired,
                        [])),
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }

    public enum OwnerProjectionScenario
    {
        AdoptionRequired,
        AdoptionForbidden,
        ValidProjection,
        InvalidProjection
    }
}
