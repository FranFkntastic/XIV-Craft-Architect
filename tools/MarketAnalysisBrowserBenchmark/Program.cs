using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var options = BenchmarkOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(BenchmarkOptions.HelpText);
    return 0;
}

var report = BrowserBenchmarkReport.Create(options);

try
{
    report.Checkpoints.Add(await BenchmarkRunner.CaptureCheckpointAsync(
        "start",
        options,
        cdp: null,
        cancellationToken: CancellationToken.None));

    if (!options.ProcessOnly)
    {
        using var cdp = await ChromeDevToolsClient.ConnectAsync(options, report.Errors, CancellationToken.None);
        if (options.Navigate)
        {
            await cdp.SendAsync("Page.enable", null, CancellationToken.None);
            await cdp.SendAsync(
                "Page.navigate",
                new Dictionary<string, object?> { ["url"] = options.Url },
                CancellationToken.None);
            await BenchmarkRunner.WaitForReadyStateAsync(cdp, options.Timeout, CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(options.WaitSelector))
        {
            await BenchmarkRunner.WaitForSelectorAsync(cdp, options.WaitSelector, options.Timeout, CancellationToken.None);
        }

        if (options.EnableDeveloperMode)
        {
            await BenchmarkRunner.EnableDeveloperModeAsync(cdp, options.Timeout, CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(options.ImportNativePlanPath))
        {
            await BenchmarkRunner.ImportNativePlanAsync(cdp, options.ImportNativePlanPath, options.Timeout, CancellationToken.None);
        }

        report.BaselineMarketIntelligenceId = await BenchmarkRunner.ReadActiveMarketIntelligenceIdAsync(
            cdp,
            CancellationToken.None);

        foreach (var benchmarkId in options.ClickBenchmarkIds)
        {
            await BenchmarkRunner.ClickBenchmarkIdAsync(cdp, benchmarkId, CancellationToken.None);
        }

        foreach (var buttonText in options.ClickButtonTexts)
        {
            await BenchmarkRunner.ClickButtonByTextAsync(cdp, buttonText, CancellationToken.None);
        }

        foreach (var elementText in options.ClickElementTexts)
        {
            await BenchmarkRunner.ClickElementByTextAsync(cdp, elementText, CancellationToken.None);
        }

        if (options.WaitMarketAnalysisCompletion)
        {
            await BenchmarkRunner.WaitForMarketAnalysisCompletionAsync(
                cdp,
                options.Timeout,
                report.BaselineMarketIntelligenceId,
                CancellationToken.None);
        }

        if (options.PostActionDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.PostActionDelay, CancellationToken.None);
        }

        report.Checkpoints.Add(await BenchmarkRunner.CaptureCheckpointAsync(
            "after-page-ready",
            options,
            cdp,
            CancellationToken.None));
    }
}
catch (Exception ex)
{
    report.Errors.Add(ex.Message);
}
finally
{
    report.CompletedAtUtc = DateTime.UtcNow;
    report.SafetyTripped = report.Checkpoints.Any(checkpoint => checkpoint.SafetyTripped);
    report.Contaminated = report.Checkpoints.Any(checkpoint => checkpoint.Contaminated);
    report.ApplyComparisonValidity();
    Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
    await File.WriteAllTextAsync(
        options.OutputPath,
        JsonSerializer.Serialize(report, BenchmarkJson.Options));
    Console.WriteLine(options.OutputPath);
}

return report.Errors.Count == 0 && !report.SafetyTripped ? 0 : 1;

internal sealed record BenchmarkOptions
{
    public string Url { get; init; } = "http://localhost:5000";

    public string DevToolsEndpoint { get; init; } = "http://127.0.0.1:9223";

    public string OutputPath { get; init; } = Path.Combine(
        Path.GetTempPath(),
        $"market-analysis-browser-benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public string Scenario { get; init; } = "custom";

    public string? PlanPath { get; init; }

    public string CacheState { get; init; } = "warm";

    public string Label { get; init; } = "manual";

    public string SystemProfile { get; init; } = "clean";

    public bool Navigate { get; init; }

    public bool ProcessOnly { get; init; }

    public string? WaitSelector { get; init; }

    public string? ImportNativePlanPath { get; init; }

    public bool EnableDeveloperMode { get; init; }

    public IReadOnlyList<string> ClickBenchmarkIds { get; init; } = [];

    public IReadOnlyList<string> ClickButtonTexts { get; init; } = [];

    public IReadOnlyList<string> ClickElementTexts { get; init; } = [];

    public TimeSpan PostActionDelay { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool WaitMarketAnalysisCompletion { get; init; }

    public decimal SafetyTotalChromeMb { get; init; } = 7_000m;

    public decimal SafetyLargestChromeMb { get; init; } = 2_500m;

    public bool ShowHelp { get; init; }

    public static string HelpText =>
        """
        MarketAnalysisBrowserBenchmark

        Records repeatable browser/process measurements for FFXIV Craft Architect market-analysis runs.

        Required runtime shape for DOM measurements:
          - the web app is running, usually http://localhost:5000
          - Chrome is already running with --remote-debugging-port=9223

        Examples:
          dotnet run --project tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj -- --process-only --scenario ssuc --cache-state warm --plan "C:\Users\gianf\Downloads\SSUC Benchmark.craftplan"
          dotnet run --project tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj -- --url http://localhost:5000 --devtools http://127.0.0.1:9223 --navigate --scenario ssuc --cache-state warm --output C:\tmp\ssuc-browser-benchmark.json

        Options:
          --url <url>                         App URL to measure. Default: http://localhost:5000
          --devtools <url>                    Chrome DevTools endpoint. Default: http://127.0.0.1:9223
          --output <path>                     JSON report path. Default: OS temp directory
          --scenario <custom|ssuc|pressure-hull>
          --plan <path>                       Plan file used by the human/browser workflow
          --cache-state <cold|warm|force-refresh|contaminated>
          --label <text>                      Free-form run label
          --system-profile <clean|ffxiv-open> Operating profile. Default: clean
          --navigate                          Navigate the selected Chrome page before measuring
          --process-only                      Skip DevTools and record system/Chrome process memory only
          --wait-selector <css>               Wait until a selector exists before measuring
          --enable-developer-mode             Persist and reload with Developer Mode enabled for benchmark hooks
          --import-native-plan <path>         Import a .craftplan through the native import dialog before measuring
          --click-benchmark-id <id>           Click an element with data-benchmark-id=<id>
          --click-button-text <text>          Click the first enabled button with matching text before measuring
          --click-element-text <text>         Click the first visible element with matching text before measuring
          --wait-market-analysis-completion   Poll until market analysis has completed or hard-failed
          --post-action-delay-seconds <sec>   Delay after click actions before measuring
          --timeout-seconds <seconds>         Wait timeout. Default: 60
          --safety-total-chrome-mb <mb>       Default: 7000
          --safety-largest-chrome-mb <mb>     Default: 2500
        """;

    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options = options with { ShowHelp = true };
                    break;
                case "--url":
                    options = options with { Url = RequireValue(args, ref index, arg) };
                    break;
                case "--devtools":
                    options = options with { DevToolsEndpoint = RequireValue(args, ref index, arg) };
                    break;
                case "--output":
                    options = options with { OutputPath = RequireValue(args, ref index, arg) };
                    break;
                case "--scenario":
                    options = options with { Scenario = RequireValue(args, ref index, arg) };
                    break;
                case "--plan":
                    options = options with { PlanPath = RequireValue(args, ref index, arg) };
                    break;
                case "--cache-state":
                    options = options with { CacheState = RequireValue(args, ref index, arg) };
                    break;
                case "--label":
                    options = options with { Label = RequireValue(args, ref index, arg) };
                    break;
                case "--system-profile":
                    options = options with { SystemProfile = RequireValue(args, ref index, arg) };
                    break;
                case "--navigate":
                    options = options with { Navigate = true };
                    break;
                case "--process-only":
                    options = options with { ProcessOnly = true };
                    break;
                case "--wait-selector":
                    options = options with { WaitSelector = RequireValue(args, ref index, arg) };
                    break;
                case "--enable-developer-mode":
                    options = options with { EnableDeveloperMode = true };
                    break;
                case "--import-native-plan":
                    options = options with { ImportNativePlanPath = RequireValue(args, ref index, arg) };
                    break;
                case "--click-benchmark-id":
                    options = options with { ClickBenchmarkIds = [.. options.ClickBenchmarkIds, RequireValue(args, ref index, arg)] };
                    break;
                case "--click-button-text":
                    options = options with { ClickButtonTexts = [.. options.ClickButtonTexts, RequireValue(args, ref index, arg)] };
                    break;
                case "--click-element-text":
                    options = options with { ClickElementTexts = [.. options.ClickElementTexts, RequireValue(args, ref index, arg)] };
                    break;
                case "--wait-market-analysis-completion":
                    options = options with { WaitMarketAnalysisCompletion = true };
                    break;
                case "--post-action-delay-seconds":
                    options = options with { PostActionDelay = TimeSpan.FromSeconds(double.Parse(RequireValue(args, ref index, arg))) };
                    break;
                case "--timeout-seconds":
                    options = options with { Timeout = TimeSpan.FromSeconds(double.Parse(RequireValue(args, ref index, arg))) };
                    break;
                case "--safety-total-chrome-mb":
                    options = options with { SafetyTotalChromeMb = decimal.Parse(RequireValue(args, ref index, arg)) };
                    break;
                case "--safety-largest-chrome-mb":
                    options = options with { SafetyLargestChromeMb = decimal.Parse(RequireValue(args, ref index, arg)) };
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Use --help for usage.");
            }
        }

        return ApplyScenarioDefaults(options);
    }

    private static BenchmarkOptions ApplyScenarioDefaults(BenchmarkOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PlanPath))
        {
            return options;
        }

        return options.Scenario.ToLowerInvariant() switch
        {
            "ssuc" => options with { PlanPath = @"C:\Users\gianf\Downloads\SSUC Benchmark.craftplan" },
            "pressure-hull" => options with { PlanPath = @"C:\Users\gianf\Downloads\Plan 2026-06-04 14_26.craftplan" },
            _ => options
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }
}

