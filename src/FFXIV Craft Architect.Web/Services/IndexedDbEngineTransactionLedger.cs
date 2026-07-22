using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record EngineLedgerCompleteTiming(
    long SerializationMilliseconds,
    long WriteMilliseconds,
    long TotalMilliseconds);

public sealed class IndexedDbEngineTransactionLedger : IEngineTransactionLedger
{
    private static readonly JsonSerializerOptions WireJsonOptions = EngineJsonSerializerOptions.CreateWire();
    private readonly IJSRuntime _jsRuntime;

    public EngineLedgerCompleteTiming? LastCompleteTiming { get; private set; }

    public IndexedDbEngineTransactionLedger(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    }

    public EngineTransactionLedgerCapability Capability { get; } = new(
        BindsCanonicalRequestIdentity: true,
        PreservesTerminalResult: false,
        IsDurable: true,
        PreservesTerminalIdentity: true);

    public async ValueTask<EngineTransactionClaim> ClaimAsync(
        Guid transactionId,
        string canonicalRequestHash,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(transactionId, canonicalRequestHash);
        var claim = await _jsRuntime.InvokeAsync<LedgerClaim>(
            "IndexedDB.claimEngineTransaction",
            cancellationToken,
            transactionId.ToString("D"),
            canonicalRequestHash);
        var disposition = claim.Disposition switch
        {
            "claimed" => EngineTransactionClaimDisposition.Claimed,
            "activeReplay" => EngineTransactionClaimDisposition.ActiveReplay,
            "terminalReplay" => EngineTransactionClaimDisposition.TerminalReplay,
            "conflict" => EngineTransactionClaimDisposition.Conflict,
            "abandonedReplay" => EngineTransactionClaimDisposition.AbandonedReplay,
            "expiredTerminalReplay" => EngineTransactionClaimDisposition.ExpiredTerminalReplay,
            _ => throw new InvalidOperationException(
                $"IndexedDB returned unknown engine transaction disposition '{claim.Disposition}'.")
        };
        var terminalResult = string.IsNullOrWhiteSpace(claim.TerminalResultJson)
            ? null
            : JsonSerializer.Deserialize<EngineResultEnvelope>(claim.TerminalResultJson, WireJsonOptions)
                ?? throw new InvalidOperationException("IndexedDB returned an empty terminal engine result.");
        return new EngineTransactionClaim(
            disposition,
            claim.CanonicalRequestHash,
            terminalResult,
            claim.ClaimToken);
    }

    public async ValueTask CompleteAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        EngineResultEnvelope terminalResult,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(transactionId, canonicalRequestHash, claimToken);
        ArgumentNullException.ThrowIfNull(terminalResult);
        var totalElapsed = Stopwatch.StartNew();
        var serializationElapsed = Stopwatch.StartNew();
        var terminalResultJson = JsonSerializer.Serialize(terminalResult, WireJsonOptions);
        serializationElapsed.Stop();
        var writeElapsed = Stopwatch.StartNew();
        await _jsRuntime.InvokeVoidAsync(
            "IndexedDB.completeEngineTransaction",
            cancellationToken,
            transactionId.ToString("D"),
            canonicalRequestHash,
            claimToken,
            terminalResultJson);
        writeElapsed.Stop();
        totalElapsed.Stop();
        LastCompleteTiming = new EngineLedgerCompleteTiming(
            serializationElapsed.ElapsedMilliseconds,
            writeElapsed.ElapsedMilliseconds,
            totalElapsed.ElapsedMilliseconds);
    }

    public async ValueTask ReleaseAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(transactionId, canonicalRequestHash, claimToken);
        await _jsRuntime.InvokeVoidAsync(
            "IndexedDB.releaseEngineTransaction",
            cancellationToken,
            transactionId.ToString("D"),
            canonicalRequestHash,
            claimToken);
    }

    private static void ValidateIdentity(
        Guid transactionId,
        string canonicalRequestHash,
        string? claimToken = null)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRequestHash);
        if (claimToken is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        }
    }

    private sealed record LedgerClaim(
        string Disposition,
        string CanonicalRequestHash,
        string? TerminalResultJson,
        string? ClaimToken);
}
