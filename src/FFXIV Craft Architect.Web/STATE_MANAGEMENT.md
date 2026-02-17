# State Management Guidelines

## Core Principle
**State should persist when switching between tabs unless the user explicitly requests a reset.**

## AppState Persistence Rules

### Shopping Items (`AppState.ShoppingItems`)
- **PERSIST**: When switching between Recipe Planner and Procurement Planner
- **PERSIST**: When adding/removing individual items
- **CLEAR ONLY**: When user clicks "Clear All", imports a new plan, or builds a new project plan

### Shopping Plans (`AppState.ShoppingPlans`)
- **PERSIST**: When switching between tabs
- **PERSIST**: When adding/removing individual items (user should explicitly re-run analysis)
- **CLEAR ONLY**: When user explicitly runs new analysis, clears shopping list, or builds new plan

### Procurement Analysis (`AppState.CurrentProcurementAnalysis`)
- **PERSIST**: When switching between tabs
- **CLEAR ONLY**: When user explicitly runs new analysis or builds new plan

### Current Plan (`AppState.CurrentPlan`)
- **PERSIST**: When switching between tabs
- **CLEAR ONLY**: When user starts new plan, clears plan, or loads different plan

## Anti-Patterns to Avoid

### 1. Don't Clear Analysis on Item Changes
```csharp
// BAD - Clears analysis when user adds one item
private void AddItem(item) {
    ShoppingItems.Add(item);
    ShoppingPlans.Clear(); // Don't do this!
}

// GOOD - Preserve analysis
private void AddItem(item) {
    ShoppingItems.Add(item);
    // ShoppingPlans preserved - user explicitly re-runs analysis when ready
}
```

### 2. Don't Reset State in OnInitialized
```csharp
// BAD - Clears state every time tab is visited
protected override void OnInitialized() {
    ShoppingPlans.Clear(); // Don't do this!
}

// GOOD - Check if state exists
protected override void OnInitialized() {
    _hasExistingAnalysis = AppState.ShoppingPlans.Any();
}
```

### 3. Don't Auto-Overwrite Shopping List
```csharp
// BAD - Overwrites shopping list without checking
public void LoadPlan(plan) {
    SyncProjectToShopping(); // Don't do this unconditionally!
}

// GOOD - Preserve existing shopping list
public void LoadPlan(plan) {
    if (!ShoppingItems.Any()) { // Only if empty
        SyncProjectToShopping();
    }
}
```

## Safe State Clearing Locations

### Recipe Planner (Index.razor)
- `BuildPlanAsync()` - Clears previous analysis when building new plan (INTENTIONAL)
- `ClearPlan()` - Clears everything when user requests (INTENTIONAL)
- `LoadSavedPlanAsync()` - Loads plan but preserves shopping list if not empty

### Procurement Planner (Procurement.razor)
- `RunMarketAnalysisAsync()` - Clears previous plans before new analysis (INTENTIONAL)
- `ClearAllItems()` - Clears shopping list and plans when user requests (INTENTIONAL)
- `ImportPlanAsync()` - Clears shopping list before importing (INTENTIONAL - user action)

## Event Subscription Pattern

Always subscribe to AppState events in `OnInitialized` and unsubscribe in `Dispose`:

```csharp
protected override void OnInitialized()
{
    AppState.OnShoppingListChanged += OnShoppingListChanged;
    AppState.OnProcurementAnalysisChanged += OnProcurementAnalysisChanged;
}

public void Dispose()
{
    AppState.OnShoppingListChanged -= OnShoppingListChanged;
    AppState.OnProcurementAnalysisChanged -= OnProcurementAnalysisChanged;
}
```

## Testing Checklist

When modifying state management, verify:
1. [ ] Switching tabs preserves shopping list
2. [ ] Switching tabs preserves analysis results
3. [ ] Adding items doesn't clear existing analysis
4. [ ] Removing items doesn't clear existing analysis
5. [ ] User can explicitly clear/re-run analysis when desired
6. [ ] Auto-save/restore works correctly
