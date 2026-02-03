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
│       ├── App.razor                    # Root component with routing
│       ├── Program.cs                   # DI registration, app bootstrap
│       ├── Theme.cs                     # Centralized MudBlazor theme
│       ├── _Imports.razor               # Global using statements
│       │
│       ├── Pages/                       # Top-level page components
│       │   ├── Index.razor              # Market Logistics (shopping lists)
│       │   ├── Planner.razor            # Recipe Planner (crafting trees)
│       │   └── About.razor              # About page (MudBlazor cards)
│       │
│       ├── Shared/                      # Reusable UI components
│       │   ├── MainLayout.razor         # App shell (menus, nav, theme)
│       │   ├── RecipeNodeView.razor     # Recursive recipe tree node
│       │   └── CraftAnalysisPanel.razor # Craft vs Buy analysis display
│       │
│       ├── Dialogs/                     # Modal dialogs
│       │   ├── SavePlanDialog.razor
│       │   ├── LoadPlanDialog.razor
│       │   ├── ImportDialog.razor
│       │   ├── ExportDialog.razor
│       │   ├── LogsDialog.razor
│       │   └── OptionsDialog.razor
│       │
│       ├── Services/                    # Web-only services (browser APIs)
│       │   ├── AppState.cs              # Central state management
│       │   └── IndexedDbService.cs      # Browser storage wrapper
│       │
│       └── wwwroot/                     # Static web assets
│           ├── index.html               # Entry point (base href critical!)
│           ├── indexedDB.js             # JS interop for IndexedDB
│           ├── mudblazor.css            # MudBlazor theme styles
│           └── css/                     # Custom CSS overrides
│
├── .github/workflows/
│   └── deploy-web.yml                   # Auto-deploy web app to GitHub Pages
├── .kimi/skills/mudblazor-ffxiv/        # Custom Kimi skill
│   └── SKILL.md                         # MudBlazor patterns for this project
├── .builderrules                        # Kimi code generation rules
├── .editorconfig                        # Code style configuration
├── settings.json                        # User settings
├── publish.bat                          # Desktop build script
├── AGENTS.md                            # This file
└── CONTEXT.md                           # Session context / changelog
```

---

## Web App Structure Reference

### Quick Navigation Guide

**To find this UI element...** | **Look in this file...**
---|---
Menu bar, tabs, app title | `Shared/MainLayout.razor`
Market search, shopping list, analyze button | `Pages/Index.razor`
Recipe tree, craft vs buy analysis, material list | `Pages/Planner.razor`
Recursive recipe node (expandable tree) | `Shared/RecipeNodeView.razor`
Craft vs Buy savings panel | `Shared/CraftAnalysisPanel.razor`
Global state (shopping items, current plan) | `Services/AppState.cs`
Browser save/load plans | `Services/IndexedDbService.cs`

### Component Hierarchy

```
App.razor (Router)
└── MainLayout.razor (App shell)
    ├── MudThemeProvider (dark theme config)
    ├── MudSnackbarProvider (toast notifications)
    └── @Body (page content)
        │
        ├── Index.razor ("/")
        │   ├── Data Center selector
        │   ├── Item search with results dropdown
        │   ├── Shopping list table
        │   ├── Import/Export buttons (file I/O)
        │   └── Market analysis results cards
        │
        ├── Planner.razor ("/planner")
        │   ├── Project items list (target crafts)
        │   ├── Build Plan button
        │   ├── RecipeNodeView (recursive tree)
        │   ├── CraftAnalysisPanel
        │   └── Shopping list summary
        │
        └── About.razor ("/about")
            └── MudBlazor cards with info
```

### State Management

**AppState.cs** - Singleton service that persists across navigation:
```csharp
// Shopping/Market state
List<MarketShoppingItem> ShoppingItems
List<ShoppingPlan> ShoppingPlans
string SelectedDataCenter
RecommendationMode RecommendationMode

// Planner state  
List<PlannerProjectItem> ProjectItems
CraftingPlan CurrentPlan
List<CraftVsBuyAnalysis> CraftAnalyses

// Persistence
List<StoredPlanSummary> SavedPlans
DateTime? LastAutoSave
bool IsAutoSaveEnabled

