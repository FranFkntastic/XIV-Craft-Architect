# Trade Crafter Lodestone Import Feasibility

Date: 2026-06-20
Branch: local-dev

## Goal

Determine what Trade Architect can populate automatically from public Lodestone character data before designing crafter-profile import UI or persistence changes.

This document covers observable public Lodestone data and the NetStone integration spike. It is not yet an implementation plan.

## Official Surfaces Checked

- Character Search: `https://na.finalfantasyxiv.com/lodestone/character/`
- Character Profile: `https://na.finalfantasyxiv.com/lodestone/character/{characterId}/`
- Character Class/Job: `https://na.finalfantasyxiv.com/lodestone/character/{characterId}/class_job/`

The Lodestone does not expose a stable public JSON API for these character profile pages. Any first implementation should treat this as HTML-backed retrieval with clear failure states, but it does not need to maintain a local parser while NetStone remains viable.

## NetStone Spike Results

NetStone is the recommended base technology for the first implementation.

Evidence from the spike:

- `NetStone` is available on NuGet through `1.4.1` and targets `.NETStandard2.1`, which is compatible with the app's current `.NET 8` projects.
- The GitHub repository has a newer `v1.4.2` tag and was updated in May 2026, so the project is not obviously abandoned.
- A disposable `.NET 8` console probe successfully restored `NetStone 1.4.1`, initialized `LodestoneClient.GetClientAsync()`, fetched Lodestone character `16331040`, and read character name, world, avatar URL, Free Company name, and all eight Disciples of the Hand levels.
- `LodestoneClient.SearchCharacter(...)` successfully searched by character name and world, returning a Lodestone character id that could be used for profile preview/import.
- NetStone exposes first-class class/job properties through `GetCharacterClassJob(id)`, including `Carpenter`, `Blacksmith`, `Armorer`, `Goldsmith`, `Leatherworker`, `Weaver`, `Alchemist`, and `Culinarian`.
- Each crafter job entry exposes `Level`, `Exists`, and `IsSpecialized`.

This means Craft Architect should not build and maintain a separate Lodestone parser unless NetStone fails in practice.

Important integration constraint:

- NetStone works cleanly from a normal .NET process.
- The current web app is Blazor WebAssembly-only.
- Lodestone character pages do not present permissive browser CORS headers, so browser-side direct fetch is likely blocked.
- Therefore, a web implementation should keep NetStone behind a server/proxy/local-helper boundary rather than calling it directly from Blazor WebAssembly.

## Searchable Before Import

The public character search page exposes search/filter controls and result rows that can identify candidate characters:

- character name query
- data center / home world filters
- class/job filters, including Disciples of the Hand and Disciples of the Land
- race/clan filters
- grand company filters
- primary language filters
- result rows with character name
- home world and data center
- visible current/search-relevant level
- language tags when present
- Free Company link/name when present
- character profile URL containing the Lodestone character id

This is enough to support a lookup flow like:

1. User enters character name.
2. App optionally narrows by known data center/world.
3. App shows candidate rows.
4. User selects one exact character to import.

Do not auto-import the first match without confirmation.

## Profile Data

The public character profile page can expose:

- Lodestone character id, from the URL
- display name
- title, when set
- home world
- data center
- avatar/portrait image URLs
- race
- clan
- gender
- nameday
- guardian
- city-state
- grand company and rank
- Free Company link/name when public/present
- currently displayed battle class/job level and gear/attribute summary
- user-written character profile text

For Trade Architect, the directly useful fields are:

- display name
- world
- data center
- Lodestone character id
- optional profile URL
- optional avatar URL
- optional Free Company name
- optional character profile text only as reference, not as structured data

Race, guardian, nameday, attributes, gear, mounts, minions, achievements, Eureka/Bozja/Occult progress, and combat stats are not useful for first-pass crafter assignment.

## Class And Job Data

The dedicated `class_job` page exposes a more reliable job table than the compact profile summary.

For every listed job it includes:

- category heading, such as Tank, Healer, Disciples of the Hand, Disciples of the Land
- job display name
- level, or `-` when unavailable/unleveled
- EXP text, such as `-- / --`, `0 / 421,000`, or current/next values
- tooltip text that includes base class for some jobs, such as `Paladin / Gladiator`

Disciples of the Hand are listed directly:

- Carpenter
- Blacksmith
- Armorer
- Goldsmith
- Leatherworker
- Weaver
- Alchemist
- Culinarian

Disciples of the Land are also available:

- Miner
- Botanist
- Fisher

Trade Architect only needs simple crafting job levels for the first pass. Gatherer jobs can be ignored by default, but the parser can still capture them if the durable model later wants provenance or diagnostics.

## Data Worth Importing Into Crafter Profiles

First-pass import should populate:

- `DisplayName`
- `World`
- `DataCenter`
- crafting job levels for the eight DoH jobs
- `LodestoneCharacterId`
- `LodestoneProfileUrl`
- `LodestoneLastSyncedAtUtc`

Potential but optional:

- avatar URL
- Free Company name
- last imported raw display title

Manual fields should remain manual:

- Discord/contact handle
- availability/status notes
- payment notes
- reliability/operator notes

## Storage Implications

Existing `TradeCrafterProfile` should gain Lodestone provenance fields rather than replacing manual entry:

