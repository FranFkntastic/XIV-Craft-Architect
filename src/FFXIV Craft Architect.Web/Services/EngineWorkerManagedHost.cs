using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;

namespace CraftArchitectEngineWorker;

public sealed record EngineWorkerRuntimeProof(
    string ProtocolVersion,
    string RuntimeAssembly,
    string Challenge,
    string ChallengeHash,
    string ProofHash);

public static partial class ManagedHost
{
    private const string ProtocolVersion = "2";

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static string GetRuntimeProofJson(string challenge) =>
        JsonSerializer.Serialize(CreateRuntimeProof(challenge), EngineJsonSerializerOptions.CreateWire());

    public static EngineWorkerRuntimeProof CreateRuntimeProof(string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        if (challenge.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(challenge), "The runtime proof challenge is too long.");
        }

        var runtimeAssembly = typeof(ManagedHost).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The worker runtime assembly has no identity.");
        var challengeHash = EngineCanonicalHash.Compute(challenge);
        var proofHash = EngineCanonicalHash.Compute(new
        {
            Domain = "engine-worker-runtime-proof-v1",
            ProtocolVersion,
            RuntimeAssembly = runtimeAssembly,
            Challenge = challenge,
            ChallengeHash = challengeHash
        });
        return new EngineWorkerRuntimeProof(
            ProtocolVersion,
            runtimeAssembly,
            challenge,
            challengeHash,
            proofHash);
    }
}
