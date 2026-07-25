namespace FFXIV_Craft_Architect.Web.Services;

public sealed class StartupInitializationService
{
    private bool _hasStarted;

    public StartupStatus Status { get; private set; } =
        StartupStatus.InProgress("Preparing Craft Architect...");

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
            await RunStepAsync(
                "Loading your preferences...",
                currentStep: 1,
                steps.LoadSettingsAsync,
                cancellationToken);
            await RunStepAsync(
                "Opening your workspace...",
                currentStep: 2,
                steps.BootstrapEngineSessionAsync,
                cancellationToken);
            await RunStepAsync(
                "Loading worlds and data centers...",
                currentStep: 3,
                steps.InitializeWorldDataAsync,
                cancellationToken);
            UpdateStatus(StartupStatus.Complete());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdateStatus(StartupStatus.Warning(
                Status.StepText,
                ex.Message,
                Status.CurrentStep,
                Status.TotalSteps));
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
        int currentStep,
        Func<CancellationToken, Task> step,
        CancellationToken cancellationToken)
    {
        UpdateStatus(StartupStatus.InProgress(stepText, currentStep));
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
    string? WarningMessage,
    int CurrentStep,
    int TotalSteps)
{
    public static StartupStatus InProgress(
        string stepText,
        int currentStep = 0,
        int totalSteps = 3)
    {
        return new StartupStatus(
            stepText,
            IsInitializing: true,
            IsWarning: false,
            CanContinue: false,
            WarningMessage: null,
            currentStep,
            totalSteps);
    }

    public static StartupStatus Warning(
        string stepText,
        string warningMessage,
        int currentStep = 0,
        int totalSteps = 3)
    {
        return new StartupStatus(
            stepText,
            IsInitializing: true,
            IsWarning: true,
            CanContinue: true,
            WarningMessage: warningMessage,
            currentStep,
            totalSteps);
    }

    public static StartupStatus Complete()
    {
        return new StartupStatus(
            "Ready",
            IsInitializing: false,
            IsWarning: false,
            CanContinue: false,
            WarningMessage: null,
            CurrentStep: 3,
            TotalSteps: 3);
    }
}
