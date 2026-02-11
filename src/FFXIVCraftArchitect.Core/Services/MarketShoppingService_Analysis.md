# Market Shopping Algorithm Analysis

## Issue: Algorithm "Hallucinates" Stock for Large Orders

### Scenario
- Need: 52,800 Steel Ingots
- Available: ~200 on Adamantoise (cheapest world)
- Result: Algorithm recommends buying from Adamantoise for 213,000g

### Root Cause Analysis

#### Pass 1: CalculateWorldSummary (per world)
```csharp
// Lines 390-397
if (remaining > 0 && quantityNeeded <= 10000)
{
    return null; // Skip worlds with insufficient stock for small orders
}
// For orders > 10,000, continues even with insufficient stock!
```

For large orders (>10,000), the algorithm intentionally DOES NOT filter out worlds with insufficient stock. It returns a `WorldShoppingSummary` with:
- `HasSufficientStock = false`
- `ShortfallQuantity = remaining` (positive value)
- But still included in `WorldOptions`

#### Recommendation Assignment
```csharp
// Line 256
plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault();
// ^ Just takes cheapest, regardless of sufficiency
```

The cheapest world becomes "recommended" even if it can only fulfill 0.4% of the order.

#### Pass 2: CalculateSplitPurchase
```csharp
// Line 626
if (plan.RecommendedWorld != null && plan.RecommendedWorld.TotalQuantityPurchased >= plan.QuantityNeeded)
{
    return; // Skip split if single world is sufficient
}
// Otherwise tries to build multi-world split
```

If the single world is insufficient, it tries to build a split. But if:
- Only one world has any stock
- Or combined stock across all worlds < quantity needed

Then the split is partial but still recommended (line 715):
```csharp
if (plan.RecommendedWorld == null || config.MeetsSavingsThreshold(savingsPercent) || remaining <= 0)
{
    plan.RecommendedSplit = split; // Can be partial!
}
```

### The Bug

1. For large orders, worlds with ANY stock are included in options
2. The cheapest world becomes "recommended" regardless of sufficiency
3. Split calculation may produce partial results
4. No clear indication to user that stock is insufficient

### Solution Options

#### Option A: Filter insufficient stock worlds (regardless of order size)
Remove the special case for large orders:
```csharp
// Instead of:
if (remaining > 0 && quantityNeeded <= 10000) return null;

// Always:
if (remaining > 0) return null; // Don't include worlds that can't fulfill
```

**Problem**: Large orders would never get ANY recommendation since no single world can fulfill them.

#### Option B: Always prefer split for insufficient stock
If single world is insufficient, ALWAYS calculate and show split. Don't fall back to single world.

#### Option C: Mark insufficient recommendations
- Add `IsInsufficientStock` flag to plan
- Show warning in UI when recommendation can't fulfill quantity
- Still show the option (it's the best available), but clearly mark it

#### Option D: Required split for large orders
If `QuantityNeeded > 10000`, automatically force split calculation and never recommend single world.

### Recommended Fix (Option C + B hybrid)

1. Track total available stock across all worlds
2. If total available < quantity needed, mark plan as "InsufficientStock"
3. Still recommend best option (single or split), but with clear warning
4. In UI, show warning: "Only X of Y available across all worlds"
