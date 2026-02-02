# FFXIV Craft Architect - Session Context

## Date: 2026-02-01 to 2026-02-02

## Session Status: ✅ COMPLETE - Milestone Commit Ready

---

## Summary of Changes

### 1. Bidirectional Artisan Import/Export Support
**Feature:** Full import and export support for Artisan (FFXIV Dalamud plugin) crafting lists.

**Export (CraftingPlan → Artisan):**
- Converts Item IDs to Recipe IDs using Garland Tools API
- Calculates recipe quantities based on yield
- Exports to clipboard as JSON

**Import (Artisan → CraftingPlan):**
- Parses Artisan JSON format
- **Uses XIVAPI to resolve Recipe IDs to Item IDs** (critical fix - Recipe ID ≠ Item ID in FFXIV)
- Creates proper PlanNode hierarchy with correct item data

**Files Added:**
- `Models/ArtisanModels.cs` - Data models for Artisan JSON
- `Services/ArtisanService.cs` - Import/export service

**Files Modified:**
- `MainWindow.xaml` - Added Import/Export menu items
- `MainWindow.xaml.cs` - Added handlers for Artisan import/export

---

### 2. Menu Bar Restructure
**Change:** Moved Import/Export buttons from Project Items panel to proper menu bar.

**New Menu Structure:**
- **File:** New, Save, Load, Logs
- **Import:** Teamcraft, Artisan
- **Export:** Teamcraft, Artisan, Text, CSV
- **Tools:** Fetch Prices, Options

**Files Modified:**
- `MainWindow.xaml` - Complete menu redesign
- `MainWindow.xaml.cs` - Menu event handlers

---

### 3. Options Window (In-App Settings)
**Feature:** Settings dialog accessible via Tools → Options.

**Tabs:**
- **Appearance:** Accent color picker with presets
- **Market:** Default DC, Auto-fetch prices, Cross-world data (hidden)
- **Planning:** Default recommendation mode
- **Live Mode:** (Hidden - feature not implemented)

**Files Added:**
- `OptionsWindow.xaml` - Settings UI
- `OptionsWindow.xaml.cs` - Settings logic

---

### 4. Debug Log Viewer
**Feature:** Enhanced log window with filtering and search.

**Capabilities:**
- Search functionality (Ctrl+F, F3 for next)
- Log level filters (Debug, Info, Warning, Error)
- Color-coded log levels
- Auto-scroll toggle
- Export logs to file
- Clear log buffer

**Files Added:**
- `LogViewerWindow.xaml` - Log viewer UI
- `LogViewerWindow.xaml.cs` - Log display logic

---

### 5. UI Cleanup - Hidden Unimplemented Features
**Change:** Hidden UI elements for features not yet implemented to reduce confusion.

**Hidden Elements:**
| Element | Location | Reason |
|---------|----------|--------|
| "Sync Teamcraft Inventory" button | MainWindow (left panel) | Stub only |
| "Live Mode" toggle | MainWindow (left panel) | Stub only |
| "Inventory: Not Synced" status | MainWindow (left panel) | Related to sync |
| "View Inventory" menu | Tools menu | Stub only |
| "Include cross-world data" toggle | Options → Market | Setting saved but unused |
| "Live Mode" tab | Options window | Feature not implemented |

**Note:** All elements hidden with `Visibility="Collapsed"` - can be re-enabled when features are implemented.

**Files Modified:**
- `MainWindow.xaml`
- `OptionsWindow.xaml`
- `OptionsWindow.xaml.cs` (commented out code-behind references)

---

### 6. Recipe Plan UI Cleanup
**Change:** Removed icons (⚒, 🛒, 🏪, •) from recipe plan items.

**Reason:** Visual noise reduction - the acquisition source is already clear from the dropdown and text color.

**Files Modified:**
- `MainWindow.xaml.cs` - Removed icon blocks from `CreateLeafNodePanel()` and `CreateNodeHeaderPanel()`

---

### 7. Build/Publish Script
**Added:** `publish.bat` for creating distribution builds.

**Usage:**
```batch
publish.bat [version] [configuration]
publish.bat 0.2.0 Release    # Full example
```

