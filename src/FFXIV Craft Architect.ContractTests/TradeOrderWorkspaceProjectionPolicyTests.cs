using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Pages;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeOrderWorkspaceProjectionPolicyTests
{
    private static readonly Guid SelectedOrderId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void UnrelatedProjectionDoesNotTouchTheSelectedWorkspace()
    {
        var snapshot = CreateSnapshot(Guid.NewGuid());

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.Ignore,
            Decide(SelectedOrderId, selectedOrderIsCanonical: true, snapshot));
    }

    [Fact]
    public void CanonicalDeletionClearsTheUnavailableWorkspace()
    {
        var snapshot = CreateSnapshot(SelectedOrderId) with
        {
            Order = null,
            Deleted = true
        };

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.ClearUnavailableSelection,
            Decide(SelectedOrderId, selectedOrderIsCanonical: true, snapshot));
    }

    [Fact]
    public void LocalDeletionOnlyClearsItsHostedCollision()
    {
        var snapshot = CreateSnapshot(SelectedOrderId) with
        {
            Order = null,
            Deleted = true
        };

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.ClearLocalCollision,
            Decide(SelectedOrderId, selectedOrderIsCanonical: false, snapshot));
    }

    [Fact]
    public void DirtyLocalDraftRecordsTheHostedCollisionWithoutReplacingItsEditor()
    {
        var snapshot = CreateSnapshot(SelectedOrderId);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.RecordLocalCollision,
            Decide(
                SelectedOrderId,
                selectedOrderIsCanonical: false,
                snapshot,
                hasLocalDraftEditorChanges: true));
    }

    [Fact]
    public void CleanLocalDraftAcceptsTheHostedProjectionInPlace()
    {
        var snapshot = CreateSnapshot(SelectedOrderId);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.RefreshReadModel,
            Decide(
                SelectedOrderId,
                selectedOrderIsCanonical: false,
                snapshot));
    }

    [Fact]
    public void CleanLocalDraftUsesFullAdoptionForANewCanonicalAuthority()
    {
        var snapshot = CreateSnapshot(SelectedOrderId);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.AdoptHostedCanonicalWorkspace,
            Decide(
                SelectedOrderId,
                selectedOrderIsCanonical: false,
                snapshot,
                incomingOrderIsCanonical: true));
    }

    [Fact]
    public void InFlightLocalDraftDefersANewCanonicalAuthority()
    {
        var snapshot = CreateSnapshot(SelectedOrderId);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.PreserveWorkingState,
            Decide(
                SelectedOrderId,
                selectedOrderIsCanonical: false,
                snapshot,
                incomingOrderIsCanonical: true,
                ownsWorkingState: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OpenOrInFlightTermsRevisionRetainsWorkspaceOwnership(
        bool isEditingTermsRevision,
        bool isRepricing)
    {
        var snapshot = CreateSnapshot(SelectedOrderId, objectRevision: 12);
        var ownsWorkingState = TradeOrderWorkspaceProjectionPolicy.OwnsWorkingState(
            hasLocalDraftEditorChanges: false,
            isEditingCommissionTermsRevision: isEditingTermsRevision,
            isPlanMutationTransactionRunning: isRepricing,
            canEditCanonicalDraft: false,
            hasSelectedOrderOutputChanges: false,
            hasCanonicalDraftDetailChanges: false,
            selectedOrderPaymentTermsDirty: false,
            hasSelectedOrderDetailChanges: false);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.PreserveWorkingState,
            Decide(
                SelectedOrderId,
                selectedOrderIsCanonical: true,
                snapshot,
                ownsWorkingState: ownsWorkingState));
    }

    [Fact]
    public void IdleCompatibleProjectionRefreshesTheReadModelInPlace()
    {
        var snapshot = CreateSnapshot(SelectedOrderId, objectRevision: 12);

        Assert.Equal(
            TradeOrderWorkspaceProjectionAction.RefreshReadModel,
            Decide(SelectedOrderId, selectedOrderIsCanonical: true, snapshot));
    }

    [Theory]
    [InlineData(null, 12, true)]
    [InlineData(11, 12, true)]
    [InlineData(12, 12, false)]
    [InlineData(13, 12, false)]
    public void PeriodicRestoreOnlyReconcilesANewerUnappliedProjection(
        int? appliedObjectRevision,
        int availableObjectRevision,
        bool expected) =>
        Assert.Equal(
            expected,
            TradeOrderWorkspaceProjectionPolicy.ShouldApplyRestoreProjection(
                appliedObjectRevision.HasValue
                    ? appliedObjectRevision.Value
                    : null,
                availableObjectRevision));

    [Theory]
    [InlineData(true, 11, 12, false)]
    [InlineData(false, 11, 12, true)]
    [InlineData(false, 12, 12, false)]
    public void DeferredProjectionAppliesOnTheFirstIdleRender(
        bool ownsWorkingState,
        int appliedObjectRevision,
        int pendingObjectRevision,
        bool expected) =>
        Assert.Equal(
            expected,
            TradeOrderWorkspaceProjectionPolicy.CanApplyPendingProjection(
                ownsWorkingState,
                appliedObjectRevision,
                pendingObjectRevision));

    [Fact]
    public void CompatibleBackgroundProjectionReconciliationNeverRemountsTheOrderWorkspace()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.Restoration.cs"));
        var pageSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.razor.cs"));
        var collaborationSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.Collaboration.cs"));
        var projectionStart = source.IndexOf(
            "private void ApplyHostedOrderProjectionState",
            StringComparison.Ordinal);
        var projectionEnd = source.IndexOf(
            "private async Task ApplyHostedOrderProjectionReset",
            projectionStart,
            StringComparison.Ordinal);
        var restoreStart = source.IndexOf(
            "private async Task ApplyHostedOrderRestoreState",
            StringComparison.Ordinal);
        var restoreEnd = source.IndexOf(
            "private void ClearUnavailableSelectedOrder",
            restoreStart,
            StringComparison.Ordinal);
        var ownershipStart = pageSource.IndexOf(
            "private bool OwnsSelectedWorkspaceWorkingState",
            StringComparison.Ordinal);
        var ownershipEnd = pageSource.IndexOf(
            "private bool HasSelectedOrderDetailChanges",
            ownershipStart,
            StringComparison.Ordinal);
        var adoptionStart = source.IndexOf(
            "private void AdoptHostedCanonicalWorkspace",
            StringComparison.Ordinal);
        var adoptionEnd = source.IndexOf(
            "private async Task ApplyHostedOrderRestoreState",
            adoptionStart,
            StringComparison.Ordinal);

        Assert.True(projectionStart >= 0 && projectionEnd > projectionStart);
        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart);
        Assert.True(ownershipStart >= 0 && ownershipEnd > ownershipStart);
        Assert.True(adoptionStart >= 0 && adoptionEnd > adoptionStart);
        var projectionBoundary = source[projectionStart..projectionEnd];
        var restoreBoundary = source[restoreStart..restoreEnd];
        var ownershipBoundary = pageSource[ownershipStart..ownershipEnd];
        var adoptionBoundary = source[adoptionStart..adoptionEnd];

        Assert.DoesNotContain("SelectOrder(", projectionBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectOrder(", restoreBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareCompanyCommissionEditor", projectionBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareCompanyCommissionEditor", restoreBoundary, StringComparison.Ordinal);
        Assert.Contains("RefreshSelectedOrderReadModel", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("AdoptHostedCanonicalWorkspace", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("!HasSelectedOrderDetailChanges()", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("!HasSelectedOrderOutputChanges", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("!_selectedOrderPaymentTermsDirty", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("!HasCanonicalDraftDetailChanges", projectionBoundary, StringComparison.Ordinal);
        Assert.Contains("IsEditingCommissionTermsRevision", ownershipBoundary, StringComparison.Ordinal);
        Assert.Contains("IsPlanMutationTransactionRunning", ownershipBoundary, StringComparison.Ordinal);
        Assert.Contains("SelectOrder(order)", adoptionBoundary, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingSelectedOrderProjectionIfIdle", pageSource, StringComparison.Ordinal);
        Assert.Contains(
            "_selectedOrder?.CompanyCommission is { } commission",
            collaborationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingDeviceOnlyCopyDetachesTheLinkedCanonicalView()
    {
        var selectionSource = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(),
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.Selection.cs"));
        var selectionStart = selectionSource.IndexOf(
            "private void SelectOrder",
            StringComparison.Ordinal);
        var selectionEnd = selectionSource.IndexOf(
            "private void ScheduleSelectedCommissionOwnerRefresh",
            selectionStart,
            StringComparison.Ordinal);

        Assert.True(selectionStart >= 0 && selectionEnd > selectionStart);
        var selectionBoundary = selectionSource[selectionStart..selectionEnd];
        Assert.Contains("order.CompanyCommission == null", selectionBoundary, StringComparison.Ordinal);
        Assert.Contains(
            "CommissionOperations.DismissLinkedProjectionForLocalOrder(order.Id)",
            selectionBoundary,
            StringComparison.Ordinal);
    }

    private static TradeOrderWorkspaceProjectionAction Decide(
        Guid? selectedOrderId,
        bool selectedOrderIsCanonical,
        HostedOrderProjectionSnapshot snapshot,
        bool incomingOrderIsCanonical = false,
        bool hasLocalDraftEditorChanges = false,
        bool ownsWorkingState = false) =>
        TradeOrderWorkspaceProjectionPolicy.Decide(
            selectedOrderId,
            selectedOrderIsCanonical,
            snapshot,
            incomingOrderIsCanonical,
            hasLocalDraftEditorChanges,
            ownsWorkingState);

    private static HostedOrderProjectionSnapshot CreateSnapshot(
        Guid orderId,
        long objectRevision = 11)
    {
        var order = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Title = "Spruce Lumber x1,998"
        };
        return new HostedOrderProjectionSnapshot(
            orderId,
            order.CompanyProfileId,
            objectRevision,
            CompanyRevision: 4,
            order,
            OwnerProjection: null,
            Deleted: false);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
