# FFXIV Craft Architect - C# Edition

**Project:** C# fork of the Python-based FFXIV Craft Architect  
**Goal:** Replicate functionality with modern C#/.NET while maintaining code quality standards  
**Status:** Active Development - Core Features Complete, Web Companion Added

---

## Project Overview

This is a C# reimplementation of the FFXIV Craft Architect application, which calculates crafting costs in Final Fantasy XIV with live inventory tracking via DLL injection.

### Multi-Platform Architecture

The project now consists of **three components**:

| Component | Platform | Purpose |
|-----------|----------|---------|
| **Core Library** | .NET 8 Class Library | Shared models and services (APIs, calculations) |
| **WPF Desktop** | Windows Desktop | Full-featured app with live inventory injection |
| **Web Companion** | Blazor WASM (Browser) | Lightweight market logistics without installation |

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

## Architecture

### The Golden Rule: Core First

```
┌─────────────────────────────────────────────────────────┐
│           FFXIVCraftArchitect.Core (Shared)             │
│  • Models (CraftingPlan, Recipe, Market data, etc.)     │
│  • Services (GarlandService, UniversalisService, etc.)  │
│  • Business logic (market calculations, recipe trees)   │
└─────────────────────────────────────────────────────────┘
                    ↗                    ↖
                   /                        \
    ┌──────────────┐                        ┌──────────────┐
    │     WPF      │                        │  Blazor WASM │
    │   (Desktop)  │                        │    (Web)     │
    │              │                        │              │
    │ • WPF UI     │                        │ • MudBlazor  │
    │ • Live Mode  │                        │   components │
    │ • File I/O   │                        │ • Browser    │
    │   (deucalion)│                        │   APIs only  │
    └──────────────┘                        └──────────────┘
```

### Edit Once, Benefit Both

**Put shared code in Core:**
- Data models (add a field → both apps get it)
- API service logic (fix a bug → both apps fixed)
- Calculation logic (shopping plans, recipe trees)
- Plan serialization format (JSON import/export)

**Platform-specific code stays separate:**
- UI code (XAML vs Razor components)
- User interactions (WPF drag-drop vs web click handlers)
- Platform features (live injection only works in WPF)

### Maintenance Guide for Agents

| If you need to... | Edit here | Notes |
|-------------------|-----------|-------|
| Add field to item data | `Core/Models/*.cs` | Both apps get it automatically |
| Fix market price calculation | `Core/Services/MarketShoppingService.cs` | Both apps get the fix |
| Change API endpoint URL | `Core/Services/*Service.cs` | One place to update |
| Add UI button in WPF | `WPF/*.xaml` | WPF only |
| Add UI button in Web | `Web/Pages/*.razor` | Web only |
| Add desktop-only feature | `WPF/` | Don't add to Core if web can't use it |
| Add web-only feature | `Web/` | Don't add to Core if desktop doesn't need it |

---

## File Structure

