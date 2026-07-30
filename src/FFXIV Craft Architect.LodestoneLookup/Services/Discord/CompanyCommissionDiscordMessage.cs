using System.Text;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class CompanyCommissionDiscordMessage
{
    private const int SapphireColor = 0x2E6EA6;
    private const int SuppressNotificationsFlag = 1 << 12;

    public static object CreatePublication(
        CommittedCompanyCommissionDiscordProjection projection)
    {
        var commission = projection.Commission;
        var state = ResolveProjectionState(commission);
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

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = DiscordProjectionSanitizer.Text(commission.Title, 256),
                    description = DiscordProjectionSanitizer.Text(projection.Summary, 4096),
                    color = SapphireColor,
                    fields = new[]
                    {
                        new
                        {
                            name = "Status",
                            value = state switch
                            {
                                DiscordPublicationState.Open => "Open for one exclusive claim",
                                DiscordPublicationState.Assigned => "Assigned",
                                DiscordPublicationState.Closed => "Closed",
                                DiscordPublicationState.Revoked => "Publication revoked",
                                _ => "Unavailable"
                            },
                            inline = true
                        },
                        new
                        {
                            name = "Reference",
                            value = DiscordProjectionSanitizer.Text(commission.Reference, 1024),
                            inline = true
                        }
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
                    title = $"{AttentionLabel(attentionClass)} - " +
                        DiscordProjectionSanitizer.Text(notification.Commission.Reference, 220),
                    description = DiscordProjectionSanitizer.Text(notification.Summary, 4096),
                    color = AttentionColor(attentionClass),
                    fields = string.IsNullOrWhiteSpace(notification.ActorDisplayName)
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
                        LinkButton("Open activity", notification.ActivityUrl.AbsoluteUri)
                    }
                }
            },
            ["allowed_mentions"] = mention
                ? OneUserMention(commissionerDiscordUserId)
                : NoMentions()
        };
        if (behavior == DiscordNotificationMentionBehavior.SilentPing)
        {
            payload["flags"] = SuppressNotificationsFlag;
        }

        return payload;
    }

    private static object LinkButton(string label, string url) => new
    {
        type = 2,
        style = 5,
        label,
        url
    };

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

        if (commission.Closed ||
            TradeOrderStatusWorkflow.IsArchived(commission.Status))
        {
            return DiscordPublicationState.Closed;
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
