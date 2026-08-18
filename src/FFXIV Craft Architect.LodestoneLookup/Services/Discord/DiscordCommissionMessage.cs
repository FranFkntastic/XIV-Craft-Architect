using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordEphemeralLink(string Label, Uri Url);

public static class DiscordCommissionMessage
{
    private const int SapphireColor = 0x2E6EA6;

    public static object Create(
        PublishedCommissionBrief published,
        string commissionBaseUrl) =>
        CreateWithPublicUrl(
            published,
            commissionBaseUrl + Uri.EscapeDataString(published.PublicId),
            DiscordPublicationState.Open,
            actionToken: null);

    public static object CreateWithPublicUrl(
        PublishedCommissionBrief published,
        string publicUrl,
        DiscordPublicationState state,
        string? actionToken,
        string? assignmentLabel = null,
        string? claimUrl = null)
    {
        var brief = published.Brief;
        var fields = new List<object>
        {
            Field("Payment", PaymentSummary(brief.Payment), false),
            Field("Materials", MaterialsSummary(brief), false),
            Field("Basis", EvidenceSummary(brief.Evidence), false),
            Field(
                "Assignment",
                string.IsNullOrWhiteSpace(assignmentLabel)
                    ? ResolveAssignmentLabel(brief, state)
                    : assignmentLabel.Trim(),
                false)
        };
        if (!string.IsNullOrWhiteSpace(brief.Contact))
        {
            fields.Add(Field("Contact", Truncate(brief.Contact.Trim(), 1024), false));
        }

        if (!string.IsNullOrWhiteSpace(brief.DeliveryInstructions))
        {
            fields.Add(Field("Delivery", Truncate(brief.DeliveryInstructions.Trim(), 1024), false));
        }

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = Truncate(brief.Title, 256),
                    description = Truncate(OutputSummary(brief, state), 4096),
                    color = SapphireColor,
                    fields,
                    footer = new
                    {
                        text = Truncate(
                            $"{brief.CompanyName} • {brief.Reference} • Brief v{published.Version}",
                            2048)
                    },
                    timestamp = published.PublishedAtUtc.ToUniversalTime().ToString("O")
                }
            },
            components = CreateComponents(
                published,
                publicUrl,
                state,
                actionToken,
                claimUrl),
            allowed_mentions = new
            {
                parse = Array.Empty<string>()
            }
        };
    }

    public static object CreateEphemeral(
        string message,
        IReadOnlyList<DiscordEphemeralLink>? links = null) => new
        {
            content = Truncate(message, 1900),
            flags = 64,
            components = links is { Count: > 0 }
            ? new[]
            {
                new
                {
                    type = 1,
                    components = links
                        .Take(5)
                        .Select(link => (object)new
                        {
                            type = 2,
                            style = 5,
                            label = Truncate(link.Label, 80),
                            url = link.Url.AbsoluteUri
                        })
                        .ToArray()
                }
            }
            : null,
            allowed_mentions = new
            {
                parse = Array.Empty<string>()
            }
        };

    private static object Field(string name, string value, bool inline) => new
    {
        name,
        value = string.IsNullOrWhiteSpace(value) ? "None" : value,
        inline
    };

    private static string OutputSummary(
        CommissionBriefDocument brief,
        DiscordPublicationState state)
    {
        var outputs = brief.Outputs
            .Take(20)
            .Select(output =>
                $"• **{EscapeMarkdown(output.Name)}** ×{output.Quantity:N0}" +
                (output.MustBeHq ? " HQ" : string.Empty));
        var suffix = brief.Outputs.Count > 20
            ? $"\n• …and {brief.Outputs.Count - 20:N0} more outputs"
            : string.Empty;
        return $"{ResolveStatusLabel(brief, state)}\n{string.Join('\n', outputs)}{suffix}";
    }

    private static object[] CreateComponents(
        PublishedCommissionBrief published,
        string publicUrl,
        DiscordPublicationState state,
        string? actionToken,
        string? claimUrl)
    {
        var buttons = new List<object>
        {
            new
            {
                type = 2,
                style = 5,
                label = "View full brief",
                url = publicUrl
            }
        };
        if (state == DiscordPublicationState.Open &&
            !string.IsNullOrWhiteSpace(claimUrl))
        {
            buttons.Add(new
            {
                type = 2,
                style = 5,
                label = "Claim commission",
                url = claimUrl
            });
        }
        if (!string.IsNullOrWhiteSpace(actionToken))
        {
            if (state == DiscordPublicationState.Open)
            {
                buttons.Add(new
                {
                    type = 2,
                    style = 1,
                    label = "Claim with Discord",
                    custom_id = $"claim-discord:{actionToken}"
                });
            }
        }
        return
        [
            new
            {
                type = 1,
                components = buttons
            }
        ];
    }

    private static string ResolveStatusLabel(
        CommissionBriefDocument brief,
        DiscordPublicationState state) =>
        state switch
        {
            DiscordPublicationState.Open => brief.StatusLabel,
            DiscordPublicationState.Assigned => "Assigned",
            DiscordPublicationState.Closed => "Closed",
            DiscordPublicationState.Revoked => "Publication revoked",
            DiscordPublicationState.Suppressed => "Temporarily unavailable",
            DiscordPublicationState.ReconciliationRequired => "Reconciliation required",
            DiscordPublicationState.Failed => "Publication failed",
            _ => "Unavailable"
        };

    private static string ResolveAssignmentLabel(
        CommissionBriefDocument brief,
        DiscordPublicationState state) =>
        state switch
        {
            DiscordPublicationState.Open => "Open the canonical brief to claim",
            DiscordPublicationState.Assigned => string.IsNullOrWhiteSpace(brief.AssignmentLabel)
                ? "Assigned by the operator"
                : brief.AssignmentLabel,
            DiscordPublicationState.Closed => "No longer accepting volunteers",
            DiscordPublicationState.Revoked => "This publication is no longer active",
            DiscordPublicationState.Suppressed => "This commission is not currently public",
            DiscordPublicationState.ReconciliationRequired => "Operator reconciliation is required",
            DiscordPublicationState.Failed => "Publication is unavailable",
            _ => "Unavailable"
        };

    private static string PaymentSummary(CommissionBriefPayment payment)
    {
        var components = new List<string>
        {
            $"materials {FormatGil(payment.MaterialReimbursement)}"
        };
        if (payment.MaterialBonus > 0)
        {
            components.Add($"bonus {FormatGil(payment.MaterialBonus)}");
        }

        if (payment.CraftLabor > 0)
        {
            var basis = payment.CraftSynthCount > 0 && payment.GilPerSynth > 0
                ? $" ({payment.CraftSynthCount:N0} synths x {payment.GilPerSynth:N0} gil)"
                : string.Empty;
            components.Add($"labor {FormatGil(payment.CraftLabor)}{basis}");
        }

        var schedule = payment.Schedule switch
        {
            CompanyCommissionPaymentSchedule.OnDelivery => "payment on delivery",
            CompanyCommissionPaymentSchedule.Custom =>
                payment.CustomTerms ?? "custom payment timing",
            _ => "payment in advance"
        };
        return $"**{FormatGil(payment.Total)} total**\n" +
            $"{CompanyCommissionPaymentDisplayFormatter.FormatContractLabel(payment.ContractLabel)}; {schedule}\n" +
            string.Join(" + ", components);
    }

    private static string MaterialsSummary(CommissionBriefDocument brief)
    {
        var summary =
            FormatMaterialResponsibility("Crafter gets", brief.CrafterMaterials) +
            "\n" +
            FormatMaterialResponsibility("Company provides", brief.CompanyMaterials);
        return Truncate(summary, 1024);
    }

    private static string EvidenceSummary(CommissionBriefEvidence evidence) =>
        $"{evidence.CostBasis}\n" +
        $"{evidence.MarketScope} • {evidence.Location}\n" +
        $"Captured <t:{new DateTimeOffset(evidence.CapturedAtUtc.ToUniversalTime()).ToUnixTimeSeconds()}:R>";

    private static string FormatMaterialResponsibility(
        string heading,
        IReadOnlyList<CommissionBriefMaterial> materials)
    {
        if (materials.Count == 0)
        {
            return $"**{heading}:** none";
        }

        var lines = materials
            .Take(12)
            .Select(material =>
            {
                var quality = material.RequiresHq ? " HQ" : string.Empty;
                var cost = material.UnitCost > 0
                    ? $" @ {FormatGil(material.UnitCost)} = {FormatGil(material.TotalCost)}"
                    : string.Empty;
                return $"- {EscapeMarkdown(material.Name)} x{material.Quantity:N0}{quality}{cost}";
            })
            .ToList();
        if (materials.Count > lines.Count)
        {
            lines.Add($"- {materials.Count - lines.Count:N0} more material lines in the full brief");
        }

        return $"**{heading}:**\n{string.Join('\n', lines)}";
    }

    private static string FormatGil(decimal value) =>
        $"{value.ToString("N0", CultureInfo.InvariantCulture)} gil";

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";
}
