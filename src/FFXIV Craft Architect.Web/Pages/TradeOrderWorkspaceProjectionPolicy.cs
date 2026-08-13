using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Pages;

public enum TradeOrderWorkspaceProjectionAction
{
    Ignore,
    ClearUnavailableSelection,
    ClearLocalCollision,
    RecordLocalCollision,
    AdoptHostedCanonicalWorkspace,
    PreserveWorkingState,
    RefreshReadModel
}

public static class TradeOrderWorkspaceProjectionPolicy
{
    public static bool OwnsWorkingState(
        bool hasLocalDraftEditorChanges,
        bool isEditingCommissionTermsRevision,
        bool isPlanMutationTransactionRunning,
        bool canEditCanonicalDraft,
        bool hasSelectedOrderOutputChanges,
        bool hasCanonicalDraftDetailChanges,
        bool selectedOrderPaymentTermsDirty,
        bool hasSelectedOrderDetailChanges)
    {
        if (hasLocalDraftEditorChanges ||
            isEditingCommissionTermsRevision ||
            isPlanMutationTransactionRunning)
        {
            return true;
        }

        return canEditCanonicalDraft &&
               (hasSelectedOrderOutputChanges ||
                hasCanonicalDraftDetailChanges ||
                selectedOrderPaymentTermsDirty ||
                hasSelectedOrderDetailChanges);
    }

    public static bool ShouldApplyRestoreProjection(
        long? appliedObjectRevision,
        long availableObjectRevision) =>
        !appliedObjectRevision.HasValue ||
        availableObjectRevision > appliedObjectRevision.Value;

    public static bool CanApplyPendingProjection(
        bool ownsWorkingState,
        long? appliedObjectRevision,
        long pendingObjectRevision) =>
        !ownsWorkingState &&
        ShouldApplyRestoreProjection(
            appliedObjectRevision,
            pendingObjectRevision);

    public static TradeOrderWorkspaceProjectionAction Decide(
        Guid? selectedOrderId,
        bool selectedOrderIsCanonical,
        HostedOrderProjectionSnapshot snapshot,
        bool incomingOrderIsCanonical,
        bool hasLocalDraftEditorChanges,
        bool ownsWorkingState)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!selectedOrderId.HasValue || selectedOrderId.Value != snapshot.OrderId)
        {
            return TradeOrderWorkspaceProjectionAction.Ignore;
        }

        if (snapshot.Deleted || snapshot.Order == null)
        {
            return selectedOrderIsCanonical
                ? TradeOrderWorkspaceProjectionAction.ClearUnavailableSelection
                : TradeOrderWorkspaceProjectionAction.ClearLocalCollision;
        }

        if (!selectedOrderIsCanonical)
        {
            if (hasLocalDraftEditorChanges)
            {
                return TradeOrderWorkspaceProjectionAction.RecordLocalCollision;
            }
        }

        if (ownsWorkingState)
        {
            return TradeOrderWorkspaceProjectionAction.PreserveWorkingState;
        }

        if (!selectedOrderIsCanonical)
        {
            if (incomingOrderIsCanonical)
            {
                return TradeOrderWorkspaceProjectionAction.AdoptHostedCanonicalWorkspace;
            }
        }

        return TradeOrderWorkspaceProjectionAction.RefreshReadModel;
    }
}
