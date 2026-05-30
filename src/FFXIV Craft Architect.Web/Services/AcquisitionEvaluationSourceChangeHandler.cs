using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class AcquisitionEvaluationSourceChangeHandler
{
    public static void Apply(AppState appState, PlanNode node, AcquisitionSource source)
    {
        new AcquisitionDecisionService(appState).ChangeSource(node, source);
    }
}
