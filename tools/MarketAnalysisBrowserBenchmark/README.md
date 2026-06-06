# Market Analysis Browser Benchmark

Reusable browser/process measurement harness for FFXIV Craft Architect market-analysis
stabilization work.

The tool is intentionally outside production code. It records machine and browser state
around a supervised browser run, then writes a JSON report that can be compared across
cache states and branches.

## Build

```powershell
dotnet build "tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj"
```

## Process-Only Smoke

Use this when Chrome DevTools is not running, or when the machine is under unrelated load
and you only want to record system/browser process state.

```powershell
dotnet run --project "tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj" -- --process-only --scenario ssuc --cache-state warm --system-profile ffxiv-open --label phase2-process-smoke --output "C:\tmp\market-analysis-browser-benchmark-phase2-process-smoke.json"
```

## Browser Run

Start the web app and Chrome with a DevTools port first. Then run:

```powershell
dotnet run --project "tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj" -- --url http://localhost:5000 --devtools http://127.0.0.1:9223 --navigate --scenario ssuc --cache-state warm --output "C:\tmp\ssuc-browser-benchmark.json"
```

For repeated local samples, prefer the wrapper script. It builds the harness once, runs
the compiled DLL, launches a scoped Chrome profile when needed, and cleans up the scoped
Chrome process after the run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Run-MarketAnalysisBrowserBenchmark.ps1" -HarnessCommandLine "--navigate --enable-developer-mode --scenario app-shell --cache-state warm --system-profile clean --output C:\tmp\app-shell.json"
```

The browser runner can also drive simple app interactions before capturing the report:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Run-MarketAnalysisBrowserBenchmark.ps1" -HarnessCommandLine "--navigate --enable-developer-mode --import-native-plan \"C:\Users\gianf\Downloads\Unmod comparison sheet.craftplan\" --post-action-delay-seconds 8 --scenario clean-unmod-imported --cache-state warm --system-profile clean --output C:\tmp\market-analysis-browser-benchmark-clean-unmod-imported.json"
```

Interaction options are intentionally small and boring:

- `--navigate` opens the supplied URL before measurement.
- `--wait-selector` waits for a CSS selector before the report is captured.
- `--enable-developer-mode` persists the app's Developer Mode setting in the scoped
  browser profile, reloads, and waits for benchmark hooks to appear.
- `--click-benchmark-id` clicks an enabled element with a matching `data-benchmark-id`.
- `--click-button-text` clicks a visible button by text.
- `--click-element-text` clicks the first visible element matching the text.
- `--import-native-plan` sets the native craftplan file input and waits for import to
  settle.
- `--post-action-delay-seconds` waits after the last action so async UI work can progress.

Current debug-gated benchmark hook ids:

- `main-import-menu`
- `main-import-native-plan`
- `main-nav-market-analysis`
- `market-analysis-run`
- `market-analysis-refresh-prices`

The harness records:

- branch, commit, dirty state, URL, scenario, cache state, and plan file;
- system profile, such as `clean` or `ffxiv-open`;
- system memory and top unrelated processes;
- Chrome process count, total private memory, largest private memory, and working set;
- DOM counts, table counts, expanded element counts, status text, IndexedDB database
  count, and `performance.memory` when DevTools is available;
- safety-trip and contamination flags.

Safety defaults:

- total Chrome private memory over `7000 MB` trips the safety flag;
- largest Chrome process private memory over `2500 MB` trips the safety flag;
- available system memory under `4096 MB` or a large unrelated process marks the run
  contaminated.
- with `--system-profile ffxiv-open`, `ffxiv_dx11` is treated as part of the expected
  operating profile instead of automatic contamination.
