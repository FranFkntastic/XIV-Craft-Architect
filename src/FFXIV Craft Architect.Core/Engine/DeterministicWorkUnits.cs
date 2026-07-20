namespace FFXIV_Craft_Architect.Core.Engine;

public sealed record EngineWorkUnit<TInput>(string StableKey, TInput Input);

public sealed record EngineWorkUnitResult<TOutput>(string StableKey, TOutput Output);

public static class DeterministicWorkUnits
{
    public static IReadOnlyList<EngineWorkUnit<TInput>> Order<TInput>(IEnumerable<EngineWorkUnit<TInput>> units)
    {
        var ordered = units.OrderBy(unit => unit.StableKey, StringComparer.Ordinal).ToArray();
        EnsureUniqueKeys(ordered.Select(unit => unit.StableKey));
        return ordered;
    }

    public static async Task<IReadOnlyList<EngineWorkUnitResult<TOutput>>> ExecuteSequentialAsync<TInput, TOutput>(
        IEnumerable<EngineWorkUnit<TInput>> units,
        Func<TInput, CancellationToken, Task<TOutput>> execute,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EngineWorkUnitResult<TOutput>>();
        foreach (var unit in Order(units))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new EngineWorkUnitResult<TOutput>(unit.StableKey, await execute(unit.Input, cancellationToken)));
        }

        return results;
    }

    public static async Task<IReadOnlyList<EngineWorkUnitResult<TOutput>>> ExecuteBoundedParallelAsync<TInput, TOutput>(
        IEnumerable<EngineWorkUnit<TInput>> units,
        int degreeOfParallelism,
        Func<TInput, CancellationToken, Task<TOutput>> execute,
        CancellationToken cancellationToken = default)
    {
        if (degreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        }

        var ordered = Order(units);
        using var gate = new SemaphoreSlim(degreeOfParallelism, degreeOfParallelism);
        var tasks = ordered.Select(async unit =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return new EngineWorkUnitResult<TOutput>(unit.StableKey, await execute(unit.Input, cancellationToken));
            }
            finally
            {
                gate.Release();
            }
        });
        return Merge(await Task.WhenAll(tasks));
    }

    public static IReadOnlyList<EngineWorkUnitResult<TOutput>> Merge<TOutput>(
        IEnumerable<EngineWorkUnitResult<TOutput>> results)
    {
        var ordered = results.OrderBy(result => result.StableKey, StringComparer.Ordinal).ToArray();
        EnsureUniqueKeys(ordered.Select(result => result.StableKey));
        return ordered;
    }

    public static string ComputeResultHash<TOutput>(IEnumerable<EngineWorkUnitResult<TOutput>> results) =>
        EngineCanonicalHash.Compute(Merge(results));

    private static void EnsureUniqueKeys(IEnumerable<string> keys)
    {
        string? previous = null;
        foreach (var key in keys)
        {
            if (string.Equals(previous, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Duplicate engine work-unit key '{key}'.");
            }

            previous = key;
        }
    }
}