**Features:**
- Cleans previous builds
- Restores packages
- Publishes single-file self-contained executable
- Enables compression
- Copies output to `dist\v{version}\`

**Output:**
- `dist\v{version}\FFXIV_Craft_Architect.exe`

---

### 8. Default Recommendation Mode Fix
**Problem:** The default recommendation mode setting wasn't being saved or loaded.

**Fix:**
- Added `planning.default_recommendation_mode` to `DefaultSettings` in `SettingsService.cs`
- Added load/save code in `OptionsWindow.xaml.cs`
- Added initialization in `MainWindow.xaml.cs` `OnLoaded()` to apply setting at startup

**Files Modified:**
- `Services/SettingsService.cs` - Added default setting
- `OptionsWindow.xaml.cs` - Load and save the setting
- `MainWindow.xaml.cs` - Apply setting on startup

---

### 9. Market Logistics UI Polish
**Changes:** Improved information density and made summary collapsible.

**Purchase Summary Card:**
- Now collapsible (like Craft vs Buy Analysis)
- Shows totals in header: "12 items • 45,230g total"
- Expanded content shows vendor/market breakdown

**Individual Item Cards - More Compact:**
| Before | After |
|--------|-------|
| Padding: 12px | Padding: 8px horizontal, 6px vertical |
| Margin: 8px bottom | Margin: 4px bottom |
| Font size: 13px name | Font size: 12px name |
| Multi-line header | Single-line header |
| "Click to expand ▼" hint | Removed (cursor indicates clickable) |
| "[RECOMMENDED]" badge | "★" icon |
| "[GOOD VALUE]" badge | "✓" icon |
| "All World Options:" header | "All Worlds:" (smaller, gray) |
| Listing font: 10px | Listing font: 9px |
| Listing margins: default | Listing margins: 1px bottom |

**World Option Panels:**
- Reduced padding: 8px → 6px/4px
- Reduced margins: 4px → 2px
- Compact cost display: "12,000g total • 5 excess"
- Streamlined listing rows

**Files Modified:**
- `MainWindow.xaml` - Changed Border to Expander for summary
- `MainWindow.xaml.cs` - Updated `UpdateMarketSummaryCard()`, `CreateExpandableMarketCard()`, `CreateWorldOptionPanel()`

---

### 10. HQ Toggle Moved to Project Items - UI Polish
**Change:** Moved the HQ requirement toggle from Recipe Plan to Project Items pane, with improved layout.

**Why:** Better UX - users specify "I want to craft these as HQ" when adding items, not after building the plan.

**Project Items Layout:**
```
[×] [Item Name.........] [HQ] [Qty]
```
- Delete button: Small red × on the left
- HQ checkbox: Compact, centered, gold text
- Quantity: Narrower field (60px)

**Implementation:**
- Added `IsHqRequired` property to `ProjectItem` class
- Added HQ checkbox to Project Items list template
- Updated `BuildPlanAsync` to accept and apply HQ requirements

**Files Modified:**
- `MainWindow.xaml.cs` - `ProjectItem` class, XAML template, BuildPlan call
- `MainWindow.xaml` - Updated Project Items ListBox template
- `Services/RecipeCalculationService.cs` - Updated `BuildPlanAsync` signature

---

### 11. Recipe Plan - HQ Visual Refresh
**Change:** Removed HQ toggle from Recipe Plan, replaced with cleaner visual indicator.

**Before:**
- HQ Toggle checkbox cluttered the UI
- `[HQ]` text appended to item names

**After:**
- Gold star (★) to the left of HQ item names
- Item name uses accent color when HQ is required
- Much cleaner, less cluttered appearance

**Files Modified:**
- `MainWindow.xaml.cs` - Updated `CreateLeafNodePanel()`, `CreateNodeHeaderPanel()`, `CreateTreeViewItem()`

---

### 12. Craft vs Buy Analysis - HQ Awareness
**Change:** Craft vs Buy Analysis now considers HQ requirements when making recommendations.

**Behavior:**
- If item has `MustBeHq` set: Uses HQ prices for primary recommendation
- If item is NQ: Uses NQ prices for primary recommendation (HQ shown as alternate)
- Header shows count of items requiring HQ
- Items marked with `[HQ Required]` in gold text

**New Properties on `CraftVsBuyAnalysis`:**
- `IsHqRequired` - Whether HQ is required for this item
- `EffectiveRecommendation` - Uses HQ or NQ based on requirement
- `EffectivePotentialSavings` - Savings based on requirement
- `EffectiveSavingsPercent` - Percent based on requirement

**Files Modified:**
- `Models/MarketShoppingModels.cs` - Added HQ-aware properties
- `Services/MarketShoppingService.cs` - Sets `IsHqRequired` from node
- `MainWindow.xaml.cs` - Updated analysis display

---

### 12. ComboBox Auto-Width Behavior
**Change:** Created reusable attached behavior to dynamically size ComboBoxes based on their widest content.

**Usage:**
```xml
<ComboBox helpers:ComboBoxAutoWidthBehavior.EnableAutoWidth="True">
    <ComboBoxItem Content="Long text here" />