// Events
OnShoppingListChanged
OnPlanChanged
OnSavedPlansChanged
```

### Key Razor Syntax Patterns Used

**Event binding with parameters:**
```razor
<button @onclick="() => RemoveItem(item)">Remove</button>
```

**Two-way binding with custom event timing:**
```razor
<input @bind="_searchQuery" @bind:event="oninput" @onkeyup="OnSearchKeyUp" />
```

**Conditional rendering:**
```razor
@if (_isLoading) { <span>Loading...</span> }
else { <span>Ready</span> }
```

**Loop rendering:**
```razor
@foreach (var item in AppState.ShoppingItems)
{
    <div @key="item.Id">@item.Name</div>
}
```

**Component parameters:**
```razor
<RecipeNodeView Node="child" 
                Depth="Depth + 1"
                OnSourceChanged="OnNodeSourceChanged" />
```

```csharp
@code {
    [Parameter] public PlanNode? Node { get; set; }
    [Parameter] public int Depth { get; set; }
    [Parameter] public EventCallback<PlanNode> OnSourceChanged { get; set; }
}
```

**RenderFragment for dynamic content:**
```csharp
private RenderFragment RenderNodeContent() => __builder =>
{
    __builder.OpenElement(0, "div");
    __builder.AddContent(1, "Content");
    __builder.CloseElement();
};
```

### Common Fixes

**Input binding conflicts:**
- ❌ `@bind` + `@onkeydown` - race condition
- ✅ `@bind:event="oninput"` + `@onkeyup` - proper sequence

**Menu outside-click:**
```javascript
// In MainLayout.razor - JS interop for document click
document.addEventListener('click', function(e) {
    // Close menus when clicking outside
});
```

**Disposal pattern:**
```csharp
@implements IDisposable

public void Dispose()
{
    AppState.OnPlanChanged -= OnPlanChanged;
    _timer?.Dispose();
}
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

## Development Conventions

### Code Style Configuration
- **`.editorconfig`** - Code formatting rules (indentation, spacing, braces)
- **`.builderrules`** - Kimi code generation rules for MudBlazor patterns
- **`.kimi/skills/mudblazor-ffxiv/SKILL.md`** - Custom patterns for this project

### MudBlazor Web App Patterns

#### Component Structure
```csharp
// Services injected at top
@inject ISnackbar Snackbar
@inject IDialogService DialogService
@inject AppState AppState

// Event handlers use "On" or "Handle" prefix
private async Task OnSavePlan() { }
private void HandleItemSelected(Item item) { }

// Private fields use underscore prefix
private bool _isLoading;
private string _searchQuery = string.Empty;
```

#### Dialog Pattern
```razor
@* Dialogs/MyDialog.razor *@
<MudDialog>
    <DialogContent>
        <!-- Content -->
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" OnClick="Submit">Save</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    private void Cancel() => MudDialog.Cancel();
    private void Submit() => MudDialog.Close(DialogResult.Ok(true));
}
```

#### State Management Pattern
```csharp
// Always call StateHasChanged after async updates
private async Task LoadData()
{
    _isLoading = true;
    _items = await Service.GetItemsAsync();
    _isLoading = false;
    StateHasChanged();
}
```

#### Snackbar Notifications
```csharp
Snackbar.Add("Success message", Severity.Success);
Snackbar.Add($"Error: {ex.Message}", Severity.Error);
Snackbar.Add("Info message", Severity.Info);
```

### Styling Standards
- Use MudBlazor utility classes: `pa-4`, `mb-2`, `gap-4`
- Theme colors: `Color.Primary` (gold), `Color.Error` (red)
- No inline styles - use component parameters or CSS isolation
- Elevation: 1 for cards, 2 for dialogs, 4 for app bar

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

## Build Configuration

### Code Analysis (Enabled)
```xml
<!-- In .csproj files -->
<PropertyGroup>
  <Nullable>enable</Nullable>
  <TreatWarningsAsErrors Condition="'$(Configuration)' == 'Release'">true</TreatWarningsAsErrors>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="8.*" />
  <PackageReference Include="Nullable" Version="1.3.1" />
</ItemGroup>
```

