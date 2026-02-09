# Task 3B.1: Command Conversion Implementation Plan

## Executive Summary

Convert ~44 event handlers (~400 lines) in MainWindow.xaml.cs to ICommands in MainViewModel, achieving true MVVM architecture where XAML bindings drive UI behavior.

---

## 1. IRelayCommand Infrastructure

### File: `src/FFXIVCraftArchitect/Infrastructure/RelayCommand.cs`

```csharp
using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure;

/// <summary>
/// Extension of ICommand that allows manual triggering of CanExecuteChanged.
/// </summary>
public interface IRelayCommand : ICommand
{
    /// <summary>
    /// Raises the CanExecuteChanged event.
    /// </summary>
    void RaiseCanExecuteChanged();
}

/// <summary>
/// Synchronous relay command with CanExecute support.
/// </summary>
public class RelayCommand : IRelayCommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Parameterized synchronous relay command.
/// </summary>
public class RelayCommand<T> : IRelayCommand
{
    private readonly Action<T> _execute;
    private readonly Predicate<T>? _canExecute;

    public RelayCommand(Action<T> execute, Predicate<T>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T)parameter!) ?? true;
    public void Execute(object? parameter) => _execute((T)parameter!);
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Asynchronous relay command with execution tracking and CanExecute support.
/// Prevents reentrancy during async execution.
/// </summary>
public class AsyncRelayCommand : IRelayCommand, INotifyPropertyChanged
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting != value)
            {
                _isExecuting = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExecuting)));
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        if (IsExecuting) return;

        IsExecuting = true;
        try
        {
            await _execute();
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Parameterized asynchronous relay command.
/// </summary>
public class AsyncRelayCommand<T> : IRelayCommand, INotifyPropertyChanged
{
    private readonly Func<T, Task> _execute;
    private readonly Predicate<T>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T, Task> execute, Predicate<T>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting != value)
            {
                _isExecuting = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExecuting)));
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke((T)parameter!) ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync((T)parameter!);
    }

    public async Task ExecuteAsync(T parameter)
    {
        if (IsExecuting) return;

        IsExecuting = true;
        try
        {
            await _execute(parameter);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
```

---

## 2. Complete Handler Inventory

Based on analysis of `MainWindow.xaml.cs`, here are all handlers organized by tier:

### TIER 1 - Core Plan Operations (8 handlers)
| Handler | Async Pair | Location | Converts To |
|---------|------------|----------|-------------|
| `OnNewPlan` | No | Menu + Code | `NewPlanCommand` (RelayCommand) |
| `OnSavePlan` | `OnSavePlanAsync` | Menu + Buttons | `SavePlanCommand` (AsyncRelayCommand) |
| `OnViewPlans` | `OnViewPlansAsync` | Menu | `ViewPlansCommand` (AsyncRelayCommand) |
| `OnRenamePlan` | `OnRenamePlanAsync` | Menu | `RenamePlanCommand` (AsyncRelayCommand) |
| `OnBuildProjectPlan` | `OnBuildProjectPlanAsync` | Button | `BuildPlanCommand` (AsyncRelayCommand) |
| `OnBrowsePlan` | No | Button | `BrowsePlanCommand` (RelayCommand) |
| `OnAddToProject` | No | Button | `AddToProjectCommand` (RelayCommand) |
| `OnRemoveProjectItem` | No | List Button | `RemoveProjectItemCommand` (RelayCommand<int>) |

### TIER 2 - Import/Export (8 handlers)
| Handler | Async Pair | Location | Converts To |
|---------|------------|----------|-------------|
| `OnImportTeamcraft` | No | Menu | `ImportTeamcraftCommand` (RelayCommand) |
| `OnImportArtisan` | `OnImportArtisanAsync` | Menu | `ImportArtisanCommand` (AsyncRelayCommand) |
| `OnExportTeamcraft` | `OnExportTeamcraftAsync` | Menu | `ExportTeamcraftCommand` (AsyncRelayCommand) |
| `OnExportArtisan` | `OnExportArtisanAsync` | Menu | `ExportArtisanCommand` (AsyncRelayCommand) |
| `OnExportPlainText` | `OnExportPlainTextAsync` | Menu + Button | `ExportPlainTextCommand` (AsyncRelayCommand) |
| `OnExportCsv` | `OnExportCsvAsync` | Menu | `ExportCsvCommand` (AsyncRelayCommand) |