</ComboBox>
```

**Applied to:**
- `MainWindow.xaml` - MarketSortCombo, MarketModeCombo
- `OptionsWindow.xaml` - DefaultDataCenterCombo, DefaultRecommendationModeCombo

**Benefits:**
- No more hardcoded widths
- Automatically adjusts when items change
- Reusable across the entire application

**Files Added:**
- `Helpers/ComboBoxAutoWidthBehavior.cs` - Attached behavior implementation

**Files Modified:**
- `MainWindow.xaml` - Applied behavior to market logistics dropdowns
- `OptionsWindow.xaml` - Applied behavior to settings dropdowns

---

### 13. Market Logistics Cards - Accent Color Support
**Change:** Market Logistics cards now use the accent color instead of hardcoded gold/olive colors.

**Before:**
- Cards used hardcoded olive colors (#3d3e2d, #4a4a3a)
- World names displayed in gold
- Recommended star in gold

**After:**
- Card backgrounds use muted accent color variations
- World names use the accent color
- Recommended star uses the accent color
- Cards dynamically adapt when accent color changes

**New Helper Methods:**
- `GetAccentColor()` - Gets accent color as Color
- `GetMutedAccentBrush()` - Darker version for card backgrounds  
- `GetMutedAccentBrushLight()` - Lighter version for headers
- `GetMutedAccentBrushExpanded()` - Expanded state color

**Files Modified:**
- `MainWindow.xaml.cs` - Updated `CreateExpandableMarketCard()`, `CreateWorldOptionPanel()`, added helper methods

---

## Active Issues (Resolved)

### ✅ Fixed: "Refresh Market Data" Button Disabled on Plan Load
**Problem:** Loading a plan with `SavedMarketPlans` didn't enable the refresh button.

**Fix:** Added `RefreshMarketButton.IsEnabled = true;` in `DisplayPlanWithCachedPrices()` when saved market plans exist.

---

### ✅ Fixed: Craft Cost Calculation
**Problem:** Craft costs were including child costs regardless of acquisition source.

**Fix:** `CalculateCraftingCost()` now returns 0 for nodes with `Source != AcquisitionSource.Craft`.

---

### ✅ Fixed: Price Fetch Error Handling
**Problem:** Failed price fetches would clear cached prices and show misleading status.

**Fix:** Preserves cached prices on timeout/failure, shows accurate status messages (complete/partial/failed).

---

## Next Steps

1. **GitHub Releases:** Set up automated releases with version tags
2. **Update Checker:** Implement version checking against GitHub releases
3. **Live Mode:** Implement packet capture for real-time inventory (long term)
4. **Inventory Sync:** Implement Teamcraft inventory synchronization

---

## Session Summary

This session focused on UI polish, bug fixes, and feature refinement:

**Major Features Added:**
- Bidirectional Artisan import/export
- In-app Options window with live theming
- Debug log viewer with filtering
- Market shopping plans with world recommendations
- Craft vs Buy analysis with HQ/NQ pricing
- Cross-DC travel optimization

**UI/UX Improvements:**
- HQ toggle moved to Project Items pane
- Recipe plan shows star + accent color for HQ items
- Compact Market Logistics cards
- Collapsible Purchase Summary
- Auto-sizing ComboBox dropdowns
- Silent save for settings

**Bugs Fixed:**
- Refresh button disabled on plan load
- Craft cost calculation respecting acquisition source
- Price fetch error handling preserving cached data
- Default recommendation mode saving/loading

---

## Version History

| Version | Date | Notes |
|---------|------|-------|
| 0.2.0 | 2026-02-02 | UI polish, Artisan support, theming improvements |
| 0.1.0 | 2026-02-01 | Initial release with core features |
