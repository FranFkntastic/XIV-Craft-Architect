using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class DiscordCommissionMessage
{
    private const int SapphireColor = 0x2E6EA6;

    public static object Create(
        PublishedCommissionBrief published,
        string commissionBaseUrl) =>
        Create(
            published,
            commissionBaseUrl,
            DiscordPublicationState.Open,
            actionToken: null);

    public static object Create(
        PublishedCommissionBrief published,
        string commissionBaseUrl,
        DiscordPublicationState state,
        string? actionToken,
        string? assignmentLabel = null)
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
                commissionBaseUrl,
                state,
                actionToken),
            allowed_mentions = new
            {
                parse = Array.Empty<string>()
            }
        };
    }

    public static object CreateEphemeral(string message) => new
    {
        content = Truncate(message, 1900),
        flags = 64,
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
        string commissionBaseUrl,
        DiscordPublicationState state,
        string? actionToken)
    {
        var buttons = new List<object>
        {
            new
            {
                type = 2,
                style = 5,
                label = "View full brief",
                url = commissionBaseUrl + Uri.EscapeDataString(published.PublicId)
            }
        };
        if (state == DiscordPublicationState.Open &&
            !string.IsNullOrWhiteSpace(actionToken) &&
            actionToken.Length <= 100)
        {
            buttons.Add(new
            {
                type = 2,
                style = 1,
                label = "Volunteer",
                custom_id = actionToken
            });
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
            DiscordPublicationState.ReconciliationRequired => "Reconciliation required",
            DiscordPublicationState.Failed => "Publication failed",
            _ => "Unavailable"
        };

    private static string ResolveAssignmentLabel(
        CommissionBriefDocument brief,
        DiscordPublicationState state) =>
        state switch
        {
            DiscordPublicationState.Open => "Volunteer below or contact the operator",
            DiscordPublicationState.Assigned => string.IsNullOrWhiteSpace(brief.AssignmentLabel)
                ? "Assigned by the operator"
                : brief.AssignmentLabel,
            DiscordPublicationState.Closed => "No longer accepting volunteers",
            DiscordPublicationState.Revoked => "This publication is no longer active",
            DiscordPublicationState.ReconciliationRequired => "Operator reconciliation is required",
            DiscordPublicationState.Failed => "Publication is unavailable",
            _ => "Unavailable"
        };

    private static string PaymentSummary(CommissionBriefPayment payment) =>
        $"**{FormatGil(payment.Total)} total**\n" +
        $"{payment.ContractLabel}\n" +
        $"Materials {FormatGil(payment.MaterialReimbursement)} + " +
        $"bonus {FormatGil(payment.MaterialBonus)} + " +
        $"labor {FormatGil(payment.CraftLabor)}";

    private static string MaterialsSummary(CommissionBriefDocument brief) =>
        $"Crafter supplies **{FormatMaterialCount(brief.CrafterMaterials)}**\n" +
        $"Company supplies **{FormatMaterialCount(brief.CompanyMaterials)}**";

    private static string EvidenceSummary(CommissionBriefEvidence evidence) =>
        $"{evidence.CostBasis}\n" +
        $"{evidence.MarketScope} • {evidence.Location}\n" +
        $"Captured <t:{new DateTimeOffset(evidence.CapturedAtUtc.ToUniversalTime()).ToUnixTimeSeconds()}:R>";

    private static string FormatMaterialCount(IReadOnlyList<CommissionBriefMaterial> materials)
    {
        var quantity = materials.Sum(material => (long)material.Quantity);
        return $"{quantity:N0} units across {materials.Count:N0} items";
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
