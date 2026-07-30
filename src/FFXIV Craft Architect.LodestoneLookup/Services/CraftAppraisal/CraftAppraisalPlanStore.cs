using System.Security.Cryptography;
using System.Text;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CraftAppraisal;

public sealed class CraftAppraisalPlanStore(CraftAppraisalApiOptions options)
{
    public async Task<string> SaveAsync(string planJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        Directory.CreateDirectory(options.PlanDirectory);

        var planId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(planJson))).ToLowerInvariant();
        var destination = GetPath(planId);
        if (File.Exists(destination))
            return planId;

        var temporary = Path.Combine(
            options.PlanDirectory,
            $".{planId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, planJson, cancellationToken);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another coalesced request published the same content first.
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return planId;
    }

    public async Task<string?> ReadAsync(string planId, CancellationToken cancellationToken)
    {
        if (planId.Length != 64 || !planId.All(Uri.IsHexDigit))
            return null;

        var path = GetPath(planId.ToLowerInvariant());
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
    }

    private string GetPath(string planId) =>
        Path.Combine(options.PlanDirectory, $"{planId}.craftplan");
}
