using System.Text.Json;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class NativePlanExportService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly StoredPlanSnapshotBuilder _snapshotBuilder;

    public NativePlanExportService(StoredPlanSnapshotBuilder snapshotBuilder)
    {
        _snapshotBuilder = snapshotBuilder;
    }

    public string GenerateNativeJson(string planId, string planName)
    {
        var snapshot = _snapshotBuilder.Build(
            planId,
            planName,
            includeSourcePlanIdentity: true);
        return JsonSerializer.Serialize(snapshot, ExportJsonOptions);
    }

    public static string CreateFileName(string? planName)
    {
        var baseName = string.IsNullOrWhiteSpace(planName) ? "plan" : planName;
        var fileName = $"{baseName}.craftplan";
        fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
        return fileName.Length > 100 ? fileName[..100] : fileName;
    }
}
