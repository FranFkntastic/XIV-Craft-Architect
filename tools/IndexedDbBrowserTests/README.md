# IndexedDB and full-workflow browser verification

Install once with `npm install`, then run `npm test` for the production-`indexedDB.js`
regressions in Chromium and Firefox plus the pure full-workflow oracle reliability tests.

`crasher-verify.mjs` is the strict product workflow verifier. Its default `seeded` mode
intercepts Universalis with deterministic complete listings while exercising the real Blazor
import/build, cache, analysis, publication, procurement, persistence, rendering, navigation,
and reload paths:

```powershell
node tools/IndexedDbBrowserTests/crasher-verify.mjs chromium http://127.0.0.1:5083 C:/Users/gianf/Downloads/crasher.craftplan C:/tmp/crasher-chromium.json seeded
node tools/IndexedDbBrowserTests/crasher-verify.mjs firefox  http://127.0.0.1:5083 C:/Users/gianf/Downloads/crasher.craftplan C:/tmp/crasher-firefox.json seeded
```

Pass `live` as the final argument for a live-network smoke. Run engines sequentially.

## Completion contract

Success requires an expanded recipe graph before analysis and matching structured
plan-session, market-publication, and route-basis identities. It also requires cache activity,
analysis/publication stages, a current route decision, autosave readback with market state,
no scheduled/running reconciliation, no active operation/progress/dirty persistence buckets,
a two-second settled window, clean console/page/network diagnostics, responsive route
inspection/navigation, and successful autosave restoration plus route regeneration after
reload. A visible result table or absence of `ANALYZING` alone is not success.

The reliability watchdog does not weaken those gates. It only classifies impossible or stalled
states sooner and preserves the latest evidence while doing so.

## Default budgets

Seeded runs use these phase budgets:

| Phase | Budget |
|---|---:|
| Whole run | 180 seconds |
| Import and graph expansion | 90 seconds |
| Analysis through autosave | 60 seconds |
| Route workflow return | 30 seconds |
| Route return through authoritative settled state | 10 seconds |
| Post-completion navigation | 30 seconds |
| Reload/restoration | 60 seconds |
| No meaningful progress | 30 seconds |
| Individual browser operation | 10 seconds |
| Browser shutdown | 5 seconds |

Live mode raises the whole-run, import, analysis, route-return, reload, and stall budgets, but
keeps the ten-second terminal-route contradiction window. A route workflow that reports
`Published` with a real decision but leaves no current route basis therefore fails in seconds
rather than consuming the whole-run budget.

For deliberate slow diagnostics, override a budget with an integer millisecond environment
variable:

- `CA_ORACLE_GLOBAL_TIMEOUT_MS`
- `CA_ORACLE_IMPORT_TIMEOUT_MS`
- `CA_ORACLE_ANALYSIS_TIMEOUT_MS`
- `CA_ORACLE_ROUTE_RETURN_TIMEOUT_MS`
- `CA_ORACLE_ROUTE_SETTLE_TIMEOUT_MS`
- `CA_ORACLE_NAVIGATION_TIMEOUT_MS`
- `CA_ORACLE_RELOAD_TIMEOUT_MS`
- `CA_ORACLE_STALL_TIMEOUT_MS`
- `CA_ORACLE_BROWSER_OPERATION_TIMEOUT_MS`
- `CA_ORACLE_CLOSE_TIMEOUT_MS`
- `CA_ORACLE_HEARTBEAT_MS`
- `CA_ORACLE_SETTLED_WINDOW_MS`

Overrides are range-checked. The route-settlement budget cannot exceed the route-return
budget, and the stall budget must remain shorter than the whole-run budget.

## Failure classifications and artifacts

The JSON report is atomically replaced on meaningful progress and periodic heartbeats. It
always includes the current phase, configured budgets, latest lifecycle/autosave snapshot,
route terminal evidence, missing completion gates, last progress fingerprint, and pending
requests.

Expected terminal classifications include:

- `terminal-failure` — the application explicitly reported a failed route or analysis;
- `terminal-contradiction` — terminal workflow evidence conflicts with authoritative AppState;
- `stalled` — no lifecycle, console, network, or persistence progress occurred;
- `phase-timeout` — one bounded phase exceeded its budget;
- `global-timeout` / `global-watchdog` — the whole verifier exceeded its hard limit;
- `browser-operation-timeout` — a Playwright or browser-side call stopped responding;
- `browser-diagnostics` — page, console, or request evidence was not clean.

Normal and failure cleanup only closes the browser instance launched by the verifier. If
Playwright shutdown itself hangs, a bounded watchdog writes the final report and exits the
verifier rather than leaving an unbounded `page.evaluate` or detached browser job behind.