```
FFXIV Craft Architect C# Edition/
├── src/
│   ├── FFXIVCraftArchitect.Core/        # SHARED: Models & Services
│   │   ├── FFXIVCraftArchitect.Core.csproj
│   │   ├── Helpers/
│   │   │   └── JobHelper.cs
│   │   ├── Models/
│   │   │   ├── CraftingPlan.cs          # Plan serialization (web ↔ desktop)
│   │   │   ├── GarlandModels.cs         # Garland Tools API models
│   │   │   ├── MarketListing.cs         # Universalis API models
│   │   │   ├── MarketShoppingModels.cs  # Shopping plan calculations
│   │   │   ├── Recipe.cs                # Recipe tree models
│   │   │   └── WorldData.cs             # Data center/world info
│   │   └── Services/
│   │       ├── GarlandService.cs        # Item search & recipe data
│   │       ├── UniversalisService.cs    # Market board prices
│   │       ├── RecipeCalculationService.cs  # Build recipe trees
│   │       └── MarketShoppingService.cs   # Shopping plan logic
│   │
│   ├── FFXIVCraftArchitect/             # WPF DESKTOP APP
│   │   ├── FFXIVCraftArchitect.csproj
│   │   ├── App.xaml
│   │   ├── MainWindow.xaml              # Main GUI
│   │   ├── Models/                      # Desktop-only models
│   │   ├── Services/                    # Desktop-only services
│   │   │   ├── SettingsService.cs       # settings.json I/O
│   │   │   ├── PlanPersistenceService.cs # Plan file save/load
│   │   │   ├── ArtisanService.cs        # Artisan plugin import/export
│   │   │   └── TeamcraftService.cs      # Teamcraft integration
│   │   ├── Views/                       # Additional windows
│   │   └── Lib/
│   │       └── deucalion.dll            # Injected DLL (live mode)
│   │
│   └── FFXIVCraftArchitect.Web/         # BLAZOR WASM WEB APP
│       ├── FFXIVCraftArchitect.Web.csproj
│       ├── App.razor
│       ├── Program.cs
│       ├── _Imports.razor
│       ├── Pages/
│       │   ├── Index.razor              # Market Logistics UI
│       │   ├── Planner.razor            # Recipe planner placeholder
│       │   └── About.razor              # About page
│       ├── Shared/
│       │   └── MainLayout.razor         # App shell with nav
│       └── wwwroot/
│           └── index.html               # Entry point
│
├── .github/workflows/
│   └── deploy-web.yml                   # Auto-deploy web app to GitHub Pages
├── settings.json                        # User settings
├── publish.bat                          # Desktop build script
├── AGENTS.md                            # This file
└── CONTEXT.md                           # Session context / changelog
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

### Phase 6: Polish & Build ✅ (Complete)
- [x] Single-file publish configuration
- [ ] AOT compilation if feasible
- [x] App manifest for UAC elevation
- [x] Build scripts (`publish.bat`)
- [x] Documentation (CONTEXT.md)
- [x] Auto-fetch prices on plan build
- [x] Craft vs Buy analysis with HQ/NQ pricing
- [x] Market shopping plans with world recommendations
- [x] Cross-DC travel optimization
- [x] Bidirectional Artisan import/export
- [x] Options window with live theming
- [x] Custom styled tab buttons (replaced TabView)
- [x] Expander-based recipe display (replaced TreeView)
- [x] Artisan craft list export support
- [x] Debug log viewer
- [x] Auto-width ComboBox behavior
- [x] HQ toggle in Project Items pane
- [x] Accent color support throughout UI

### Phase 7: Web Companion ✅ (Complete)
- [x] Create FFXIVCraftArchitect.Core shared library
- [x] Move shared models and services to Core
- [x] Create Blazor WASM web project
- [x] Implement Market Logistics UI in web
- [x] Add plan import/export (web ↔ desktop compatibility)
- [x] GitHub Actions deployment to GitHub Pages
- [x] Update solution file
- [x] Document architecture for future agents

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

### Multi-Project Awareness
- **Check which project you're editing** - Core changes affect both apps
- **Keep Core library pure** - No WPF or Blazor dependencies in Core
- **Test both apps after Core changes** - A breaking change in Core breaks both

### Thorough Analysis Requirement (CRITICAL)

**Before submitting ANY code change, agents MUST perform intense and thorough analysis of all possible failure cases.**

This project involves:
- Multiple external APIs with inconsistent schemas (int/string polymorphism in Garland Tools)
- WPF UI threading and data binding edge cases
- File I/O with concurrent access potential
- Complex JSON deserialization with null/missing fields
- **NEW:** Blazor WASM browser limitations (no file system, no P/Invoke)

**Required Analysis Checklist:**
1. **Null/Reference Safety** - Can any property or parameter be null? Add null checks or null-conditional operators (`?.`, `??`)
2. **Type Safety** - Handle polymorphic JSON fields (object type with int/string/null possibilities)
3. **Exception Handling** - Wrap file I/O, network calls, and parsing in try-catch with graceful degradation
4. **Overflow/Bounds** - Check numeric conversions, collection indexing, and string length limits
5. **Concurrency** - Thread-safe access to shared resources (use locks, ConcurrentDictionary, etc.)
6. **UI Thread Safety** - Dispatch UI updates to the main thread using `Dispatcher.Invoke()`
7. **Resource Disposal** - Ensure `IDisposable` objects are properly disposed (using statements)
8. **Edge Cases in Loops** - Empty collections, single items, very large collections
9. **WASM Compatibility** - If adding to Core, ensure it works in browser (no file I/O, no P/Invoke)

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

### Core Library
```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
<PackageReference Include="System.Net.Http.Json" Version="8.0.0" />
```

### WPF Desktop
```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.x" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.x" />
<PackageReference Include="WPF-UI" Version="3.0.x" />
<PackageReference Include="CsvHelper" Version="33.0.x" />
```

### Web (Blazor WASM)
```xml
<PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="8.0.x" />
<PackageReference Include="MudBlazor" Version="6.12.x" />
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
- Blazor uses component state + `StateHasChanged()`