- `string? LodestoneCharacterId`
- `string? LodestoneProfileUrl`
- `DateTime? LodestoneLastSyncedAtUtc`
- `string? LodestoneAvatarUrl`
- `string? LodestoneFreeCompanyName`

The imported job levels should write into the same simple per-job level storage already used by manual crafter profiles.

Manual edits must remain possible after import. A future sync action should update Lodestone-owned fields and job levels, but it should not erase local notes/payment/contact fields.

## Reliability Concerns

Risks:

- Lodestone HTML is not an API contract.
- Class/job ordering and CSS classes can change.
- Some character data may be hidden, absent, renamed, or localized.
- Search can produce ambiguous names.
- Lodestone availability/rate limiting should be respected.
- Client-side browser CORS may block direct fetches from Blazor WebAssembly.

Mitigations:

- Require user confirmation from search results.
- Store the Lodestone character id after selection.
- Prefer parsing named job rows from the `class_job` page over relying on icon order.
- Treat import as best-effort and show explicit parse failures.
- Keep manual crafter creation and editing fully available.
- Consider a small serverless/proxy route only if browser CORS blocks direct retrieval.

## Recommended First Implementation Slice

1. Add Lodestone provenance fields to `TradeCrafterProfile`.
2. Add a project-owned `ILodestoneCrafterLookupService` boundary that returns a `LodestoneCrafterProfileImport`.
3. Implement the first lookup provider with NetStone, not a local parser.
4. Add a small contract test or integration probe around the NetStone mapping layer using known public Lodestone data, with network-dependent tests clearly isolated from the normal fast test suite.
5. Add a Crafters page import dialog:
   - search by name
   - select candidate
   - preview imported DoH levels
   - create or update crafter
6. Save imported crafters through existing Trade persistence.
7. If Blazor WebAssembly cannot call the lookup service directly because of CORS, add a minimal server/proxy endpoint or local helper before wiring the UI.

Do not wire automatic background sync in the first slice.

## Open Questions

- Should imported crafter profiles display a small Lodestone-linked badge?
- Should re-sync overwrite manually edited job levels, or should imported and manual levels be tracked separately?
- Should world/data center changes from Lodestone update existing crafters automatically?
- Should the first implementation support direct profile URL paste before name search?
- What is the smallest acceptable NetStone execution host for the web app: hosted endpoint, local helper, or deferred desktop-only import?

## Dirty Spike Runtime Result

Direct NetStone lookup from Blazor WebAssembly failed in local development because the browser blocked Lodestone requests through `BrowserHttpHandler` / `TypeError: Failed to fetch`.

The implemented spike still leaves the useful durable pieces in place:

- `ILodestoneCrafterLookupService` is the replacement boundary.
- Lodestone provenance fields are stored on `TradeCrafterProfile`.
- `TradeCrafterProfileImportMapper` maps imported identity/job levels without erasing manual contact, payment, availability, or operator notes.
- The Crafters page import panel renders and reports browser-blocked lookup failures clearly.

Next implementation slice should keep the UI and core mapper, then replace or wrap `NetStoneLodestoneCrafterLookupService` with a lookup host that runs NetStone outside the browser. The likely options are a small hosted endpoint, a local helper service, or a future desktop-side bridge.

## Local Helper Transport Result

The next implementation slice replaced the direct browser NetStone transport with a localhost HTTP transport:

- `FFXIV Craft Architect.Web` now calls `ILodestoneCrafterLookupService` through `HttpLodestoneCrafterLookupService`.
- The web lookup base address is configured by `wwwroot/appsettings.json` under `LodestoneLookup:BaseAddress`, defaulting to `http://localhost:5128/`.
- `FFXIV Craft Architect.LodestoneLookup` is a small ASP.NET Core helper host that runs NetStone outside the browser.
- The helper exposes:
  - `GET /lodestone/crafters/search?name=...&world=...&dataCenter=...`
  - `GET /lodestone/crafters/{characterId}/preview`
- Local CORS allows the web app origins on ports `5000` and `5001`.

Runtime verification:

- `http://localhost:5128/` returns a ready payload.
- Searching `Level Checker` on `Behemoth` returns Lodestone character id `16331040`.
- Previewing `16331040` returns eight DoH job levels at `100`.
- A request with `Origin: http://localhost:5001` returns `Access-Control-Allow-Origin: http://localhost:5001`.

This makes the local helper the working development/private-use transport. A future VPS-hosted version should preserve the same endpoint shape and only change `LodestoneLookup:BaseAddress`.

## Local Launcher

For personal/local use, start the Trade web app and Lodestone lookup helper together with:

```powershell
.\run-trade-local.ps1
```

Useful options:

```powershell
.\run-trade-local.ps1 -StopExisting
.\run-trade-local.ps1 -NoBrowser
.\run-trade-local.ps1 -NoBuild
.\run-trade-local.ps1 -WebPort 5001 -LookupPort 5128
```

The script starts:

- web app: `http://localhost:5001`
- Lodestone helper: `http://localhost:5128`

It writes process logs under `.devlogs/trade-local` and stops both child processes when the script exits.

The executable wrapper can be run from source with:

```powershell
dotnet run --project "src\FFXIV Craft Architect.LocalLauncher\FFXIV Craft Architect.LocalLauncher.csproj" -- -StopExisting
```

The wrapper delegates to `run-trade-local.ps1`, so the script remains the single source of startup behavior. A future packaged build can publish the wrapper as an `.exe` without changing the local stack design.
