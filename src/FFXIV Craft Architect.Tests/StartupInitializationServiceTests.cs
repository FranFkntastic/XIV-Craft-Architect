using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class StartupInitializationServiceTests
{
    [Fact]
    public async Task InitializeAsync_NoAutosave_RunsStepsAndCompletes()
    {
        var service = new StartupInitializationService();
        var observedSteps = new List<string>();
        service.StatusChanged += () => observedSteps.Add(service.Status.StepText);
        var calls = new List<string>();

        await service.InitializeAsync(new StartupInitializationSteps(
            LoadSettingsAsync: _ =>
            {
                calls.Add("settings");
                return Task.CompletedTask;
            },
            InitializeWorldDataAsync: _ =>
            {
                calls.Add("world-data");
                return Task.CompletedTask;
            },
            StartAutoSave: () => calls.Add("autosave-timer"),
            RestoreAutoSaveAsync: _ =>
            {
                calls.Add("autosave-restore");
                return Task.FromResult(StartupAutoSaveRestoreResult.NoAutoSave());
            }));

        Assert.Equal(
            ["settings", "world-data", "autosave-timer", "autosave-restore"],
            calls);
        Assert.Contains("Loading settings...", observedSteps);
        Assert.Contains("Loading world data...", observedSteps);
        Assert.Contains("Checking autosave...", observedSteps);
        Assert.Contains("Restoring autosave...", observedSteps);
        Assert.False(service.Status.IsInitializing);
        Assert.False(service.Status.IsWarning);
        Assert.False(service.Status.CanContinue);
    }

    [Fact]
    public async Task InitializeAsync_AutosaveRestoreWarning_RequiresContinueBeforeCompleting()
    {
        var service = new StartupInitializationService();

        await service.InitializeAsync(new StartupInitializationSteps(
            LoadSettingsAsync: _ => Task.CompletedTask,
            InitializeWorldDataAsync: _ => Task.CompletedTask,
            StartAutoSave: () => { },
            RestoreAutoSaveAsync: _ => Task.FromResult(
                StartupAutoSaveRestoreResult.Restored("Could not load market analysis data."))));

        Assert.True(service.Status.IsInitializing);
        Assert.True(service.Status.IsWarning);
        Assert.True(service.Status.CanContinue);
        Assert.Contains("Could not load market analysis data.", service.Status.WarningMessage);

        service.ContinueAfterWarning();

        Assert.False(service.Status.IsInitializing);
        Assert.False(service.Status.IsWarning);
        Assert.False(service.Status.CanContinue);
    }

    [Fact]
    public async Task InitializeAsync_RecoverableFailure_ShowsWarningContinue()
    {
        var service = new StartupInitializationService();

        await service.InitializeAsync(new StartupInitializationSteps(
            LoadSettingsAsync: _ => throw new InvalidOperationException("settings unavailable"),
            InitializeWorldDataAsync: _ => Task.CompletedTask,
            StartAutoSave: () => { },
            RestoreAutoSaveAsync: _ => Task.FromResult(StartupAutoSaveRestoreResult.NoAutoSave())));

        Assert.True(service.Status.IsInitializing);
        Assert.True(service.Status.IsWarning);
        Assert.True(service.Status.CanContinue);
        Assert.Contains("settings", service.Status.StepText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settings unavailable", service.Status.WarningMessage);
    }
}
