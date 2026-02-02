# FFXIV Craft Architect - C# Edition

**Project:** C# fork of the Python-based FFXIV Craft Architect  
**Goal:** Replicate functionality with modern C#/.NET while maintaining code quality standards  
**Status:** Active Development - Core Features Complete

---

## Project Overview

This is a C# reimplementation of the FFXIV Craft Architect application, which calculates crafting costs in Final Fantasy XIV with live inventory tracking via DLL injection.

### Original Python Stack
- **GUI:** customtkinter (modern tkinter wrapper)
- **HTTP:** requests library
- **Windows API:** pywin32 + ctypes
- **Images:** Pillow (PIL)
- **Build:** PyInstaller

### Target C# Stack
- **Framework:** .NET 8 (LTS)
- **GUI:** WPF with WPF-UI 3.0.4 (WinUI style)
- **HTTP:** HttpClient (built-in)
- **Windows API:** P/Invoke
- **Images:** System.Drawing or SkiaSharp
- **Build:** Single-file publish with AOT

---

## Architecture Mapping (Python → C#)

| Python Module | C# Namespace | Responsibility |
|--------------|--------------|----------------|
| `ffxiv_craft_architect.py` | `FFXIVCraftArchitect` | Main application, main window |
| `live_inventory.py` | `FFXIVCraftArchitect.LiveInventory` | Live inventory tracking manager |
| `packet_capture.py` | `FFXIVCraftArchitect.PacketCapture` | DLL injection, named pipe communication |
| `inventory_helpers.py` | `FFXIVCraftArchitect.Inventory` | Container mappings, utilities |
| `settings_manager.py` | `FFXIVCraftArchitect.Settings` | Settings persistence |

---

## File Structure

```
FFXIV Craft Architect C# Edition/
├── src/
│   └── FFXIVCraftArchitect/           # Main project
│       ├── FFXIVCraftArchitect.csproj
│       ├── App.xaml                   # Application entry (WPF-UI Dark theme)
│       ├── App.xaml.cs
│       ├── MainWindow.xaml            # Main GUI (WPF-UI TabView)
│       ├── MainWindow.xaml.cs
│       ├── MainWindow.xaml.tabbed-backup  # Backup of original tab layout
│       ├── Models/                    # Data models
│       │   ├── InventoryItem.cs
│       │   ├── CraftingPlan.cs        # Plan with AcquisitionSource enum
│       │   ├── MarketListing.cs
│       │   ├── MarketShoppingModels.cs # DetailedShoppingPlan, WorldShoppingSummary
│       │   ├── Recipe.cs
│       │   └── WorldData.cs
│       ├── Services/                  # Business logic
│       │   ├── GarlandService.cs      # Garland Tools API
│       │   ├── UniversalisService.cs  # Universalis API
│       │   ├── SettingsService.cs     # settings.json persistence
│       │   ├── MarketShoppingService.cs # Shopping plan calculation
│       │   ├── RecipeCalculationService.cs
│       │   ├── PlanPersistenceService.cs # Save/load plans
│       │   ├── TeamcraftService.cs    # Import/export
│       │   └── ArtisanService.cs      # Artisan export
│       ├── Views/                     # Additional windows
│       │   ├── InventoryWindow.xaml
│       │   ├── LogsWindow.xaml
│       │   └── PlansWindow.xaml
│       └── Lib/                       # Native dependencies
│           └── deucalion.dll          # Injected DLL
├── settings.json                      # User settings (auto_fetch_on_build, etc.)
├── CONTEXT.md                         # Session context / changelog
└── AGENTS.md                          # This file
```

---

## Phase-by-Phase Implementation Plan

### Phase 1: Foundation ✅ (Complete)
- [x] Initialize Git repository
- [x] Create AGENTS.md with action plan
- [x] Create solution structure (.sln + projects)
- [x] Set up project dependencies
- [x] Create base Models (Item, Recipe, Container, etc.)
- [x] Initial commit with project skeleton