### Build Commands
```bash
# Build entire solution
dotnet build

# Build specific project
dotnet build src/FFXIVCraftArchitect.Web/

# Watch for changes (Web development)
dotnet watch --project src/FFXIVCraftArchitect.Web

# Clean and rebuild
dotnet clean src/FFXIVCraftArchitect.Web/
dotnet build src/FFXIVCraftArchitect.Web/

# Release build (Web)
dotnet publish src/FFXIVCraftArchitect.Web/ -c Release -o dist/web

# Release build (Desktop)
dotnet publish src/FFXIVCraftArchitect/ -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
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

## Development Workflow

### Branch Setup (One-time)

You need two branches:
- **`main`**: For GitHub Pages deployment (base href = `/XIV-Craft-Architect/`)
- **`local-dev`**: For local testing (base href = `/`)

**In GitHub Desktop:**
1. Create branch: Branch → New branch → Name: `local-dev` → Create branch
2. Switch to `local-dev` using the branch dropdown at top
3. Change `index.html` base href to `/` for local testing
4. The `index.html` file is now "skipped" - Git will ignore changes to it on this branch

### Daily Workflow with GitHub Desktop

**1. Develop Locally**
- Make sure you're on `local-dev` branch (check dropdown at top)
- Run: `dotnet watch --project src/FFXIVCraftArchitect.Web`
- Make your code changes
- Files appear in GitHub Desktop automatically
- Commit to `local-dev` with descriptive message
- Repeat until feature is complete

**2. Deploy to GitHub Pages**
```
Branch dropdown → main
↓
Branch → Merge into current branch → local-dev
↓
If merge conflict on index.html:
   Right-click index.html → "Resolve using 'main'" (keeps GitHub Pages base href)
   → Commit merge
↓
Push origin
↓
Wait 2-3 minutes for GitHub Actions to deploy
↓
Test at https://yourusername.github.io/XIV-Craft-Architect/
```

**3. Continue Developing**
```
Branch dropdown → local-dev
↓
Keep working, commits to local-dev don't affect deployed site
```

### Why This Works

The `index.html` file has different base href on each branch:
- `main`: `/XIV-Craft-Architect/` (GitHub Pages needs this)
- `local-dev`: `/` (local development needs this)

Git is configured to ignore `index.html` changes on `local-dev`, so:
- You can freely change code on `local-dev`
- When merging to `main`, the GitHub Pages base href is preserved
- No manual file editing needed during deployment

### Initial GitHub Setup (One-time)

**Publish to GitHub:**
1. GitHub Desktop: Click "Publish repository" button
2. Name: `XIV-Craft-Architect`, choose Public/Private
3. Click Publish Repository

**Enable GitHub Pages:**
1. Go to `github.com/YOUR_USERNAME/XIV-Craft-Architect` in browser
2. Settings → Pages (left sidebar)
3. Build and deployment → Source: GitHub Actions
4. Done! Workflow deploys automatically on each push to main

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

## Documentation Quick Reference

| File | Purpose |
|------|---------|
| `.builderrules` | Kimi code generation rules for this project |
| `.editorconfig` | Code formatting rules (C#, Razor, JSON) |
| `.kimi/skills/mudblazor-ffxiv/SKILL.md` | Custom MudBlazor patterns and commands |
| `AGENTS.md` | This file - project overview and conventions |
| `CONTEXT.md` | Session-specific notes and recent changes |

### Using the Custom Skill
Reference the skill when asking Kimi to create components:
```
Create a dialog for importing Teamcraft lists using the dialog-menu-item pattern
```

Or reference specific patterns:
```
Add a new state property for tracking favorite items using the state-service pattern
```

---

*Last updated: 2026-02-03*  
*See CONTEXT.md for detailed session notes*

---

**Recent Documentation Updates:**
- Added `.builderrules` for code generation guidance
- Added `.editorconfig` for consistent code formatting  
- Created custom `.kimi/skills/mudblazor-ffxiv/SKILL.md` for project-specific patterns
- Added `Theme.cs` for centralized theme configuration
- Expanded MudBlazor conventions section
