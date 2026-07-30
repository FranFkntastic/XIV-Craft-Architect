using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.CompanyMigration;

public enum CompanyMigrationCheckpointStage
{
    Prepared,
    CommitSent,
    Committed,
    Activated
}

public sealed class CompanyMigrationRecoveryCheckpoint
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public CompanyMigrationCheckpointStage Stage { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string HostUrl { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string ActiveCompanyId { get; init; } = string.Empty;
    public CompanyMigrationExportBundle Source { get; init; } = new();
    public ProfileHostBootstrapPayload DestinationBefore { get; init; } = new();
    public ProfileHostMigrationPreflightResponse Preflight { get; set; } = new();
    public ProfileHostMigrationCommitRequest Request { get; init; } = new();
    public ProfileHostMigrationCommitResponse? Receipt { get; set; }
}

public sealed class CompanyMigrationCheckpointStore
{
    private const string ModulePath = "./companyMigrationCheckpoint.js?v=1";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _jsRuntime;
    private Task<IJSObjectReference>? _moduleTask;

    public CompanyMigrationCheckpointStore(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<CompanyMigrationRecoveryCheckpoint?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken);
        var json = await module.InvokeAsync<string?>(
            "loadActiveCheckpoint",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var checkpoint = JsonSerializer.Deserialize<CompanyMigrationRecoveryCheckpoint>(
            json,
            JsonOptions);
        if (checkpoint is not { FormatVersion: CompanyMigrationRecoveryCheckpoint.CurrentFormatVersion })
        {
            throw new InvalidOperationException(
                "The saved company migration checkpoint uses an unsupported format.");
        }

        return checkpoint;
    }

    public async Task SaveAsync(
        CompanyMigrationRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        var module = await GetModuleAsync(cancellationToken);
        var saved = await module.InvokeAsync<bool>(
            "saveActiveCheckpoint",
            cancellationToken,
            JsonSerializer.Serialize(checkpoint, JsonOptions));
        if (!saved)
        {
            throw new InvalidOperationException(
                "Browser storage could not save the company migration recovery checkpoint.");
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken);
        var cleared = await module.InvokeAsync<bool>(
            "clearActiveCheckpoint",
            cancellationToken);
        if (!cleared)
        {
            throw new InvalidOperationException(
                "Browser storage could not clear the company migration recovery checkpoint.");
        }
    }

    private Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        _moduleTask ??= _jsRuntime
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
            .AsTask();
        return _moduleTask;
    }
}