internal static class BenchmarkRunner
{
    public static async Task<BenchmarkCheckpoint> CaptureCheckpointAsync(
        string name,
        BenchmarkOptions options,
        ChromeDevToolsClient? cdp,
        CancellationToken cancellationToken)
    {
        var chrome = ProcessSnapshot.Capture("chrome");
        var system = SystemMemorySnapshot.Capture();
        JsonElement? page = cdp is null
            ? null
            : await cdp.EvaluateValueAsync(CreatePageSnapshotExpression(), cancellationToken);
        var contaminated = IsContaminated(system, chrome.TopUnrelatedProcesses, options);
        var safetyTripped = chrome.TotalPrivateMemoryMb > options.SafetyTotalChromeMb ||
                            chrome.LargestPrivateMemoryMb > options.SafetyLargestChromeMb;
        return new BenchmarkCheckpoint(
            name,
            DateTime.UtcNow,
            system,
            chrome,
            page,
            contaminated,
            safetyTripped);
    }

    public static async Task WaitForReadyStateAsync(
        ChromeDevToolsClient cdp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await cdp.EvaluateValueAsync("document.readyState", cancellationToken);
            if (state?.ValueKind == JsonValueKind.String &&
                string.Equals(state.Value.GetString(), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for document.readyState == complete.");
    }

    public static async Task WaitForSelectorAsync(
        ChromeDevToolsClient cdp,
        string selector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var exists = await cdp.EvaluateValueAsync(
                $"document.querySelector({JsonSerializer.Serialize(selector)}) !== null",
                cancellationToken);
            if (exists?.ValueKind == JsonValueKind.True)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for selector '{selector}'.");
    }

    public static async Task WaitForMarketAnalysisCompletionAsync(
        ChromeDevToolsClient cdp,
        TimeSpan timeout,
        string? baselineMarketIntelligenceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await cdp.EvaluateValueAsync(
                CreateMarketAnalysisCompletionExpression(baselineMarketIntelligenceId),
                cancellationToken);
            if (state?.ValueKind == JsonValueKind.Object)
            {
                var completionState = GetString(state.Value, "completionState");
                if (string.Equals(completionState, "completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(completionState, "hard-error", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(1_000, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for market analysis completion.");
    }

    public static async Task<string?> ReadActiveMarketIntelligenceIdAsync(
        ChromeDevToolsClient cdp,
        CancellationToken cancellationToken)
    {
        var value = await cdp.EvaluateValueAsync(CreateActiveMarketIntelligenceIdExpression(), cancellationToken);
        return value?.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : null;
    }

    public static async Task ImportNativePlanAsync(
        ChromeDevToolsClient cdp,
        string planPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(planPath))
        {
            throw new FileNotFoundException("Plan file not found.", planPath);
        }

        if (!await TryClickBenchmarkIdAsync(cdp, "main-import-menu", cancellationToken))
        {
            await ClickButtonByTextAsync(cdp, "Import", cancellationToken);
        }

        if (!await TryClickBenchmarkIdAsync(cdp, "main-import-native-plan", cancellationToken))
        {
            await ClickElementByTextAsync(cdp, "From Craft Architect", cancellationToken);
        }
        await WaitForSelectorAsync(cdp, "#nativeFileInput", timeout, cancellationToken);
        await cdp.SetFileInputFilesAsync("#nativeFileInput", [Path.GetFullPath(planPath)], cancellationToken);
        await WaitForButtonEnabledAsync(cdp, "IMPORT", timeout, cancellationToken);
        await ClickButtonByTextAsync(cdp, "IMPORT", cancellationToken);
        await Task.Delay(5_000, cancellationToken);
    }

    public static async Task EnableDeveloperModeAsync(
        ChromeDevToolsClient cdp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var indexedDbReady = await cdp.EvaluateValueAsync(
                "typeof window.IndexedDB?.saveSetting === 'function'",
                cancellationToken);
            if (indexedDbReady?.ValueKind == JsonValueKind.True)
            {
                break;
            }

            await Task.Delay(250, cancellationToken);
        }

        var enabled = await cdp.EvaluateValueAsync(
            """
            (async () => {
              if (!window.IndexedDB?.saveSetting) {
                return false;
              }

              await window.IndexedDB.saveSetting('debug.secret_tools_enabled', 'true');
              return true;
            })()
            """,
            cancellationToken);
        if (enabled?.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("Could not enable Developer Mode because IndexedDB.saveSetting was unavailable.");
        }

        await cdp.SendAsync("Page.reload", null, cancellationToken);
        await WaitForReadyStateAsync(cdp, timeout, cancellationToken);
        await WaitForSelectorAsync(cdp, "[data-benchmark-id]", timeout, cancellationToken);
    }

    public static async Task ClickBenchmarkIdAsync(
        ChromeDevToolsClient cdp,
        string benchmarkId,
        CancellationToken cancellationToken)
    {
        if (!await TryClickBenchmarkIdAsync(cdp, benchmarkId, cancellationToken))
        {
            throw new InvalidOperationException($"Could not find enabled benchmark hook '{benchmarkId}'.");
        }
    }

    private static async Task<bool> TryClickBenchmarkIdAsync(
        ChromeDevToolsClient cdp,
        string benchmarkId,
        CancellationToken cancellationToken)
    {
        var escapedId = JsonSerializer.Serialize(benchmarkId);
        var clicked = await cdp.EvaluateValueAsync(
            $$"""
            (() => {
              const id = {{escapedId}};
              const element = document.querySelector(`[data-benchmark-id="${CSS.escape(id)}"]`);
              if (!element || element.disabled || element.getAttribute('aria-disabled') === 'true') {
                return false;
              }

              element.click();
              return true;
            })()
            """,
            cancellationToken);
        return clicked?.ValueKind == JsonValueKind.True;
    }

    public static async Task WaitForButtonEnabledAsync(
        ChromeDevToolsClient cdp,
        string buttonText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var escapedText = JsonSerializer.Serialize(buttonText);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var enabled = await cdp.EvaluateValueAsync(
                $$"""
                (() => {
                  const expected = {{escapedText}}.trim();
                  return Array.from(document.querySelectorAll('button'))
                    .some(button => (button.innerText || '').trim() === expected && !button.disabled);
                })()
                """,
                cancellationToken);
            if (enabled?.ValueKind == JsonValueKind.True)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for button '{buttonText}' to become enabled.");
    }

    public static async Task ClickButtonByTextAsync(
        ChromeDevToolsClient cdp,
        string buttonText,
        CancellationToken cancellationToken)
    {
        var escapedText = JsonSerializer.Serialize(buttonText);
        var clicked = await cdp.EvaluateValueAsync(
            $$"""
            (() => {
              const expected = {{escapedText}}.trim().toLowerCase();
              const expectedRaw = {{escapedText}}.trim();
              const button = Array.from(document.querySelectorAll('button'))
                .find(candidate => !candidate.disabled && (candidate.innerText || '').trim() === expectedRaw) ??
                Array.from(document.querySelectorAll('button'))
                  .find(candidate => !candidate.disabled && (candidate.innerText || '').trim().toLowerCase() === expected);
              if (!button) {
                return false;
              }

              button.click();
              return true;
            })()
            """,
            cancellationToken);
        if (clicked?.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException($"Could not find enabled button with text '{buttonText}'.");
        }
    }

    public static async Task ClickElementByTextAsync(
        ChromeDevToolsClient cdp,
        string elementText,
        CancellationToken cancellationToken)
    {
        var escapedText = JsonSerializer.Serialize(elementText);
        var clicked = await cdp.EvaluateValueAsync(
            $$"""
            (() => {
              const expected = {{escapedText}}.trim().toLowerCase();
              const elements = Array.from(document.querySelectorAll('button, [role="menuitem"], .mud-list-item, .mud-menu-item, li, div, span'));
              const matches = elements
                .filter(candidate => {
                  const rect = candidate.getBoundingClientRect();
                  const text = (candidate.innerText || candidate.textContent || '').trim().toLowerCase();
                  return rect.width > 0 &&
                  rect.height > 0 &&
                  text.includes(expected);
                })
                .sort((left, right) =>
                  (left.innerText || left.textContent || '').length -
                  (right.innerText || right.textContent || '').length);
              const element = matches[0];
              if (!element) {
                return false;
              }

              element.click();
              return true;
            })()
            """,
            cancellationToken);
        if (clicked?.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException($"Could not find visible element with text '{elementText}'.");
        }
    }

    private static string CreatePageSnapshotExpression()
    {
        return """
            (async () => {
              const all = document.querySelectorAll('*');
              const text = document.body?.innerText || '';
              let indexedDbDatabaseCount = null;
              try {
                if (indexedDB.databases) {
                  indexedDbDatabaseCount = (await indexedDB.databases()).length;
                }
              } catch {
                indexedDbDatabaseCount = null;
              }

              const statusText = Array.from(document.querySelectorAll('[role="status"], .validation-message, .mud-alert-message, .mud-progress-linear'))
                .map(element => (element.textContent || '').trim())
                .filter(Boolean)
                .slice(0, 10);
              const statusTextJoined = statusText.join('\n');

              const getField = (value, camel, pascal) => value?.[camel] ?? value?.[pascal] ?? null;
              const parseJsonObject = (value) => {
                if (!value) {
                  return null;
                }

                if (typeof value !== 'string') {
                  return typeof value === 'object' ? value : null;
                }

                try {
                  const parsed = JSON.parse(value);
                  return parsed && typeof parsed === 'object' ? parsed : null;
                } catch {
                  return null;
                }
              };

              const selectActiveMarketIntelligence = (plans) => {
                if (!Array.isArray(plans)) {
                  return null;
                }

                const candidates = plans
                  .map(plan => {
                    const intelligence = parseJsonObject(getField(plan, 'marketIntelligenceJson', 'MarketIntelligenceJson'));
                    return intelligence ? { plan, intelligence } : null;
                  })
                  .filter(Boolean);

                candidates.sort((left, right) => {
                  const leftModified = Date.parse(getField(left.plan, 'modifiedAt', 'ModifiedAt') || '') || 0;
                  const rightModified = Date.parse(getField(right.plan, 'modifiedAt', 'ModifiedAt') || '') || 0;
                  return rightModified - leftModified;
                });

                return candidates[0] ?? null;
              };

              let marketAnalysisPersistenceWarning = null;
              let marketCacheStats = null;
              let marketCacheStatsWarning = null;
              let marketAnalysisActiveIntelligenceId = null;
              let marketAnalysisPersistedItemAnalysisCount = null;
              let marketAnalysisPersistedRecommendationCount = null;
              let marketAnalysisCompletionState = 'unknown';
              const marketAnalysisAnalyzingVisible = /\bANALYZING\b/i.test(text);
              const marketAnalysisHardErrorVisible = /suspect cache|failed|error|could not/i.test(statusTextJoined);
              const marketAnalysisHasTopLevelResults = document.querySelectorAll('table tr').length > 1 &&
                /recommended|total cost|world/i.test(text);
              try {
                if (window.IndexedDB?.loadAllPlans) {
                  const plans = await window.IndexedDB.loadAllPlans();
                  const active = selectActiveMarketIntelligence(plans);
                  const intelligence = active?.intelligence ?? null;
                  const itemAnalyses = getField(intelligence, 'itemAnalyses', 'ItemAnalyses');
                  const recommendations = getField(intelligence, 'recommendations', 'Recommendations');
                  marketAnalysisActiveIntelligenceId = getField(intelligence, 'marketIntelligenceId', 'MarketIntelligenceId');
                  marketAnalysisPersistedItemAnalysisCount = Array.isArray(itemAnalyses) ? itemAnalyses.length : 0;
                  marketAnalysisPersistedRecommendationCount = Array.isArray(recommendations) ? recommendations.length : 0;
                  if (marketAnalysisActiveIntelligenceId && marketAnalysisPersistedItemAnalysisCount > 0) {
                    marketAnalysisCompletionState = marketAnalysisAnalyzingVisible ? 'analyzing' : 'completed';
                  } else if (intelligence) {
                    marketAnalysisPersistenceWarning = 'Stored market intelligence did not include item analyses.';
                    marketAnalysisCompletionState = marketAnalysisAnalyzingVisible ? 'analyzing' : 'missing-market-analysis';
                  }
                } else {
                  marketAnalysisCompletionState = marketAnalysisAnalyzingVisible ? 'analyzing' : 'missing-indexeddb-hooks';
                }
              } catch (error) {
                marketAnalysisPersistenceWarning = String(error?.message || error);
                marketAnalysisCompletionState = marketAnalysisAnalyzingVisible ? 'analyzing' : 'market-intelligence-error';
              }

              try {
                if (window.IndexedDB?.getMarketCacheStats) {
                  const oneHourAgoUnix = Math.floor(Date.now() / 1000) - 3600;
                  marketCacheStats = await window.IndexedDB.getMarketCacheStats(oneHourAgoUnix);
                }
              } catch (error) {
                marketCacheStatsWarning = String(error?.message || error);
              }

              if (marketAnalysisCompletionState === 'unknown') {
                marketAnalysisCompletionState = marketAnalysisAnalyzingVisible
                  ? 'analyzing'
                  : marketAnalysisHardErrorVisible
                    ? 'hard-error'
                    : 'missing-market-intelligence';
              }

              if (marketAnalysisHardErrorVisible && marketAnalysisCompletionState !== 'completed') {
                marketAnalysisCompletionState = 'hard-error';
              }

              return {
                href: location.href,
                title: document.title,
                readyState: document.readyState,
                nodeCount: all.length,
                tableCount: document.querySelectorAll('table').length,
                tableRowCount: document.querySelectorAll('tr').length,
                expandedElementCount: document.querySelectorAll('[aria-expanded="true"], details[open]').length,
                dialogCount: document.querySelectorAll('[role="dialog"], .mud-dialog').length,
                statusText,
                bodyTextLength: text.length,
                bodyTextPreview: text.slice(0, 2000),
                buttons: Array.from(document.querySelectorAll('button'))
                  .map((button, index) => ({
                    index,
                    text: (button.innerText || '').trim(),
                    title: button.title || '',
                    ariaLabel: button.getAttribute('aria-label') || '',
                    disabled: button.disabled,
                    className: button.className || ''
                  }))
                  .slice(0, 80),
                inputs: Array.from(document.querySelectorAll('input'))
                  .map((input, index) => ({
                    index,
                    type: input.type || '',
                    accept: input.accept || '',
                    id: input.id || '',
                    name: input.name || '',
                    ariaLabel: input.getAttribute('aria-label') || '',
                    className: input.className || ''
                  }))
                  .slice(0, 40),
                indexedDbDatabaseCount,
                marketAnalysisActiveIntelligenceId,
                marketAnalysisPersistedItemAnalysisCount,
                marketAnalysisPersistedRecommendationCount,
                marketAnalysisCompletionState,
                marketAnalysisAnalyzingVisible,
                marketAnalysisHardErrorVisible,
                marketAnalysisHasTopLevelResults,
                performanceMemory: performance.memory ? {
                  jsHeapSizeLimit: performance.memory.jsHeapSizeLimit,
                  totalJSHeapSize: performance.memory.totalJSHeapSize,
                  usedJSHeapSize: performance.memory.usedJSHeapSize
                } : null,
                marketAnalysisPersistenceWarning,
                marketCacheStats,
                marketCacheStatsWarning
              };
            })()
            """;
    }

    private static string CreateActiveMarketIntelligenceIdExpression()
    {
        return """
            (async () => {
              const getField = (value, camel, pascal) => value?.[camel] ?? value?.[pascal] ?? null;
              const parseJsonObject = (value) => {
                if (!value) {
                  return null;
                }

                if (typeof value !== 'string') {
                  return typeof value === 'object' ? value : null;
                }

                try {
                  const parsed = JSON.parse(value);
                  return parsed && typeof parsed === 'object' ? parsed : null;
                } catch {
                  return null;
                }
              };

              if (!window.IndexedDB?.loadAllPlans) {
                return null;
              }

              const plans = await window.IndexedDB.loadAllPlans();
              const activePlan = Array.isArray(plans)
                ? plans
                  .filter(plan => getField(plan, 'marketIntelligenceJson', 'MarketIntelligenceJson'))
                  .sort((left, right) => {
                    const leftModified = Date.parse(getField(left, 'modifiedAt', 'ModifiedAt') || '') || 0;
                    const rightModified = Date.parse(getField(right, 'modifiedAt', 'ModifiedAt') || '') || 0;
                    return rightModified - leftModified;
                  })[0]
                : null;
              const intelligence = parseJsonObject(getField(activePlan, 'marketIntelligenceJson', 'MarketIntelligenceJson'));
              return getField(intelligence, 'marketIntelligenceId', 'MarketIntelligenceId');
            })()
            """;
    }

    private static string CreateMarketAnalysisCompletionExpression(string? baselineMarketIntelligenceId)
    {
        var serializedBaselineMarketIntelligenceId = JsonSerializer.Serialize(baselineMarketIntelligenceId);
        return """
            (async () => {
              const baselineMarketIntelligenceId = __BASELINE_MARKET_INTELLIGENCE_ID__;
              const text = document.body?.innerText || '';
              const getField = (value, camel, pascal) => value?.[camel] ?? value?.[pascal] ?? null;
              const parseJsonObject = (value) => {
                if (!value) {
                  return null;
                }

                if (typeof value !== 'string') {
                  return typeof value === 'object' ? value : null;
                }

                try {
                  const parsed = JSON.parse(value);
                  return parsed && typeof parsed === 'object' ? parsed : null;
                } catch {
                  return null;
                }
              };
              const analyzingVisible = /\bANALYZING\b/i.test(text);
              const statusText = Array.from(document.querySelectorAll('[role="status"], .validation-message, .mud-alert-message, .mud-progress-linear'))
                .map(element => (element.textContent || '').trim())
                .filter(Boolean)
                .join('\n');
              const hardErrorVisible = /suspect cache|failed|error|could not/i.test(statusText);
              try {
                if (window.IndexedDB?.loadAllPlans) {
                  const plans = await window.IndexedDB.loadAllPlans();
                  const activePlan = Array.isArray(plans)
                    ? plans
                      .filter(plan => getField(plan, 'marketIntelligenceJson', 'MarketIntelligenceJson'))
                      .sort((left, right) => {
                        const leftModified = Date.parse(getField(left, 'modifiedAt', 'ModifiedAt') || '') || 0;
                        const rightModified = Date.parse(getField(right, 'modifiedAt', 'ModifiedAt') || '') || 0;
                        return rightModified - leftModified;
                      })[0]
                    : null;
                  const intelligence = parseJsonObject(getField(activePlan, 'marketIntelligenceJson', 'MarketIntelligenceJson'));
                  const activeMarketIntelligenceId = getField(intelligence, 'marketIntelligenceId', 'MarketIntelligenceId');
                  const itemAnalyses = getField(intelligence, 'itemAnalyses', 'ItemAnalyses');
                  const itemAnalysisCount = Array.isArray(itemAnalyses) ? itemAnalyses.length : 0;
                  if (activeMarketIntelligenceId && itemAnalysisCount > 0) {
                    if (baselineMarketIntelligenceId && activeMarketIntelligenceId === baselineMarketIntelligenceId) {
                      return { completionState: 'baseline-intelligence', activeMarketIntelligenceId, analyzingVisible, hardErrorVisible };
                    }

                    return { completionState: analyzingVisible ? 'analyzing' : 'completed', activeMarketIntelligenceId, itemAnalysisCount };
                  }
                }
              } catch (error) {
                return {
                  completionState: analyzingVisible ? 'analyzing' : 'market-intelligence-error',
                  warning: String(error?.message || error)
                };
              }

              return {
                completionState: hardErrorVisible ? 'hard-error' : analyzingVisible ? 'analyzing' : 'missing-market-intelligence',
                analyzingVisible,
                hardErrorVisible
              };
            })()
            """.Replace("__BASELINE_MARKET_INTELLIGENCE_ID__", serializedBaselineMarketIntelligenceId);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsContaminated(
        SystemMemorySnapshot system,
        IReadOnlyList<ProcessMemoryEntry> topUnrelatedProcesses,
        BenchmarkOptions options)
    {
        if (system.AvailableMb < 4_096m)
        {
            return true;
        }

        if (!string.Equals(options.SystemProfile, "ffxiv-open", StringComparison.OrdinalIgnoreCase))
        {
            return topUnrelatedProcesses.Any(process => process.PrivateMemoryMb > 2_048m);
        }

        return topUnrelatedProcesses.Any(process =>
            process.PrivateMemoryMb > 4_096m &&
            !string.Equals(process.Name, "ffxiv_dx11", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class ChromeDevToolsClient : IDisposable
{
    private readonly ClientWebSocket _socket;
    private int _nextId;

    private ChromeDevToolsClient(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public static async Task<ChromeDevToolsClient> ConnectAsync(
        BenchmarkOptions options,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var endpoint = options.DevToolsEndpoint.TrimEnd('/');
        var target = await FindOrCreatePageTargetAsync(http, endpoint, options, cancellationToken);
        var webSocketUrl = target.WebSocketDebuggerUrl
            ?? throw new InvalidOperationException("Selected Chrome target did not expose a webSocketDebuggerUrl.");
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
        errors.AddRange(target.Warnings);
        return new ChromeDevToolsClient(socket);
    }

    public async Task<JsonElement> SendAsync(
        string method,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        var json = JsonSerializer.Serialize(payload, BenchmarkJson.DefaultOptions);
        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        while (true)
        {
            var response = await ReceiveJsonAsync(cancellationToken);
            if (response.TryGetProperty("id", out var responseId) &&
                responseId.GetInt32() == id)
            {
                if (response.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(error.ToString());
                }

                return response.Clone();
            }
        }
    }

    public async Task<JsonElement?> EvaluateValueAsync(string expression, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            "Runtime.evaluate",
            new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["awaitPromise"] = true,
                ["returnByValue"] = true
            },
            cancellationToken);
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("result", out var evaluationResult) ||
            !evaluationResult.TryGetProperty("value", out var value))
        {
            return null;
        }

        return value.Clone();
    }

    public async Task SetFileInputFilesAsync(
        string selector,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var document = await SendAsync("DOM.getDocument", null, cancellationToken);
        var rootNodeId = document
            .GetProperty("result")
            .GetProperty("root")
            .GetProperty("nodeId")
            .GetInt32();
        var query = await SendAsync(
            "DOM.querySelector",
            new Dictionary<string, object?>
            {
                ["nodeId"] = rootNodeId,
                ["selector"] = selector
            },
            cancellationToken);
        var nodeId = query.GetProperty("result").GetProperty("nodeId").GetInt32();
        if (nodeId == 0)
        {
            throw new InvalidOperationException($"Could not find file input '{selector}'.");
        }

        await SendAsync(
            "DOM.setFileInputFiles",
            new Dictionary<string, object?>
            {
                ["nodeId"] = nodeId,
                ["files"] = files
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }

    private async Task<JsonElement> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Chrome DevTools WebSocket closed.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<ChromeTarget> FindOrCreatePageTargetAsync(
        HttpClient http,
        string endpoint,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var targets = await LoadTargetsAsync(http, endpoint, cancellationToken);
        var target = targets
            .Where(candidate => string.Equals(candidate.Type, "page", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => UrlMatches(candidate.Url, options.Url))
            .FirstOrDefault();
        if (target is not null)
        {
            return target;
        }

        if (!options.Navigate)
        {
            throw new InvalidOperationException("No Chrome page target was available. Start Chrome with --remote-debugging-port=9223 or pass --navigate to create a page.");
        }

        var createUri = $"{endpoint}/json/new?{Uri.EscapeDataString(options.Url)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, createUri);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var created = await JsonSerializer.DeserializeAsync<ChromeTarget>(
            stream,
            BenchmarkJson.DefaultOptions,
            cancellationToken);
        return created ?? throw new InvalidOperationException("Chrome did not return a created page target.");
    }

    private static async Task<List<ChromeTarget>> LoadTargetsAsync(
        HttpClient http,
        string endpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync($"{endpoint}/json", cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<ChromeTarget>>(
            stream,
            BenchmarkJson.DefaultOptions,
            cancellationToken) ?? [];
    }

    private static bool UrlMatches(string? candidateUrl, string expectedUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl))
        {
            return false;
        }

        return candidateUrl.Contains(expectedUrl, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ChromeTarget
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("webSocketDebuggerUrl")]
    public string? WebSocketDebuggerUrl { get; init; }

    public List<string> Warnings { get; init; } = [];
}

internal sealed record BrowserBenchmarkReport
{
    public string ToolVersion { get; init; } = "phase2-browser-harness";

    public DateTime StartedAtUtc { get; init; }

    public DateTime CompletedAtUtc { get; set; }

    public string Branch { get; init; } = string.Empty;

    public string Commit { get; init; } = string.Empty;

    public bool Dirty { get; init; }

    public string DirtyState { get; init; } = string.Empty;

    public string Scenario { get; init; } = string.Empty;

    public string CacheState { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string SystemProfile { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string DevToolsEndpoint { get; init; } = string.Empty;

    public string? PlanPath { get; init; }

    public bool PlanExists { get; init; }

    public bool ProcessOnly { get; init; }

    public bool WaitMarketAnalysisCompletion { get; init; }

    public string? BaselineMarketIntelligenceId { get; set; }

    public bool Contaminated { get; set; }

    public bool SafetyTripped { get; set; }

    public bool ValidForComparison { get; set; }

    public string ComparisonStatus { get; set; } = "not-evaluated";

    public List<string> InconclusiveReasons { get; } = [];

    public List<BenchmarkCheckpoint> Checkpoints { get; } = [];

    public List<string> Errors { get; } = [];

    public static BrowserBenchmarkReport Create(BenchmarkOptions options)
    {
        return new BrowserBenchmarkReport
        {
            StartedAtUtc = DateTime.UtcNow,
            Branch = GitInfo("rev-parse --abbrev-ref HEAD"),
            Commit = GitInfo("rev-parse --short HEAD"),
            Dirty = !string.IsNullOrWhiteSpace(GitInfo("status --short")),
            DirtyState = GitInfo("status --short"),
            Scenario = options.Scenario,
            CacheState = options.CacheState,
            Label = options.Label,
            SystemProfile = options.SystemProfile,
            Url = options.Url,
            DevToolsEndpoint = options.DevToolsEndpoint,
            PlanPath = options.PlanPath,
            PlanExists = !string.IsNullOrWhiteSpace(options.PlanPath) && File.Exists(options.PlanPath),
            ProcessOnly = options.ProcessOnly,
            WaitMarketAnalysisCompletion = options.WaitMarketAnalysisCompletion
        };
    }

    public void ApplyComparisonValidity()
    {
        InconclusiveReasons.Clear();
        if (ProcessOnly)
        {
            ComparisonStatus = "process-only";
            InconclusiveReasons.Add("Process-only reports do not include browser market-analysis completion state.");
            ValidForComparison = false;
            return;
        }

        if (Errors.Count > 0)
        {
            ComparisonStatus = "blocked";
            InconclusiveReasons.AddRange(Errors.Select(error => $"Harness error: {error}"));
            ValidForComparison = false;
            return;
        }

        if (SafetyTripped)
        {
            ComparisonStatus = "safety-tripped";
            InconclusiveReasons.Add("A browser memory safety guard tripped during the run.");
            ValidForComparison = false;
            return;
        }

        var finalSnapshot = Checkpoints.LastOrDefault(checkpoint => checkpoint.PageSnapshot.HasValue)?.PageSnapshot;
        if (!finalSnapshot.HasValue || finalSnapshot.Value.ValueKind != JsonValueKind.Object)
        {
            ComparisonStatus = "missing-page-snapshot";
            InconclusiveReasons.Add("No final page snapshot was captured.");
            ValidForComparison = false;
            return;
        }

        var completionState = ReadString(finalSnapshot.Value, "marketAnalysisCompletionState");
        if (string.Equals(completionState, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var activeMarketIntelligenceId = ReadString(finalSnapshot.Value, "marketAnalysisActiveIntelligenceId");
            if (!string.IsNullOrWhiteSpace(BaselineMarketIntelligenceId) &&
                string.Equals(activeMarketIntelligenceId, BaselineMarketIntelligenceId, StringComparison.Ordinal))
            {
                ComparisonStatus = "inconclusive";
                InconclusiveReasons.Add("Final page snapshot still referenced the baseline market-intelligence id.");
                ValidForComparison = false;
                return;
            }

            if (!HasPersistedMarketAnalysis(finalSnapshot.Value))
            {
                ComparisonStatus = "inconclusive";
                InconclusiveReasons.Add("Market analysis appeared completed, but persisted full market intelligence was missing item analyses.");
                ValidForComparison = false;
                return;
            }

            if (!ReadBool(finalSnapshot.Value, "marketAnalysisHasTopLevelResults"))
            {
                ComparisonStatus = "inconclusive";
                InconclusiveReasons.Add("Market analysis persisted, but top-level market-analysis results were not visible.");
                ValidForComparison = false;
                return;
            }

            ComparisonStatus = "completed";
            ValidForComparison = true;
            return;
        }

        if (string.Equals(completionState, "hard-error", StringComparison.OrdinalIgnoreCase))
        {
            ComparisonStatus = "hard-error";
            ValidForComparison = true;
            return;
        }

        ComparisonStatus = "inconclusive";
        InconclusiveReasons.Add($"Market analysis completion state was '{completionState ?? "missing"}'.");
        if (ReadBool(finalSnapshot.Value, "marketAnalysisAnalyzingVisible"))
        {
            InconclusiveReasons.Add("The page still showed ANALYZING.");
        }

        var warning = ReadString(finalSnapshot.Value, "marketAnalysisPersistenceWarning");
        if (!string.IsNullOrWhiteSpace(warning))
        {
            InconclusiveReasons.Add($"Market-intelligence persistence warning: {warning}");
        }

        ValidForComparison = false;
    }

    private static bool HasPersistedMarketAnalysis(JsonElement snapshot)
    {
        return ReadInt(snapshot, "marketAnalysisPersistedItemAnalysisCount") > 0;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static string GitInfo(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return string.Empty;
            }

            process.WaitForExit(5_000);
            return process.ExitCode == 0
                ? process.StandardOutput.ReadToEnd().Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed record BenchmarkCheckpoint(
    string Name,
    DateTime CapturedAtUtc,
    SystemMemorySnapshot SystemMemory,
    ProcessSnapshot ChromeProcesses,
    JsonElement? PageSnapshot,
    bool Contaminated,
    bool SafetyTripped);

internal sealed record ProcessSnapshot(
    int ProcessCount,
    decimal TotalPrivateMemoryMb,
    decimal LargestPrivateMemoryMb,
    decimal TotalWorkingSetMb,
    IReadOnlyList<ProcessMemoryEntry> Processes,
    IReadOnlyList<ProcessMemoryEntry> TopUnrelatedProcesses)
{
    public static ProcessSnapshot Capture(string processName)
    {
        var matchingProcesses = Process.GetProcessesByName(processName)
            .Select(CreateEntry)
            .Where(entry => entry is not null)
            .Cast<ProcessMemoryEntry>()
            .OrderByDescending(entry => entry.PrivateMemoryMb)
            .ToList();
        var unrelated = Process.GetProcesses()
            .Where(process => !string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .Select(CreateEntry)
            .Where(entry => entry is not null)
            .Cast<ProcessMemoryEntry>()
            .OrderByDescending(entry => entry.PrivateMemoryMb)
            .Take(10)
            .ToList();

        return new ProcessSnapshot(
            matchingProcesses.Count,
            matchingProcesses.Sum(entry => entry.PrivateMemoryMb),
            matchingProcesses.Count == 0 ? 0m : matchingProcesses.Max(entry => entry.PrivateMemoryMb),
            matchingProcesses.Sum(entry => entry.WorkingSetMb),
            matchingProcesses,
            unrelated);
    }

    private static ProcessMemoryEntry? CreateEntry(Process process)
    {
        try
        {
            return new ProcessMemoryEntry(
                process.Id,
                process.ProcessName,
                BytesToMb(process.PrivateMemorySize64),
                BytesToMb(process.WorkingSet64),
                process.MainWindowTitle);
        }
        catch
        {
            return null;
        }
    }

    private static decimal BytesToMb(long bytes)
    {
        return Math.Round(bytes / 1024m / 1024m, 1);
    }
}

internal sealed record ProcessMemoryEntry(
    int ProcessId,
    string Name,
    decimal PrivateMemoryMb,
    decimal WorkingSetMb,
    string? WindowTitle);

internal sealed record SystemMemorySnapshot(
    decimal TotalMb,
    decimal AvailableMb,
    decimal UsedMb,
    decimal MemoryLoadPercent)
{
    public static SystemMemorySnapshot Capture()
    {
        var status = new NativeMemoryStatus();
        if (!GlobalMemoryStatusEx(status))
        {
            return new SystemMemorySnapshot(0, 0, 0, 0);
        }

        var total = BytesToMb((long)status.TotalPhys);
        var available = BytesToMb((long)status.AvailPhys);
        return new SystemMemorySnapshot(
            total,
            available,
            total - available,
            status.MemoryLoad);
    }

    private static decimal BytesToMb(long bytes)
    {
        return Math.Round(bytes / 1024m / 1024m, 1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] NativeMemoryStatus buffer);
}

[StructLayout(LayoutKind.Sequential)]
internal sealed class NativeMemoryStatus
{
    public uint Length = (uint)Marshal.SizeOf<NativeMemoryStatus>();
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}

internal static class BenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);
}
