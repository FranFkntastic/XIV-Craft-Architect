using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record AcquisitionSourceChangeRequest(PlanNode Node, AcquisitionSource Source);

public sealed record PlanNodeHqChangeRequest(PlanNode Node, bool MustBeHq);
