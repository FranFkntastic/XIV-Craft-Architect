using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ExperimentalProcurementEngineRegistrationTests
{
    [Fact]
    public void DefaultCompositionRejectsBeforeBrowserInterop()
    {
        var runtime = new RecordingJsRuntime();
        using var provider = CreateProvider(runtime, executionEnabled: false);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;

        Assert.False(services.GetRequiredService<ExperimentalProcurementEngineCapability>().IsExecutionEnabled);
        var factory = services.GetRequiredService<ExperimentalProcurementEngineFactory>();
        var exception = Assert.Throws<NotSupportedException>(() => factory.Create(null!));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(services.GetService<IEngineExecutionTransport>());
        Assert.Null(services.GetService<IEngineTransactionLedger>());
        Assert.Null(services.GetService<IEngineExecutionHost>());
        Assert.Equal(0, runtime.InvocationCount);
    }

    [Fact]
    public async Task EnabledCompositionCreatesIsolatedWorkerExecutionsWithDurableLedger()
    {
        var runtime = new RecordingJsRuntime();
        using var provider = CreateProvider(runtime, executionEnabled: true);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var factory = services.GetRequiredService<ExperimentalProcurementEngineFactory>();
        var appState = services.GetRequiredService<AppState>();
        await using var first = factory.Create(CreateRegistration(appState));
        await using var second = factory.Create(CreateRegistration(appState));

        Assert.NotSame(first, second);
        Assert.Equal(EngineExecutionTransportKind.BrowserWorker, first.TransportCapability.Kind);
        Assert.True(first.TransportCapability.IsSupported);
        Assert.True(first.LedgerCapability.IsDurable);
        Assert.True(first.LedgerCapability.BindsCanonicalRequestIdentity);
        Assert.False(first.LedgerCapability.PreservesTerminalResult);
        Assert.True(first.LedgerCapability.PreservesTerminalIdentity);
        Assert.Null(services.GetService<IEngineExecutionTransport>());
        Assert.Null(services.GetService<IEngineTransactionLedger>());
        Assert.Null(services.GetService<IEngineExecutionHost>());
        Assert.Equal(0, runtime.InvocationCount);
    }

    [Fact]
    public void DeploymentConfigurationEnablesEngineOnlyForLocalDev()
    {
        var root = LocateRepositoryRoot();
        var appSettings = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "appsettings.json"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy-vps-web.yml"));
        var artifactBuilder = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "TruthfulSuite",
            "truthful-artifact.mjs"));

        Assert.Contains("\"GenerationEnabled\": false", appSettings, StringComparison.Ordinal);
        Assert.Contains("\"ExecutionEnabled\": false", appSettings, StringComparison.Ordinal);
        Assert.Contains("slot === 'local-dev'", artifactBuilder, StringComparison.Ordinal);
        Assert.Contains("procurementRoutesGenerationEnabled: true", artifactBuilder, StringComparison.Ordinal);
        Assert.Contains("engineRewriteExecutionEnabled: true", artifactBuilder, StringComparison.Ordinal);
        Assert.Contains("procurementRoutesGenerationEnabled: false", artifactBuilder, StringComparison.Ordinal);
        Assert.Contains("engineRewriteExecutionEnabled: false", artifactBuilder, StringComparison.Ordinal);
        Assert.Contains("--id engine-browser-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("if: steps.target.outputs.slot == 'local-dev'", workflow, StringComparison.Ordinal);
        Assert.Contains("check-product.mjs", workflow, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(IJSRuntime runtime, bool executionEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ExperimentalEngineServiceCollectionExtensions.ExecutionEnabledConfigurationKey] =
                    executionEnabled.ToString()
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton(new AppState());
        services.AddSingleton(provider => new IndexedDbService(provider.GetRequiredService<IJSRuntime>()));
        services.AddExperimentalProcurementEngine(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static WebProcurementSettlementRegistration CreateRegistration(AppState appState)
    {
        var request = new EngineRequestEnvelope(
            "1",
            Guid.NewGuid(),
            EngineInputKind.RootIntent,
            JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)),
            new EngineBasisSet(
                EngineBasisIdentity.Empty("plan"),
                EngineBasisIdentity.Empty("session"),
                EngineBasisIdentity.Empty("publication"),
                EngineBasisIdentity.Empty("route")),
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
        var gateHeld = true;
        var gate = OperationGateLease.Create(
            () => gateHeld,
            () =>
            {
                if (!gateHeld)
                {
                    return false;
                }
                gateHeld = false;
                return true;
            });
        return new WebProcurementSettlementRegistration(
            request,
            new CraftingPlan(),
            appState.PlanSessionVersion,
            appState.CurrentVersions.PlanDecisionVersion,
            appState.CurrentVersions.MarketAnalysisVersion,
            appState.CreateCurrentProcurementRouteBasis(),
            gate);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public int InvocationCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            InvocationCount++;
            throw new InvalidOperationException($"Unexpected browser interop: {identifier}");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            InvocationCount++;
            throw new InvalidOperationException($"Unexpected browser interop: {identifier}");
        }
    }
}
