using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class AcquisitionDiagnosticSelectionService
{
    private AcquisitionDiagnosticSelection? _selection;

    public void SetSelection(
        DecisionRow row,
        DetailedShoppingPlan? resolvedMarketPlan,
        long planSessionVersion)
    {
        ArgumentNullException.ThrowIfNull(row);
        _selection = new AcquisitionDiagnosticSelection(row, resolvedMarketPlan, planSessionVersion);
    }

    public bool TryGetCurrent(long planSessionVersion, out AcquisitionDiagnosticSelection selection)
    {
        if (_selection == null || _selection.PlanSessionVersion != planSessionVersion)
        {
            selection = null!;
            return false;
        }

        selection = _selection;
        return true;
    }

    public void Clear() => _selection = null;
}

public sealed record AcquisitionDiagnosticSelection(
    DecisionRow Row,
    DetailedShoppingPlan? ResolvedMarketPlan,
    long PlanSessionVersion);