### TIER 3 - Market Operations (6 handlers)
| Handler | Async Pair | Location | Converts To |
|---------|------------|----------|-------------|
| `OnFetchPrices` | `OnFetchPricesAsync` | Menu + Buttons | `FetchPricesCommand` (AsyncRelayCommand) |
| `OnRebuildFromCache` | `OnRebuildFromCacheAsync` | Button | `RebuildFromCacheCommand` (AsyncRelayCommand) |
| `OnRefreshMarketData` | No (calls async) | Menu | `RefreshMarketDataCommand` (AsyncRelayCommand) |
| `OnViewMarketStatus` | No | Menu | `ViewMarketStatusCommand` (RelayCommand) |
| `OnExpandAll` | No | Button | `ExpandAllCommand` (RelayCommand) |
| `OnCollapseAll` | No | Button | `CollapseAllCommand` (RelayCommand) |

### TIER 4 - Navigation/UI (12 handlers)
| Handler | Converts To | Notes |
|---------|-------------|-------|
| `OnRecipePlannerTabClick` | **STAYS IN VIEW** | View-only: tab switching |
| `OnMarketAnalysisTabClick` | **STAYS IN VIEW** | View-only: tab switching |
| `OnProcurementPlannerTabClick` | **STAYS IN VIEW** | View-only: tab switching |
| `OnViewLogs` | `ViewLogsCommand` (RelayCommand) | |
| `OnRestartApp` | `RestartAppCommand` (AsyncRelayCommand) | |
| `OnOptions` | `OptionsCommand` (RelayCommand) | |
| `OnDebugOptions` | **REMOVE** | Redundant, calls OnOptions |
| `OnViewBlacklistedWorlds` | `ViewBlacklistedWorldsCommand` (AsyncRelayCommand) | |
| `OnManageItemsClick` | `ManageItemsCommand` (RelayCommand) | |
| `OnSearchItem` | `SearchCommand` (AsyncRelayCommand) | |
| `OnItemSearchKeyDown` | **STAYS IN VIEW** | View-only: key handling |
| `OnItemSelected` | **STAYS IN VIEW** | View-only: selection change |

### TIER 5 - Watch List (4 handlers)
| Handler | Converts To |
|---------|-------------|
| `OnAddToWatchList` | `AddToWatchListCommand` (RelayCommand) |
| `OnRemoveWatchItem` | `RemoveWatchItemCommand` (RelayCommand<int>) |
| `OnClearWatchList` | `ClearWatchListCommand` (RelayCommand) |
| `OnRefreshWatchList` | `RefreshWatchListCommand` (AsyncRelayCommand) |

### TIER 6 - Item Management (3 handlers)
| Handler | Converts To | Notes |
|---------|-------------|-------|
| `OnQuantityGotFocus` | **STAYS IN VIEW** | View behavior |
| `OnQuantityPreviewTextInput` | **STAYS IN VIEW** | View validation |
| `OnQuantityChanged` | `UpdateQuantityCommand` (RelayCommand<(int itemId, int quantity)>) | |

---

## 3. MainViewModel Command Properties

### File: `src/FFXIVCraftArchitect/ViewModels/MainViewModel.cs`

```csharp
public partial class MainViewModel : ViewModelBase
{
    // === TIER 1: Core Plan Operations ===
    public IRelayCommand NewPlanCommand { get; }
    public IRelayCommand SavePlanCommand { get; }
    public IRelayCommand ViewPlansCommand { get; }
    public IRelayCommand RenamePlanCommand { get; }
    public IRelayCommand BuildPlanCommand { get; }
    public IRelayCommand BrowsePlanCommand { get; }
    public IRelayCommand AddToProjectCommand { get; }
    public IRelayCommand RemoveProjectItemCommand { get; } // Parameterized

    // === TIER 2: Import/Export ===
    public IRelayCommand ImportTeamcraftCommand { get; }
    public IRelayCommand ImportArtisanCommand { get; }
    public IRelayCommand ExportTeamcraftCommand { get; }
    public IRelayCommand ExportArtisanCommand { get; }
    public IRelayCommand ExportPlainTextCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }

    // === TIER 3: Market Operations ===
    public IRelayCommand FetchPricesCommand { get; }
    public IRelayCommand RebuildFromCacheCommand { get; }
    public IRelayCommand RefreshMarketDataCommand { get; }
    public IRelayCommand ViewMarketStatusCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }

    // === TIER 4: Navigation/UI ===
    public IRelayCommand ViewLogsCommand { get; }
    public IRelayCommand RestartAppCommand { get; }
    public IRelayCommand OptionsCommand { get; }
    public IRelayCommand ViewBlacklistedWorldsCommand { get; }
    public IRelayCommand ManageItemsCommand { get; }
    public IRelayCommand SearchCommand { get; }

    // === TIER 5: Watch List ===
    public IRelayCommand AddToWatchListCommand { get; }
    public IRelayCommand RemoveWatchItemCommand { get; } // Parameterized
    public IRelayCommand ClearWatchListCommand { get; }
    public IRelayCommand RefreshWatchListCommand { get; }
    
    // === Status Message Binding ===
    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
}
```

