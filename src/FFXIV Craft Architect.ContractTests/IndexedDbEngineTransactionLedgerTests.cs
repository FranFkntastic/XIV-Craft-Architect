using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public sealed class IndexedDbEngineTransactionLedgerTests
{
    [Fact]
    public async Task Ledger_MapsDurableClaimAndWritesTerminalResult()
    {
        var runtime = new RecordingLedgerRuntime();
        var ledger = new IndexedDbEngineTransactionLedger(runtime);
        var transactionId = Guid.NewGuid();
        var requestHash = new string('a', 64);

        var claim = await ledger.ClaimAsync(transactionId, requestHash, CancellationToken.None);
        var result = CreateTerminalResult(transactionId);
        await ledger.CompleteAsync(
            transactionId,
            requestHash,
            claim.ClaimToken!,
            result,
            CancellationToken.None);

        Assert.True(ledger.Capability.IsDurable);
        Assert.True(ledger.Capability.BindsCanonicalRequestIdentity);
        Assert.False(ledger.Capability.PreservesTerminalResult);
        Assert.True(ledger.Capability.PreservesTerminalIdentity);
        Assert.Equal(EngineTransactionClaimDisposition.Claimed, claim.Disposition);
        Assert.Equal("claim-token", claim.ClaimToken);
        Assert.Equal("IndexedDB.completeEngineTransaction", runtime.LastIdentifier);
        Assert.Equal(transactionId.ToString("D"), runtime.LastArguments![0]);
        Assert.Contains(result.Completion.FinalTransactionHash, Assert.IsType<string>(runtime.LastArguments[3]), StringComparison.Ordinal);
        Assert.NotNull(ledger.LastCompleteTiming);
    }

    [Fact]
    public async Task Ledger_MapsConflictWithoutInventingClaimOwnership()
    {
        var runtime = new RecordingLedgerRuntime
        {
            ClaimJson = """
                {"disposition":"conflict","canonicalRequestHash":"existing-hash","terminalResultJson":null,"claimToken":null}
                """
        };
        var ledger = new IndexedDbEngineTransactionLedger(runtime);

        var claim = await ledger.ClaimAsync(Guid.NewGuid(), "requested-hash", CancellationToken.None);

        Assert.Equal(EngineTransactionClaimDisposition.Conflict, claim.Disposition);
        Assert.Equal("existing-hash", claim.CanonicalRequestHash);
        Assert.Null(claim.ClaimToken);
        Assert.Null(claim.TerminalResult);
    }

    [Fact]
    public async Task Ledger_MapsExpiredTerminalWithoutClaimOwnership()
    {
        var runtime = new RecordingLedgerRuntime
        {
            ClaimJson = """
                {"disposition":"expiredTerminalReplay","canonicalRequestHash":"existing-hash","terminalResultJson":null,"claimToken":null}
                """
        };
        var ledger = new IndexedDbEngineTransactionLedger(runtime);

        var claim = await ledger.ClaimAsync(Guid.NewGuid(), "existing-hash", CancellationToken.None);

        Assert.Equal(EngineTransactionClaimDisposition.ExpiredTerminalReplay, claim.Disposition);
        Assert.Null(claim.ClaimToken);
        Assert.Null(claim.TerminalResult);
    }

    private static EngineResultEnvelope CreateTerminalResult(Guid transactionId)
    {
        var basis = new EngineBasisSet(
            new EngineBasisIdentity("plan", "1", "plan"),
            new EngineBasisIdentity("session", "1", "session"),
            new EngineBasisIdentity("publication", "1", "publication"),
            new EngineBasisIdentity("route", "1", "route"));
        var completion = new EngineCompletionEvidence(
            "1",
            transactionId,
            EngineTerminalStatus.Succeeded,
            EnginePhase.Completed,
            basis,
            "root",
            "graph",
            "analysis",
            "route",
            "final-transaction-hash",
            new Dictionary<string, string> { ["settlement"] = "complete" });
        return new EngineResultEnvelope(
            "1",
            transactionId,
            EngineTerminalStatus.Succeeded,
            null,
            completion);
    }

    private sealed class RecordingLedgerRuntime : IJSRuntime
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public string ClaimJson { get; init; } = """
            {"disposition":"claimed","canonicalRequestHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","terminalResultJson":null,"claimToken":"claim-token"}
            """;

        public string? LastIdentifier { get; private set; }

        public object?[]? LastArguments { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            LastIdentifier = identifier;
            LastArguments = args;
            if (identifier == "IndexedDB.claimEngineTransaction")
            {
                return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(ClaimJson, JsonOptions)!);
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