### Phase 2: Core Services ✅ (Complete)
- [x] Implement Garland Tools API service
- [x] Implement Universalis API service
- [x] Implement Settings service
- [x] Add retry logic for API timeouts (504 Gateway Timeout handling)

### Phase 3: GUI (WPF) ✅ (Complete)
- [x] Create MainWindow layout
- [x] Implement DC/World selector
- [x] Implement item search with results
- [x] Implement recipe tree view
- [x] Implement market logistics cards
- [x] Add dark theme styling
- [x] TabView for Recipe Plan / Market Logistics

### Phase 4: Inventory Management ✅ (Complete)
- [x] Port container mappings (inventory_helpers.py)
- [x] Implement InventoryManager class
- [x] Implement inventory cache
- [x] Add inventory viewer window

### Phase 5: Live Mode (DLL Injection) ✅ (Complete)
- [x] Port DLL injector (ctypes → P/Invoke)
- [x] Implement named pipe communication
- [x] Port packet parsing logic
- [x] Implement LiveInventoryManager
- [x] Add admin privilege handling

### Phase 6: Polish & Build 🔄 (In Progress)
- [x] Single-file publish configuration
- [ ] AOT compilation if feasible
- [x] App manifest for UAC elevation
- [ ] Build scripts
- [x] Documentation (CONTEXT.md)
- [x] Auto-fetch prices on plan build
- [x] Craft vs Buy analysis with HQ/NQ pricing
- [x] Market shopping plans with world recommendations
- [x] Cross-DC travel optimization
- [x] Artisan craft list export support

---

## Code Quality Rules (Inherited from Python Edition)

### Syntax Validation
- **Always validate before building** - Use `dotnet build` to catch compilation errors
- **Check first lines** - Ensure using directives, namespace declarations are correct

### Naming Consistency
- Match the source system's terminology exactly
- If Python says "Live Mode", C# says "Live Mode" - not "Realtime Mode", not "Packet Mode"

### Error Prevention
- **64-bit compatibility** - Target x64 architecture (matches deucalion.dll)
- **Null safety** - Use nullable reference types (`<Nullable>enable</Nullable>`)
- **Async/await** - Use proper async patterns for API calls

### File Operations
- Prefer filesystem MCP tools over shell commands

### Thorough Analysis Requirement (CRITICAL)

**Before submitting ANY code change, agents MUST perform intense and thorough analysis of all possible failure cases.**

This project involves:
- Multiple external APIs with inconsistent schemas (int/string polymorphism in Garland Tools)
- WPF UI threading and data binding edge cases
- File I/O with concurrent access potential
- Complex JSON deserialization with null/missing fields

**Required Analysis Checklist:**
1. **Null/Reference Safety** - Can any property or parameter be null? Add null checks or null-conditional operators (`?.`, `??`)
2. **Type Safety** - Handle polymorphic JSON fields (object type with int/string/null possibilities)
3. **Exception Handling** - Wrap file I/O, network calls, and parsing in try-catch with graceful degradation
4. **Overflow/Bounds** - Check numeric conversions, collection indexing, and string length limits
5. **Concurrency** - Thread-safe access to shared resources (use locks, ConcurrentDictionary, etc.)
6. **UI Thread Safety** - Dispatch UI updates to the main thread using `Dispatcher.Invoke()`
7. **Resource Disposal** - Ensure `IDisposable` objects are properly disposed (using statements)
8. **Edge Cases in Loops** - Empty collections, single items, very large collections

**Example - Bad (causes crashes):**
```csharp
public int Id => int.Parse(_idElement.GetString()!);  // Crashes on null or non-string
```

**Example - Good (defensive):**
```csharp
public int Id => _idElement.ValueKind switch
{
    JsonValueKind.Number => _idElement.TryGetInt32(out var i) ? i : 0,
    JsonValueKind.String => int.TryParse(_idElement.GetString(), out var s) ? s : 0,
    _ => 0
};
```

