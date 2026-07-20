using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public sealed record EngineParityFixture(
    string Name,
    EngineInputKind InputKind,
    JsonElement Input,
    string ExpectedExpandedGraphHash,
    string ExpectedResultHash,
    bool ExpectsSuccessfulRoute);

public interface IEngineParityFixtureSource
{
    IReadOnlyList<EngineParityFixture> GetFixtures();
}

public interface IEngineParityFixtureRunner
{
    Task<(string ExpandedGraphHash, string ResultHash, bool RouteSucceeded)> RunAsync(
        EngineParityFixture fixture,
        int degreeOfParallelism,
        CancellationToken cancellationToken = default);
}
