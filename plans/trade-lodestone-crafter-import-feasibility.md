# Trade Crafter Lodestone Import Feasibility

Date: 2026-06-20
Branch: local-dev

## Goal

Determine what Trade Architect can populate automatically from public Lodestone character data before designing crafter-profile import UI or persistence changes.

This document covers observable public Lodestone data only. It is not yet an implementation plan.

## Official Surfaces Checked

- Character Search: `https://na.finalfantasyxiv.com/lodestone/character/`
- Character Profile: `https://na.finalfantasyxiv.com/lodestone/character/{characterId}/`
- Character Class/Job: `https://na.finalfantasyxiv.com/lodestone/character/{characterId}/class_job/`

The Lodestone does not expose a stable public JSON API for these character profile pages. Any first implementation should treat this as HTML retrieval/parsing with clear failure states.

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
2. Add a pure parser service that accepts HTML strings and returns a `LodestoneCrafterProfileImport`.
3. Add parser fixtures from saved Lodestone profile and class/job HTML samples.
4. Add a lookup service that fetches search/profile/class-job pages.
5. Add a Crafters page import dialog:
   - search by name
   - select candidate
   - preview imported DoH levels
   - create or update crafter
6. Save imported crafters through existing Trade persistence.

Do not wire automatic background sync in the first slice.

## Open Questions

- Should imported crafter profiles display a small Lodestone-linked badge?
- Should re-sync overwrite manually edited job levels, or should imported and manual levels be tracked separately?
- Should world/data center changes from Lodestone update existing crafters automatically?
- Should the first implementation support direct profile URL paste before name search?
- Do we need a proxy endpoint for browser CORS, or can the app fetch directly in current deployment modes?
