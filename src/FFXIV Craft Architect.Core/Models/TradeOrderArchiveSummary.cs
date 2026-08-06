using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed record TradeOrderArchiveSummaryOutput(
    string Name,
    int Quantity,
    bool MustBeHq);

public sealed class TradeOrderArchiveSummary
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid OrderId { get; set; }
    public Guid CompanyProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public TradeOrderStatus Status { get; set; }
    public Guid? AssignedCrafterId { get; set; }
    public DateTime CommissionedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<TradeOrderArchiveSummaryOutput> Outputs { get; set; } = [];
}

public static class TradeOrderArchiveSummaryCodec
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();

    public static TradeOrderArchiveSummary? TryCreate(
        string payloadJson,
        string expectedObjectId)
    {
        TradeOrder? order;
        try
        {
            order = JsonSerializer.Deserialize<TradeOrder>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (order == null ||
            !string.Equals(order.Id.ToString("D"), expectedObjectId, StringComparison.OrdinalIgnoreCase) ||
            !TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return null;
        }

        return new TradeOrderArchiveSummary
        {
            OrderId = order.Id,
            CompanyProfileId = order.CompanyProfileId,
            Title = order.Title,
            Status = order.Status,
            AssignedCrafterId = order.AssignedCrafterId,
            CommissionedAtUtc = order.CommissionedAtUtc,
            UpdatedAtUtc = order.UpdatedAtUtc,
            Outputs = order.SourceSnapshot.RootItems
                .Select(item => new TradeOrderArchiveSummaryOutput(
                    item.Name,
                    item.Quantity,
                    item.MustBeHq))
                .ToList()
        };
    }

    public static string Serialize(TradeOrderArchiveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return JsonSerializer.Serialize(summary, JsonOptions);
    }

    public static TradeOrderArchiveSummary Deserialize(
        string summaryJson,
        string expectedObjectId)
    {
        var summary = JsonSerializer.Deserialize<TradeOrderArchiveSummary>(
                summaryJson,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"Archived order summary '{expectedObjectId}' could not be deserialized.");
        if (!string.Equals(summary.OrderId.ToString("D"), expectedObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Archived order summary '{expectedObjectId}' does not match its object identity.");
        }

        if (summary.SchemaVersion > TradeOrderArchiveSummary.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Archived order summary '{expectedObjectId}' uses unsupported schema version {summary.SchemaVersion}.");
        }

        summary.Outputs ??= [];
        return summary;
    }
}