---

## 4. XAML Binding Changes

### Example: Menu Items

**BEFORE:**
```xml
<MenuItem Header="File">
    <MenuItem Header="New Plan" Click="OnNewPlan"/>
    <MenuItem Header="Save Plan..." Click="OnSavePlan"/>
    <MenuItem Header="Load Plans..." Click="OnViewPlans"/>
</MenuItem>
```

**AFTER:**
```xml
<MenuItem Header="File">
    <MenuItem Header="New Plan" Command="{Binding NewPlanCommand}"/>
    <MenuItem Header="Save Plan..." Command="{Binding SavePlanCommand}"/>
    <MenuItem Header="Load Plans..." Command="{Binding ViewPlansCommand}"/>
</MenuItem>
```

### Example: Buttons with CanExecute

**BEFORE:**
```xml
<ui:Button x:Name="BuildPlanButton"
           Content="Build Project Plan"
           IsEnabled="False"
           Click="OnBuildProjectPlan"/>
```

**AFTER:**
```xml
<ui:Button Content="Build Project Plan"
           Command="{Binding BuildPlanCommand}"/>
```

### Example: Parameterized Commands

**BEFORE:**
```xml
<Button Content="×"
        Click="OnRemoveProjectItem"
        Tag="{Binding Id}"/>
```

**AFTER:**
```xml
<Button Content="×"
        Command="{Binding DataContext.RemoveProjectItemCommand, 
                         RelativeSource={RelativeSource AncestorType=ListBox}}"
        CommandParameter="{Binding Id}"/>
```

### Example: Status Label Binding

**BEFORE:**
```xml
<TextBlock x:Name="StatusLabel" Text="Ready"/>
```

**AFTER:**
```xml
<TextBlock Text="{Binding StatusMessage}"/>
```

---

## 5. Window Reference Strategy

### Problem
Several commands need `Window` as owner for dialogs:
- `SavePlanAsync` needs `ownerWindow` for dialog positioning
- `ViewPlansAsync` needs `ownerWindow` for dialog positioning
- `ViewLogs`, `Options`, `ViewMarketStatus` create child windows

### Solution: IWindowService Interface

```csharp
// File: src/FFXIVCraftArchitect/Services/Interfaces/IWindowService.cs
public interface IWindowService
{
    /// <summary>
    /// Gets the current main window.
    /// </summary>
    Window? GetMainWindow();
    
    /// <summary>
    /// Shows a window as a dialog with the main window as owner.
    /// </summary>
    bool? ShowDialog(Window window);
    
    /// <summary>
    /// Shows a window non-modally with the main window as owner.
    /// </summary>
    void Show(Window window);
    
    /// <summary>
    /// Creates and shows the log viewer window.
    /// </summary>
    void ShowLogViewer();
    
    /// <summary>
    /// Creates and shows the options dialog.
    /// </summary>
    bool? ShowOptions();
    
    /// <summary>
    /// Creates and shows the market status window.
    /// </summary>
    void ShowMarketStatus(IEnumerable<MarketStatusItem> items);
}
```

### Implementation

```csharp
// File: src/FFXIVCraftArchitect/Services/WindowService.cs
public class WindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private Window? _mainWindow;

    public WindowService(IServiceProvider services)
    {
        _services = services;
    }

    public void SetMainWindow(Window window) => _mainWindow = window;
    public Window? GetMainWindow() => _mainWindow ?? Application.Current.MainWindow;

    public bool? ShowDialog(Window window)
    {
        window.Owner = GetMainWindow();
        return window.ShowDialog();
    }

    public void Show(Window window)
    {
        window.Owner = GetMainWindow();
        window.Show();
    }

    public void ShowLogViewer()
    {
        var window = new LogViewerWindow { Owner = GetMainWindow() };
        window.Show();
    }

    public bool? ShowOptions()
    {
        var window = _services.GetRequiredService<OptionsWindow>();
        window.Owner = GetMainWindow();
        return window.ShowDialog();
    }
    
    // ... other methods
}
```