**Consequence of Non-Compliance:** Debug cycles waste user time. Prefer burning tokens on analysis over iterative debugging.

---

## API Endpoints

```csharp
// Garland Tools
const string GarlandSearch = "https://www.garlandtools.org/api/search.php?text={0}&lang=en";
const string GarlandItem = "https://www.garlandtools.org/db/doc/item/en/3/{0}.json";

// Universalis
const string UniversalisApi = "https://universalis.app/api/v2/{0}/{1}";
const string UniversalisWorlds = "https://universalis.app/api/v2/worlds";
const string UniversalisDataCenters = "https://universalis.app/api/v2/data-centers";
const string UniversalisMarketUrl = "https://universalis.app/market/{0}";

// XIVAPI (for icons)
const string XIVApiIcon = "https://xivapi.com/i/{0:D6}";

// Teamcraft
static readonly string TeamcraftInventory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "ffxiv-teamcraft",
    "inventory.json"
);
```

---

## Dependencies

```xml
<!-- NuGet packages -->
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.x" />
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.x" />
<PackageReference Include="System.Net.Http.Json" Version="8.0.x" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.x" />
<PackageReference Include="WPF-UI" Version="3.0.x" />
```

---

## Key Technical Challenges

### 1. DLL Injection in C#
- Python uses `ctypes` and `pywin32` for Windows API
- C# needs P/Invoke declarations for:
  - `OpenProcess`, `VirtualAllocEx`, `WriteProcessMemory`
  - `CreateRemoteThread`, `WaitForSingleObject`
  - `CreateToolhelp32Snapshot`, `Module32First/Next`

### 2. Named Pipes
- Python uses `win32pipe` and `win32file`
- C# has `System.IO.Pipes.NamedPipeClientStream` (built-in)

### 3. GUI Threading
- Python uses `threading` + `after()` for UI updates
- C# WPF uses `Dispatcher.Invoke()` or `IProgress<T>`

### 4. Admin Privilege Check
- Python: `ctypes.windll.shell32.IsUserAnAdmin()`
- C#: `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`

---

## Build Configuration

### Single-File Publish (PowerShell)
```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
```

### Output
- **Target:** `FFXIV_Craft_Architect.exe`
- **Size:** ~30-50 MB (single-file, self-contained)
- **Requires:** .NET 8 runtime (if not self-contained)

---

## Recent Changes (2026-02-01)

### Features Added
- **Artisan craft list export** - Export plans to Artisan (Dalamud plugin) JSON format
  - Converts Item IDs to Recipe IDs via Garland Tools API
  - Calculates proper quantities based on recipe yield
  - Copies JSON to clipboard for Artisan's "Import List From Clipboard"

### Previous Changes (2026-01-31)
- **Auto-fetch prices** on plan build with `planning.auto_fetch_on_build` setting
- **Cross-DC search** with retry logic and sequential fetching (fixes 504 timeouts)
- **Craft vs Buy analysis** with HQ/NQ pricing and quality warnings
- **Market shopping plans** with world recommendations and DC average comparison
- **Settings persistence** to `settings.json`

### UI Improvements
- TabView layout for Recipe Plan / Market Logistics
- Dark theme styling throughout
- Tooltips on recommendation mode dropdown
- Fixed white background in right panel

### Architecture Decisions
- **Recommendation Modes:** `MinimizeTotalCost` and `MaximizeValue` exposed in UI; `BestUnitPrice` hidden but preserved
- **Market Data:** Sequential fetching with 100ms delay between DCs to avoid rate limits
- **Error Handling:** Per-DC error handling allows partial results when individual DCs fail

---

## Next Steps

1. Options menu for toggling advanced settings
2. Build scripts for distribution
3. Documentation for end users
4. Performance optimization for large plans
5. Artisan import support (reverse direction)

---

*Last updated: 2026-01-31*  
*See CONTEXT.md for detailed session notes*
