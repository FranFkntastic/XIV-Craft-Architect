# FFXIV Craft Architect - Session Context

## Date: 2026-02-01

---

## Summary of Changes

### 1. Artisan Craft List Export Support
**Feature:** Export crafting plans to Artisan format for use with the Artisan Dalamud plugin.

**Implementation:**
- New `ArtisanService` class handles conversion from CraftingPlan to Artisan JSON format
- New `ArtisanModels` with `ArtisanCraftingList`, `ArtisanListItem`, and `ArtisanListItemOptions`
- Converts Item IDs to Recipe IDs using Garland Tools API lookups
- Calculates proper recipe quantities based on recipe yield
- Exports to clipboard as JSON for Artisan's "Import List From Clipboard" feature

**Files Added:**
- `Models/ArtisanModels.cs` - Data models for Artisan JSON format
- `Services/ArtisanService.cs` - Export service with async recipe lookup

**Files Modified:**
- `App.xaml.cs` - Registered `ArtisanService` in DI container
- `MainWindow.xaml` - Added "Export to Artisan" button
- `MainWindow.xaml.cs` - Added `OnExportArtisan` event handler

**Usage:**
1. Build a project plan with items to craft
2. Click "Export to Artisan" button
3. In FFXIV with Artisan installed, click "Import List From Clipboard (Artisan Export)"
4. The crafting list will be imported with correct recipe IDs and quantities

---

### 2. Cross-DC Search Timeout Fix (504 Gateway Timeout) - Previous Session
**Problem:** "Search all NA DCs" was failing with 504 Gateway Timeout errors when hitting all 4 DCs simultaneously.

**Solution:**
- Changed from parallel to **sequential fetching** with 100ms delay between DCs
- Added **retry logic** with exponential backoff (3 retries, 10s timeout each)
- Added per-DC error handling to continue if one DC fails
- Handles 504/502/503/408/429 errors gracefully

**Files Modified:**
- `Services/MarketShoppingService.cs`

---

### 3. Auto-Fetch Prices on Plan Build - Previous Session
**Feature:** Automatically fetch market prices after building a project plan.

**Implementation:**
- Added `planning.auto_fetch_on_build` setting (default: `true`)
- Non-blocking async operation using `Dispatcher.InvokeAsync`
- User can disable via `settings.json`

**Files Modified:**
- `Services/SettingsService.cs` - Added default setting
- `MainWindow.xaml.cs` - `OnBuildProjectPlan()` triggers auto-fetch

---

### 4. UI Text and Tooltip Improvements - Previous Session

#### Recommendation Mode Rename
- Changed display text from "Minimize Cost" to "Minimize Total Cost"
- Updated tooltip descriptions

#### Fixed Tooltips
- Added individual `ToolTip` attributes to each `ComboBoxItem`
- Tooltips now show on hover when dropdown is open

#### Removed Best Per-Unit Price Mode
- Removed from dropdown UI (enum value still exists for future use)
- Simplified `GetCurrentRecommendationMode()` to handle only 2 modes
- Users didn't find value in a mode that ignores quantity requirements

**Files Modified:**
- `MainWindow.xaml` - Dropdown items and tooltips
- `MainWindow.xaml.cs` - Mode selection logic

---

### 5. Right Panel Background Fix - Previous Session
**Problem:** Right panel (Recipe Plan/Market Logistics tabs) showed blinding white background when no content or short content.

**Root Cause:** WPF-UI's `TabView` control has default white background for its content presenter area.

**Solution:**
- Set `Background="#2d2d2d"` on the `TabView` element
- Matches the dark theme of the rest of the application

**Attempted but Reverted:**
- Tried stacked layout (vertical) instead of tabs
- Scrolling issues and loss of collapsible sections
- Tabbed version restored from backup

**Files Modified:**
- `MainWindow.xaml` - TabView background property

---

### 6. Refresh Button State Fix - Previous Session
**Problem:** Refresh Market Data button could get stuck disabled after errors.

**Solution:**
- Moved button re-enable to `finally` block in `UpdateMarketLogisticsAsync()`
- Ensures button is always re-enabled regardless of success/failure

**Files Modified:**
- `MainWindow.xaml.cs`

---

## Architecture Notes

### Settings System
Settings are stored in `settings.json` with dot-notation paths:
```csharp
_settingsService.Get<bool>("planning.auto_fetch_on_build")
```

### Recommendation Modes
The `RecommendationMode` enum has 3 values but only 2 are exposed in UI:
- `MinimizeTotalCost` - Default, minimizes total gil spent
- `MaximizeValue` - Best price per unit, may buy excess
- `BestUnitPrice` - (Hidden) Lowest per-unit price regardless of quantity

### Market Shopping Service
Two main methods for calculating shopping plans:
- `CalculateDetailedShoppingPlansAsync()` - Single DC search
- `CalculateDetailedShoppingPlansMultiDCAsync()` - All NA DCs with retry logic

Both support the `RecommendationMode` parameter.

### Artisan Export Format
Artisan uses a JSON format with the following structure:
```json
{
  "ID": 12345,
  "Name": "My Crafting List",
  "Recipes": [
    {
      "ID": 1234,
      "Quantity": 5,
      "ListItemOptions": { "NQOnly": false, "Skipping": false }
    }
  ],
  "ExpandedList": [1234, 1234, 1234, 1234, 1234],
  "SkipIfEnough": false,
  "Materia": false,
  "Repair": false,
  "RepairPercent": 50,
  "AddAsQuickSynth": false,
  "TidyAfter": true,
  "OnlyRestockNonCrafted": false
}
```

**Key Points:**
- Recipe `ID` is the Recipe ID, not the Item ID
- `Quantity` is the number of recipe executions, not total items
- `ExpandedList` contains the Recipe ID repeated for each execution

---

## Backup Files
- `MainWindow.xaml.tabbed-backup` - Preserves original TabView layout

---

## Known Issues / Future Work

1. **Settings UI** - No UI to toggle `auto_fetch_on_build`; users must edit `settings.json`
2. **TabView Theming** - The fix works but is a workaround; proper WPF-UI theming would be cleaner
3. **Options Menu** - Requested but not implemented; would house advanced settings like hidden recommendation modes
4. **Artisan Recipe Selection** - Currently selects the lowest level recipe; could offer choice when multiple recipes exist

---

## Build Status
✅ Build successful (12 warnings - all obsolete `IsUncraftable` property usage)