### Registration

```csharp
// In App.xaml.cs or service registration
services.AddSingleton<IWindowService>(sp => 
{
    var service = new WindowService(sp);
    // Set main window when available
    return service;
});
```

---

## 6. Status Message Binding Approach

### Current Pattern
```csharp
// MainWindow.xaml.cs
StatusLabel.Text = "Some message";
```

### New Pattern
```csharp
// MainViewModel.cs
StatusMessage = "Some message";
```

### XAML Binding
```xml
<TextBlock Text="{Binding StatusMessage}" 
           Foreground="{StaticResource GoldAccentBrush}"/>
```

### Migration Strategy
1. Add `StatusMessage` property to MainViewModel
2. Replace all `StatusLabel.Text =` in handler logic with `StatusMessage =`
3. Bind StatusLabel.Text to StatusMessage in XAML
4. Remove direct StatusLabel references from MainWindow.xaml.cs

---

## 7. Execution Order

### Phase 1: Infrastructure (Files: 1)
1. Create `Infrastructure/RelayCommand.cs` with all command types
2. Create `Services/Interfaces/IWindowService.cs`
3. Create `Services/WindowService.cs`
4. Register IWindowService in DI container

### Phase 2: MainViewModel Expansion (Files: 1)
1. Add StatusMessage property to MainViewModel
2. Add ICommand properties for Tier 1 (Core Plan)
3. Add ICommand properties for Tier 2 (Import/Export)
4. Add ICommand properties for Tier 3 (Market Operations)
5. Add ICommand properties for Tier 4 (Navigation/UI)
6. Add ICommand properties for Tier 5 (Watch List)
7. Initialize all commands in constructor

### Phase 3: XAML Updates (Files: 1)
1. Update Menu Items (File, Import, Export, Tools menus)
2. Update Buttons in Left Panel
3. Update Buttons in Center Panel
4. Update ListBox ItemTemplate buttons
5. Bind StatusLabel to StatusMessage

### Phase 4: MainWindow Cleanup (Files: 1)
1. Remove Tier 1 handler methods
2. Remove Tier 2 handler methods
3. Remove Tier 3 handler methods
4. Remove Tier 4 handler methods (keeping view-only ones)
5. Remove Tier 5 handler methods
6. Remove StatusLabel direct references (replace with binding)

### Phase 5: Testing
1. Verify all menu items work
2. Verify all buttons work
3. Verify CanExecute disables buttons appropriately
4. Verify async commands prevent double-click
5. Verify status messages display correctly

---

## 8. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Async reentrancy | High | AsyncRelayCommand.IsExecuting blocks double-clicks |
| Dialog owner null | Medium | IWindowService always provides owner |
| CanExecute not updating | Medium | Use CommandManager.InvalidateRequerySuggested() |
| Parameter type mismatch | Low | Strong typing in RelayCommand<T> |
| Memory leaks | Low | ViewModelBase.Dispose pattern for cleanup |

---

## 9. File Change Summary

| File | Lines Added | Lines Removed | Net Change |
|------|-------------|---------------|------------|
| `Infrastructure/RelayCommand.cs` | +150 | 0 | +150 |
| `Services/Interfaces/IWindowService.cs` | +20 | 0 | +20 |
| `Services/WindowService.cs` | +80 | 0 | +80 |
| `ViewModels/MainViewModel.cs` | +300 | -10 | +290 |
| `MainWindow.xaml` | +50 | -50 | 0 |
| `MainWindow.xaml.cs` | +20 | -400 | -380 |
| **Total** | **+620** | **-460** | **+160** |

---

## 10. Gold Standard Verification Checklist

After implementation, MainWindow.xaml.cs should:
- [ ] Have NO Click handlers for menu items
- [ ] Have NO Click handlers for buttons (except view-only behaviors)
- [ ] Have NO direct StatusLabel.Text assignments
- [ ] Still have tab switching handlers (view-only)
- [ ] Still have key down handlers (view-only)
- [ ] Still have selection changed handlers (view-only)

MainWindow.xaml should:
- [ ] Use Command="{Binding XxxCommand}" for all action buttons
- [ ] Bind StatusLabel.Text to ViewModel.StatusMessage
- [ ] Have no x:Name attributes for buttons that were only used for enabling/disabling

MainViewModel should:
- [ ] Have 30+ ICommand properties
- [ ] Have StatusMessage property
- [ ] Initialize all commands in constructor
- [ ] Use IWindowService for window operations
- [ ] Use coordinators for business logic
