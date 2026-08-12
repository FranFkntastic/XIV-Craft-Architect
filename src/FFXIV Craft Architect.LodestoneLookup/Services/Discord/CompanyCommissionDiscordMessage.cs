using System.Globalization;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class CompanyCommissionDiscordMessage
{
    private const int SapphireColor = 0x2E6EA6;
    private const int ClaimedColor = 0xD18B18;
    private const int CraftingColor = 0x2E8B57;
    private const int DeliveryColor = 0x2B9AA0;
    private const int ClosedColor = 0x6B7280;
    private const int ExceptionColor = 0xC0392B;
    private const int SuppressNotificationsFlag = 1 << 12;

    public static object CreatePublication(
        CommittedCompanyCommissionDiscordProjection projection,
        string? actionToken = null)
    {
        var commission = projection.Commission;
        var state = ResolveProjectionState(commission);
        var lifecycle = ResolveLifecycleLabel(commission);
        var buttons = new List<object>
        {
            LinkButton("View commission", projection.PublicViewUrl.AbsoluteUri)
        };
        if (state == DiscordPublicationState.Open)
        {
            if (projection.ClaimUrl == null)
            {
                throw new InvalidOperationException(
                    "An open Discord commission projection requires a claim-capable URL.");
            }

            buttons.Add(LinkButton("Claim commission", projection.ClaimUrl.AbsoluteUri));
        }
        if (!string.IsNullOrWhiteSpace(actionToken))
        {
            if (state == DiscordPublicationState.Open)
            {
                buttons.Add(ActionButton(
                    "Claim with Discord",
                    $"claim-discord:{actionToken}",
                    style: 1));
            }
        }

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = DiscordProjectionSanitizer.Text(
                        commission.IsTestFixture
                            ? $"TEST COMMISSION - {commission.Title}"
                            : commission.Title,
                        256),
                    description = DiscordProjectionSanitizer.Text(
                        (commission.IsTestFixture
                            ? "**TEST FIXTURE - CLAIMING DISABLED**\n"
                            : string.Empty) +
                        $"**{lifecycle}**\n{OutputSummary(commission)}",
                        4096),
                    color = PublicationColor(commission, state),
                    author = new
                    {
                        name = "Craft Architect | Commission"
                    },
                    fields = new List<object>
                    {
                        Field("Payment", PaymentSummary(commission.Terms.Payment), false),
                        Field(
                            "Crafter gets",
                            MaterialsSummary(
                                commission.Terms.Materials,
                                CommissionMaterialResponsibility.Crafter),
                            false),
                        Field(
                            "Company provides",
                            MaterialsSummary(
                                commission.Terms.Materials,
                                CommissionMaterialResponsibility.Provided),
                            false),
                        Field("Work clearance", GateSummary(commission), true),
                        Field(
                            "Reference",
                            DiscordProjectionSanitizer.Text(commission.Reference, 1024),
                            true)
                    },
                    footer = new
                    {
                        text = DiscordProjectionSanitizer.Text(
                            $"{commission.CompanyDisplayName} | Terms v{commission.Terms.Version}",
                            2048)
                    },
                    timestamp = projection.CommittedAtUtc.ToUniversalTime().ToString("O")
                }
            },
            components = new[]
            {
                new
                {
                    type = 1,
                    components = buttons
                }
            },
            allowed_mentions = NoMentions()
        };
    }

    public static object CreateNotification(
        CommittedCompanyCommissionNotification notification,
        DiscordNotificationAttentionClass attentionClass,
        DiscordNotificationMentionBehavior behavior,
        DiscordNotificationDestinationKind destinationKind,
        string commissionerDiscordUserId)
    {
        if (behavior == DiscordNotificationMentionBehavior.Off)
        {
            throw new InvalidOperationException("Suppressed notifications cannot be rendered.");
        }

        var mention =
            destinationKind == DiscordNotificationDestinationKind.UpdateChannel &&
            (behavior is
                DiscordNotificationMentionBehavior.Push or
                DiscordNotificationMentionBehavior.SilentPing);
        var content = mention ? $"<@{commissionerDiscordUserId}>" : null;
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["embeds"] = new[]
            {
                new
                {
                    title = DiscordProjectionSanitizer.Text(
                        $"{AttentionLabel(attentionClass)} - " +
                        $"{notification.Commission.Title} [{notification.Commission.Reference}]",
                        256),
                    description = DiscordProjectionSanitizer.Text(notification.Summary, 4096),
                    color = AttentionColor(attentionClass),
                    fields = destinationKind == DiscordNotificationDestinationKind.MemberDirectMessage ||
                        string.IsNullOrWhiteSpace(notification.ActorDisplayName)
                        ? Array.Empty<object>()
                        :
                        [
                            new
                            {
                                name = "Updated by",
                                value = DiscordProjectionSanitizer.Text(
                                    notification.ActorDisplayName,
                                    1024),
                                inline = true
                            }
                        ],
                    url = notification.ActivityUrl.AbsoluteUri,
                    timestamp = notification.CommittedAtUtc.ToUniversalTime().ToString("O")
                }
            },
            ["components"] = new[]
            {
                new
                {
                    type = 1,
                    components = new[]
                    {
                        LinkButton(
                            ResolveNotificationActionLabel(notification, destinationKind),
                            notification.ActivityUrl.AbsoluteUri)
                    }
                }
            },
            ["allowed_mentions"] = mention
                ? OneUserMention(commissionerDiscordUserId)
                : NoMentions()
        };
        if (behavior == DiscordNotificationMentionBehavior.SilentPing ||
            (destinationKind ==
                    DiscordNotificationDestinationKind.CommissionerDirectMessage &&
                behavior == DiscordNotificationMentionBehavior.NoPing))
        {
            payload["flags"] = SuppressNotificationsFlag;
        }

        return payload;
    }

    private static string ResolveNotificationActionLabel(
        CommittedCompanyCommissionNotification notification,
        DiscordNotificationDestinationKind destinationKind) =>
        destinationKind == DiscordNotificationDestinationKind.MemberDirectMessage
            ? notification.EventKind switch
            {
                CompanyCommissionActivityKind.ProgressReported => "View progress",
                CompanyCommissionActivityKind.CommentAdded => "View comment",
                _ => "View commission"
            }
            : DiscordProjectionSanitizer.Text(notification.ActionLabel, 80);

    private static object LinkButton(string label, string url) => new
    {
        type = 2,
        style = 5,
        label,
        url
    };

    private static object ActionButton(string label, string customId, int style) => new
    {
        type = 2,
        style,
        label,
        custom_id = customId
    };

    private static object Field(string name, string value, bool inline) => new
    {
        name,
        value = string.IsNullOrWhiteSpace(value) ? "None" : value,
        inline
    };

    private static string ResolveLifecycleLabel(CompanyCommissionPublicBrief commission)
    {
        if (commission.ViewState == CompanyCommissionPublicViewState.Revoked)
        {
            return "PUBLICATION REVOKED";
        }
        if (commission.Status == TradeOrderStatus.Canceled)
        {
            return "CANCELED";
        }
        if (commission.Status == TradeOrderStatus.Completed)
        {
            return commission.SettlementState == CompanyCommissionSettlementState.Satisfied
                ? "COMPLETED"
                : "DELIVERY ACCEPTED - SETTLEMENT PENDING";
        }
        if (commission.IsTestFixture)
        {
            return "TEST COMMISSION - NOT CLAIMABLE";
        }
        if (commission.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "READY FOR DELIVERY";
        }
        if (commission.Status == TradeOrderStatus.InProgress)
        {
            return "CRAFTING";
        }
        if (!commission.IsClaimed)
        {
            return "OPEN - ONE CLAIM SLOT";
        }
        if (commission.Gates.Identity == CompanyCommissionClearanceState.Pending)
        {
            return "CLAIMED - IDENTITY REVIEW";
        }
        return commission.ClearedToWork
            ? "CRAFTING"
            : "ASSIGNED - PRE-WORK";
    }

    private static int PublicationColor(
        CompanyCommissionPublicBrief commission,
        DiscordPublicationState state)
    {
        if (state is DiscordPublicationState.Revoked or DiscordPublicationState.Suppressed ||
            commission.Status is TradeOrderStatus.Canceled or TradeOrderStatus.ResolutionRequired)
        {
            return ExceptionColor;
        }

        return commission.Status switch
        {
            TradeOrderStatus.InProgress => CraftingColor,
            TradeOrderStatus.AwaitingDelivery => DeliveryColor,
            TradeOrderStatus.Completed => ClosedColor,
            _ when state == DiscordPublicationState.Assigned => ClaimedColor,
            _ => SapphireColor
        };
    }

    private static string OutputSummary(CompanyCommissionPublicBrief commission)
    {
        var progressByLine = commission.OutputProgress
            .ToDictionary(progress => progress.LineId);
        var lines = commission.Terms.Outputs
            .Take(20)
            .Select(output =>
            {
                var quality = output.MustBeHq ? " HQ" : string.Empty;
                var progress = progressByLine.GetValueOrDefault(output.LineId);
                var progressSummary = progress is { CompletedQuantity: > 0 } or { ReadyQuantity: > 0 }
                    ? $" - {progress.CompletedQuantity:N0} crafted, {progress.ReadyQuantity:N0} ready"
                    : string.Empty;
                return $"- **{EscapeMarkdown(output.Name)}** x{output.RequiredQuantity:N0}{quality}{progressSummary}";
            })
            .ToList();
        if (commission.Terms.Outputs.Count > lines.Count)
        {
            lines.Add(
                $"- {commission.Terms.Outputs.Count - lines.Count:N0} more output lines in the full brief");
        }

        return string.Join('\n', lines);
    }

    private static string PaymentSummary(CompanyCommissionPaymentTerms payment)
    {
        var timing = payment.Schedule switch
        {
            CompanyCommissionPaymentSchedule.Advance => "Payment up front",
            CompanyCommissionPaymentSchedule.OnDelivery => "Payment on delivery",
            CompanyCommissionPaymentSchedule.Custom =>
                payment.CustomTerms ?? "Custom payment timing",
            _ => "Payment timing unavailable"
        };
        var labor = payment.CraftSynthCount > 0 && payment.GilPerSynth > 0
            ? $"\nLabor: {FormatGil(payment.CraftLabor)} " +
              $"({payment.CraftSynthCount:N0} synths x {payment.GilPerSynth:N0} gil)"
            : payment.CraftLabor > 0
                ? $"\nLabor: {FormatGil(payment.CraftLabor)}"
                : string.Empty;

        return $"**{FormatGil(payment.Total)} total**\n{timing} | {payment.ContractLabel}" +
            $"\nMaterials: {FormatGil(payment.MaterialReimbursement)}{labor}";
    }

    private static string MaterialsSummary(
        IReadOnlyList<CompanyCommissionMaterialTerm> materials,
        CommissionMaterialResponsibility responsibility)
    {
        var selected = materials
            .Where(material => material.Responsibility == responsibility)
            .ToArray();
        if (selected.Length == 0)
        {
            return "None";
        }

        var lines = selected
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
        if (selected.Length > lines.Count)
        {
            lines.Add($"- {selected.Length - lines.Count:N0} more lines in the full brief");
        }

        return DiscordProjectionSanitizer.Text(string.Join('\n', lines), 1024);
    }

    private static string GateSummary(CompanyCommissionPublicBrief commission) =>
        commission.ClearedToWork
            ? "Cleared to start"
            : $"Identity: {GateLabel(commission.Gates.Identity)}\n" +
              $"Payment: {GateLabel(commission.Gates.Payment)}\n" +
              $"Company materials: {GateLabel(commission.Gates.CompanyMaterials)}";

    private static string GateLabel(CompanyCommissionClearanceState state) =>
        state switch
        {
            CompanyCommissionClearanceState.Satisfied => "cleared",
            CompanyCommissionClearanceState.NotRequired => "not required",
            _ => "pending"
        };

    private static string FormatGil(decimal value) =>
        $"{value.ToString("N0", CultureInfo.InvariantCulture)} gil";

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static object NoMentions() => new
    {
        parse = Array.Empty<string>(),
        users = Array.Empty<string>(),
        roles = Array.Empty<string>(),
        replied_user = false
    };

    private static object OneUserMention(string userId) => new
    {
        parse = Array.Empty<string>(),
        users = new[] { userId },
        roles = Array.Empty<string>(),
        replied_user = false
    };

    private static string AttentionLabel(DiscordNotificationAttentionClass attentionClass) =>
        attentionClass switch
        {
            DiscordNotificationAttentionClass.Routine => "Commission update",
            DiscordNotificationAttentionClass.ActionRequired => "Action required",
            DiscordNotificationAttentionClass.CriticalException => "Commission exception",
            _ => "Commission update"
        };

    private static int AttentionColor(DiscordNotificationAttentionClass attentionClass) =>
        attentionClass switch
        {
            DiscordNotificationAttentionClass.Routine => SapphireColor,
            DiscordNotificationAttentionClass.ActionRequired => 0xD18B18,
            DiscordNotificationAttentionClass.CriticalException => 0xC0392B,
            _ => SapphireColor
        };

    internal static DiscordPublicationState ResolveProjectionState(
        CompanyCommissionPublicBrief commission)
    {
        if (commission.ViewState == CompanyCommissionPublicViewState.Revoked)
        {
            return DiscordPublicationState.Revoked;
        }

        if (commission.Status is TradeOrderStatus.Canceled or
            TradeOrderStatus.ResolutionRequired ||
            commission.RequiresManualResolution)
        {
            return DiscordPublicationState.Suppressed;
        }

        if (commission.Closed || commission.Status == TradeOrderStatus.Completed)
        {
            return DiscordPublicationState.Closed;
        }

        if (commission.IsTestFixture)
        {
            return DiscordPublicationState.TestFixture;
        }

        return commission.IsClaimed
                ? DiscordPublicationState.Assigned
                : DiscordPublicationState.Open;
    }
}

internal static class DiscordProjectionSanitizer
{
    public static string Text(string? value, int maximumLength)
    {
        var source = value?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return "Not provided";
        }

        var builder = new StringBuilder(Math.Min(source.Length, maximumLength));
        foreach (var character in source)
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        var sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0)
        {
            return "Not provided";
        }

        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..(maximumLength - 3)] + "...";
    }
}
