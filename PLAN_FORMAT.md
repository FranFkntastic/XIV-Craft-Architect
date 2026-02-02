# FFXIV Craft Architect - Plan JSON Format Specification

This document describes the JSON format used for saving and loading crafting plans.

## Overview

Plan files are stored in the `Plans/` directory with a `.json` extension.

## Root Structure

```json
{
  "version": 1,
  "id": "guid-here",
  "name": "Plan Name",
  "createdAt": "2026-01-31T12:00:00Z",
  "modifiedAt": "2026-01-31T12:30:00Z",
  "dataCenter": "Aether",
  "world": "Entire Data Center",
  "rootItems": [],
  "marketPlans": []
}
```

## PlanNode Structure

| Field | Type | Description |
|-------|------|-------------|
| `itemId` | int | FFXIV item ID |
| `name` | string | Item name |
| `iconId` | int | Icon ID for images |
| `quantity` | int | How many needed |
| `source` | int | 0=Craft, 1=Buy NQ, 2=Buy HQ, 3=Vendor |
| `requiresHq` | bool | HQ required |
| `isUncraftable` | bool | DEPRECATED |
| `recipeLevel` | int | Recipe level |
| `job` | string | Job name |
| `yield` | int | Items per craft |
| `marketPrice` | decimal | Price per unit |
| `children` | array | Ingredient nodes |
| `nodeId` | string | Unique node ID |
| `parentNodeId` | string | Parent reference |

## AcquisitionSource Enum

| Value | Name | Description |
|-------|------|-------------|
| 0 | Craft | Craft using components |
| 1 | MarketBuyNq | Buy NQ from market |
| 2 | MarketBuyHq | Buy HQ from market |
| 3 | VendorBuy | Buy from vendor |

## MarketPlans Structure

| Field | Type | Description |
|-------|------|-------------|
| `itemId` | int | Item ID |
| `name` | string | Item name |
| `quantityNeeded` | int | Quantity needed |
| `dcAveragePrice` | decimal | DC average price |
| `worldOptions` | array | World summaries |
| `recommendedWorld` | object | Best world option |

### WorldShoppingSummary

| Field | Type | Description |
|-------|------|-------------|
| `worldName` | string | World name |
| `totalCost` | long | Total gil cost |
| `averagePricePerUnit` | decimal | Avg price/unit |
| `listingsUsed` | int | Listings needed |
| `excessQuantity` | int | Extra bought |
| `listings` | array | Market listings |

### ShoppingListingEntry

| Field | Type | Description |
|-------|------|-------------|
| `quantity` | int | Stack size |
| `pricePerUnit` | long | Price each |
| `retainerName` | string | Seller name |
| `isUnderAverage` | bool | Under DC avg |
| `isHq` | bool | HQ listing |
| `neededFromStack` | int | Needed qty |
| `excessQuantity` | int | Extra qty |

## Debugging Commands

### 1. Check Root Items

PowerShell command to list all root items:

```powershell
$plan = Get-Content 'Plan.json' | ConvertFrom-Json
$plan.RootItems | Select-Object itemId, name, quantity
```

### 2. Find (Circular) Items

PowerShell command to find items marked as circular:

```powershell
$plan = Get-Content 'Plan.json' | ConvertFrom-Json
function Find-Circular($node) {
    if ($node.name -like "*(Circular)*") {
        [PSCustomObject]@{
            ItemId = $node.itemId
            Name = $node.name
        }
    }
    foreach ($child in $node.children) {
        Find-Circular $child
    }
}
$plan.RootItems | ForEach-Object { Find-Circular $_ }
```

### 3. Count Total Nodes

PowerShell command to count all nodes:

```powershell
$plan = Get-Content 'Plan.json' | ConvertFrom-Json
function Count-Nodes($node) {
    $count = 1
    foreach ($child in $node.children) {
        $count += (Count-Nodes $child)
    }
    return $count
}
$total = ($plan.RootItems | ForEach-Object { Count-Nodes $_ } | Measure-Object -Sum).Sum
Write-Host "Total nodes: $total"
```

### 4. Find Items Without Prices

PowerShell command to find items missing market data:

```powershell
$plan = Get-Content 'Plan.json' | ConvertFrom-Json
function Find-NoPrice($node) {
    if ($node.marketPrice -eq 0 -and $node.source -ne 0) {
        [PSCustomObject]@{
            Name = $node.name
            ItemId = $node.itemId
        }
    }
    foreach ($child in $node.children) { Find-NoPrice $child }
}
$plan.RootItems | ForEach-Object { Find-NoPrice $_ }
```

## Common Issues

### Missing Main Craft
If root items are missing (e.g., imported from Artisan but main craft not present), check the import logs. Common causes:
- XIVAPI lookup failure
- Recipe ID not found in Garland
- Network timeout during import

### (Circular) Items
Items marked "(Circular)" indicate the circular dependency detection triggered. This can be:
- True circular: Item A requires B which requires A
- False positive: Bug where shared ingredients across root items are flagged

### Empty Children
If `children` array is empty for a craftable item:
- Item was marked as buy (source=1 or 2)
- Recipe lookup failed during build
- Item is genuinely uncraftable (gathered/dropped)

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1 | 2026-01-31 | Initial format |
