namespace FFXIV_Craft_Architect.Web.Services;

public sealed class StartupInitializationService
{
    private bool _hasStarted;

    public StartupStatus Status { get; private set; } = StartupStatus.InProgress("Starting...");

    public event Action? StatusChanged;

    public async Task InitializeAsync(
        StartupInitializationSteps steps,
        CancellationToken cancellationToken = default)
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;

        try
        {
            await RunStepAsync("Loading settings...", steps.LoadSettingsAsync, cancellationToken);
            await RunStepAsync(
                "Restoring engine session...",
                steps.BootstrapEngineSessionAsync,
                cancellationToken);
            await RunStepAsync("Loading world data...", steps.InitializeWorldDataAsync, cancellationToken);
            UpdateStatus(StartupStatus.Complete());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdateStatus(StartupStatus.Warning(Status.StepText, ex.Message));
        }
    }

    public void ContinueAfterWarning()
    {
        if (!Status.CanContinue)
        {
            return;
        }

        UpdateStatus(StartupStatus.Complete());
    }

    private async Task RunStepAsync(
        string stepText,
        Func<CancellationToken, Task> step,
        CancellationToken cancellationToken)
    {
        UpdateStatus(StartupStatus.InProgress(stepText));
        await step(cancellationToken);
    }

    private void UpdateStatus(StartupStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }
}

public sealed record StartupInitializationSteps(
    Func<CancellationToken, Task> LoadSettingsAsync,
    Func<CancellationToken, Task> BootstrapEngineSessionAsync,
    Func<CancellationToken, Task> InitializeWorldDataAsync);

public sealed record StartupStatus(
    string StepText,
    bool IsInitializing,
    bool IsWarning,
    bool CanContinue,
    string? WarningMessage)
{
    public static StartupStatus InProgress(string stepText)
    {
        return new StartupStatus(stepText, IsInitializing: true, IsWarning: false, CanContinue: false, WarningMessage: null);
    }

    public static StartupStatus Warning(string stepText, string warningMessage)
    {
        return new StartupStatus(stepText, IsInitializing: true, IsWarning: true, CanContinue: true, WarningMessage: warningMessage);
    }

    public static StartupStatus Complete()
    {
        return new StartupStatus("Ready", IsInitializing: false, IsWarning: false, CanContinue: false, WarningMessage: null);
    }
}