### 4. Admin Privilege Check
- Python: `ctypes.windll.shell32.IsUserAnAdmin()`
- C#: `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`

### 5. WASM Limitations
- No direct file system access (use browser File API)
- No P/Invoke (can't call native Windows APIs)
- CORS restrictions when calling APIs (Garland/Universalis must allow)

---

## Build Configuration

### Desktop App (Single-File Publish)
```powershell
dotnet publish src/FFXIVCraftArchitect/FFXIVCraftArchitect.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
```

### Web App (GitHub Pages)
```powershell
dotnet publish src/FFXIVCraftArchitect.Web/FFXIVCraftArchitect.Web.csproj `
    -c Release -o dist/web
```

### Output
- **Desktop:** `FFXIV_Craft_Architect.exe` (~30-50 MB)
- **Web:** Static files in `dist/web/wwwroot` for GitHub Pages

---

## Recent Changes (2026-02-02) - Milestone v0.2.0

### Major Features Added
- **Bidirectional Artisan support** - Full import and export of crafting lists
  - Export to Artisan JSON (Item IDs → Recipe IDs via Garland Tools)
  - Import from Artisan (uses XIVAPI to resolve Recipe IDs → Item IDs)
- **In-app Options window** with live theming
  - Accent color picker with presets
  - Settings persistence to `settings.json`
  - Silent save (no confirmation dialog on success)
- **Debug Log Viewer** with filtering and search
  - Log level filters (Debug, Info, Warning, Error)
  - Color-coded output
  - Export and clear functionality
- **Market Shopping Plans** with detailed world recommendations
  - Cross-DC travel optimization (NA data centers)
  - Per-world pricing with excess quantity calculation
  - Best listing highlighting

### Web Companion (NEW)
- **Blazor WASM app** deployed to GitHub Pages
- **Market Logistics** - Search items, build shopping lists, analyze market prices
- **Plan Import/Export** - JSON format shared with desktop app
- **No installation required** - Works on mobile and desktop browsers

### UI/UX Improvements
- **HQ Toggle moved to Project Items** - Set HQ requirement before building plan
- **Recipe Plan visual refresh** - Gold star (★) + accent color for HQ items
- **Market Logistics cards** - Accent color support, compact layout
- **Purchase Summary** - Collapsible expander with totals
- **Auto-width ComboBoxes** - Dynamic sizing based on content
- **Build script** (`publish.bat`) for distribution

### Architecture Changes
- **Extracted Core library** - Shared models and services between WPF and Web
- **Refactored services** - Removed WPF dependencies from business logic
- **Plan serialization** - JSON format works in both desktop and web

### Bug Fixes
- Refresh button disabled when loading plans with market data
- Craft cost calculation respecting acquisition source
- Price fetch error handling preserving cached data
- Default recommendation mode saving/loading correctly

---

## Next Steps

1. **GitHub Releases** - Automated builds and version tags
2. **Update Checker** - Version checking against GitHub releases  
3. **Live Mode** - Packet capture for real-time inventory (long term)
4. **Inventory Sync** - Teamcraft inventory synchronization
5. **Performance** - Optimization for large crafting plans
6. **Web Recipe Planner** - Full crafting tree visualization in browser

---

*Last updated: 2026-02-02*  
*See CONTEXT.md for detailed session notes*
