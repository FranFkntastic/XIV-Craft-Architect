using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CommissionTermChange(string Term, string Before, string After);
public sealed record CommissionPaymentChange(string Component, decimal Before, decimal After)
{
    public decimal Delta => After - Before;
}
public sealed record CommissionTermsComparison(int BeforeVersion, int AfterVersion, bool Available,
    string? UnavailableReason, IReadOnlyList<CommissionTermChange> Changes,
    IReadOnlyList<CommissionPaymentChange> Payment, bool EvidenceChanged)
{
    public decimal? PaymentDelta => Available ? Payment.Single(p => p.Component == "Total").Delta : null;
    public bool ComponentsReconcile => !Available ||
        (Payment.Where(p => p.Component != "Total").Sum(p => p.Before) == Payment.Single(p => p.Component == "Total").Before &&
         Payment.Where(p => p.Component != "Total").Sum(p => p.After) == Payment.Single(p => p.Component == "Total").After);
}

/// <summary>Compares recorded terms only; never resolves prices or infers transfers.</summary>
public static class CompanyCommissionTermsComparison
{
    public static CommissionTermsComparison Between(IReadOnlyList<CompanyCommissionTermsVersion> versions,
        int before, int after) => Between(Unique(versions, before), Unique(versions, after), before, after);

    private static CompanyCommissionTermsVersion? Unique(IReadOnlyList<CompanyCommissionTermsVersion> versions, int version) =>
        versions.Count(v => v.Version == version) == 1 ? versions.Single(v => v.Version == version) : null;

    public static CommissionTermsComparison Between(CompanyCommissionTermsVersion? before,
        CompanyCommissionTermsVersion? after, int beforeVersion, int afterVersion)
    {
        if (before == null || after == null || before.Version != beforeVersion || after.Version != afterVersion)
        {
            return new(beforeVersion, afterVersion, false, "Comparison unavailable: a recorded version is missing or ambiguous.", [], [], false);
        }

        var changes = new List<CommissionTermChange>();
        void Add(string field, string? a, string? b)
        {
            if (!string.Equals(a ?? "", b ?? "", StringComparison.Ordinal))
            {
                changes.Add(new(field, a ?? "", b ?? ""));
            }
        }
        // Match stable IDs first, then item+quality only when unique. Never attribute by display name.
        Match(before.Outputs, after.Outputs, x => x.LineId, x => x.ItemId, x => x.MustBeHq,
            x => x.Name, x => $"{Number(x.RequiredQuantity)} × {(x.MustBeHq ? "HQ required" : "NQ accepted")}", "Output", Add);
        Match(before.Materials, after.Materials, x => x.LineId, x => x.ItemId, x => x.RequiresHq,
            x => x.Name, x => $"{Number(x.Quantity)} × {(x.RequiresHq ? "HQ" : "NQ")} · {(x.Responsibility == CommissionMaterialResponsibility.Provided ? "Company supplies" : "Crafter supplies")}", "Material", Add);
        var a = before.Payment;
        var b = after.Payment;
        var payment = new CommissionPaymentChange[] {
            new("Reimbursement", a.MaterialReimbursement, b.MaterialReimbursement),
            new("Craft labor", a.CraftLabor, b.CraftLabor),
            new("Adjustment", a.MaterialAdjustment, b.MaterialAdjustment), new("Total", a.Total, b.Total) };
        foreach (var p in payment.Where(p => p.Delta != 0))
        {
            Add(p.Component, Money(p.Before), Money(p.After));
        }

        Add("Payment timing", a.Schedule.ToString(), b.Schedule.ToString());
        Add("Custom payment terms", a.CustomTerms, b.CustomTerms);
        Add("Payment contract", a.ContractLabel, b.ContractLabel);
        Add("Labor calculation", $"{a.CraftSynthCount} × {Money(a.GilPerSynth)}", $"{b.CraftSynthCount} × {Money(b.GilPerSynth)}");
        Add("Delivery instructions", before.DeliveryInstructions, after.DeliveryInstructions);
        Add("Contact instructions", before.ContactInstructions, after.ContactInstructions);
        // Compare portable recorded provenance, not operational quote-lock/clearance metadata.
        static object Provenance(CompanyCommissionPricingEvidence evidence) =>
            (evidence.CostBasis, evidence.MarketScope, evidence.Location, evidence.CapturedAtUtc);
        var evidenceChanged = !Equals(Provenance(before.PricingEvidence), Provenance(after.PricingEvidence)) ||
            !before.Materials.Select(x => (x.ItemId, x.RequiresHq, x.UnitCost, x.TotalCost)).OrderBy(x => x).SequenceEqual(
                after.Materials.Select(x => (x.ItemId, x.RequiresHq, x.UnitCost, x.TotalCost)).OrderBy(x => x));
        return new(beforeVersion, afterVersion, true, null, changes, payment, evidenceChanged);
    }

    public static string Number(decimal value) => value.ToString("#,##0.############################", CultureInfo.InvariantCulture);
    public static string Money(decimal value) => Number(value) + " gil";

    private static void Match<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, Guid> id,
        Func<T, int> item, Func<T, bool> quality, Func<T, string> name, Func<T, string> describe,
        string category, Action<string, string?, string?> add) where T : class
    {
        var remaining = after.ToList();
        var unresolved = new List<T>();
        foreach (var old in before)
        {
            var candidates = remaining.Where(x => id(x) == id(old) && item(x) == item(old) && id(old) != Guid.Empty).ToArray();
            if (candidates.Length != 1 || before.Count(x => id(x) == id(old)) != 1)
            {
                candidates = before.Count(x => item(x) == item(old) && quality(x) == quality(old)) == 1
                    ? remaining.Where(x => item(x) == item(old) && quality(x) == quality(old)).ToArray() : [];
            }

            if (candidates.Length == 0 && before.Count(x => item(x) == item(old)) == 1)
            {
                candidates = remaining.Where(x => item(x) == item(old)).ToArray();
            }

            string Label(T line, IReadOnlyList<T> lines)
            {
                var peers = lines.Where(x => name(x) == name(line) && quality(x) == quality(line)).ToList();
                var ordinal = peers.Count > 1 ? $" · line {peers.IndexOf(line) + 1}" : "";
                return $"{category}: {name(line)} ({(quality(line) ? "HQ" : "NQ")}){ordinal}";
            }
            var label = Label(old, before);
            if (candidates.Length == 1)
            {
                add(label, describe(old), describe(candidates[0]));
                remaining.Remove(candidates[0]);
            }
            else
            {
                var ambiguous = remaining.Any(x => item(x) == item(old) &&
                    !before.Any(known => id(x) != Guid.Empty && id(known) == id(x) && item(known) == item(x)));
                if (ambiguous)
                {
                    unresolved.Add(old);
                }
                add(label + (ambiguous ? " · unmatched recorded line (ambiguous)" : " · removed"), describe(old),
                    ambiguous ? "Not matched; no individual attribution inferred" : "Removed");
            }
        }
        foreach (var added in remaining)
        {
            var peers = after.Where(x => name(x) == name(added) && quality(x) == quality(added)).ToList();
            var ordinal = peers.Count > 1 ? $" · line {peers.IndexOf(added) + 1}" : "";
            var ambiguous = unresolved.Any(x => item(x) == item(added));
            add($"{category}: {name(added)} ({(quality(added) ? "HQ" : "NQ")}){ordinal}" +
                (ambiguous ? " · unmatched recorded line (ambiguous)" : " · added"), ambiguous ? "Not matched" : "Not present", describe(added));
        }
    }
}
