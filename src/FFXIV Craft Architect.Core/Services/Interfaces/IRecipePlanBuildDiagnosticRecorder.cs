namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipePlanBuildDiagnosticRecorder
{
    T RunPhase<T>(string name, Func<T> action);

    void RunPhase(string name, Action action);

    Task<T> RunPhaseAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);

    Task RunPhaseAsync(
        string name,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken);
}
