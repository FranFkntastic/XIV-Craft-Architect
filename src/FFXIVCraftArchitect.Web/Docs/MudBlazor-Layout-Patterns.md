# MudBlazor Layout Patterns Guide

Common layout patterns and solutions for MudBlazor components in this project.

---

## Layout Bug Fix: Market Analysis Off-Screen

### Problem
Market analysis cards were rendering at x=2498px (off-screen to the right).

### Root Cause
Combining `MudGrid` with `Class="flex-column"` causes layout calculation issues where child elements get positioned at extreme x-coordinates.

```razor
<!-- ❌ BAD: MudGrid + flex-column causes off-screen positioning -->
<MudGrid Class="flex-column" Style="height: 100%;">
    <MudItem>Content goes way off-screen</MudItem>
</MudGrid>
```

### Solution
Use standard CSS flexbox on a container element instead:

```razor
<!-- ✅ GOOD: Standard flexbox with MudPaper -->
<MudPaper Style="height: 100%; display: flex; flex-direction: column;">
    <!-- Header -->
    <div Style="flex-shrink: 0;">Fixed header</div>
    
    <!-- Scrollable content -->
    <div Style="flex-grow: 1; overflow-y: auto; min-height: 0;">
        Scrollable content here
    </div>
</MudPaper>
```

### Key CSS Properties
- `flex-shrink: 0` - Prevents element from shrinking
- `flex-grow: 1` - Allows element to fill available space
- `overflow-y: auto` - Enables vertical scrolling
- `min-height: 0` - Critical for proper flex scroll behavior

---

## Layout Pattern: Three-Column Responsive Page

Used in: `Pages/Index.razor` (Market Logistics)

```razor
<MudContainer MaxWidth="MaxWidth.ExtraExtraLarge" Class="pa-0" 
              Style="height: calc(100vh - 64px);">
    <MudGrid Spacing="0" Style="height: 100%; margin: 0;">
        <!-- Left Column: Controls -->
        <MudItem xs="12" md="3" lg="2" 
                 Class="pa-4" 
                 Style="border-right: 1px solid #444; overflow-y: auto;">
            <!-- Controls content -->
        </MudItem>
        
        <!-- Middle Column: List -->
        <MudItem xs="12" md="4" lg="3" 
                 Class="pa-4" 
                 Style="border-right: 1px solid #444; overflow-y: auto;">
            <!-- List content -->
        </MudItem>
        
        <!-- Right Column: Results -->
        <MudItem xs="12" md="5" lg="7" 
                 Class="pa-4" 
                 Style="overflow: hidden; display: flex; flex-direction: column;">
            <!-- Results content -->
        </MudItem>
    </MudGrid>
</MudContainer>
```

### Breakpoints
- `xs="12"` - Mobile: full width, stacked vertically
- `md="X"` - Tablet: side by side
- `lg="X"` - Desktop: wider proportions

---

## Layout Pattern: Header + Scrollable Content

Used in: `Shared/MarketLogisticsPanel.razor`

```razor
<MudPaper Style="height: 100%; display: flex; flex-direction: column;">
    
    <!-- Fixed Header -->
    <MudPaper Elevation="1" Style="flex-shrink: 0;">
        Filter controls here
    </MudPaper>
    
    <!-- Scrollable Content -->
    <MudPaper Elevation="1" 
              Style="flex-grow: 1; overflow-y: auto; min-height: 0;">
        @foreach (var item in Items)
        {
            <MarketCard Item="item" />
        }
    </MudPaper>
</MudPaper>
```

---

## When to Use Each Layout Approach

| Scenario | Approach | Example |
|----------|----------|---------|
| Multi-column responsive | `MudGrid` + `MudItem` | Index.razor columns |
| Vertical flex layout | CSS flexbox on container | MarketLogisticsPanel |
| Card grids | `MudGrid` without flex | Recipe cards grid |
| Simple horizontal/vertical | `MudStack` | Button groups |

---

## Debugging Layout Issues

### 1. Use Browser DevTools
- Right-click → Inspect Element
- Check computed styles for `position`, `width`, `left`
- Look for extreme values (e.g., left: 2498px)

### 2. Add Debug Borders
```razor
<div Style="border: 2px solid red;">Debug this container</div>
```

### 3. Check Overflow
```javascript
// In browser console
document.querySelectorAll('*').forEach(el => {
    const rect = el.getBoundingClientRect();
    if (rect.left > window.innerWidth) {
        console.log('Off-screen element:', el, rect);
    }
});
```

### 4. Common Symptoms
- Content appears "cut off" → Check `overflow: hidden`
- Content pushed off-screen → Check `flex-direction` on parent
- Unexpected spacing → Check `MudGrid` gutters

---

## MudBlazor Grid vs Flexbox Quick Reference

### MudGrid + MudItem
✅ Good for:
- Responsive column layouts
- Page-level structure
- Card grids

❌ Avoid when:
- Need vertical flex behavior
- Nesting grids deeply
- Combining with `flex-direction: column`

### CSS Flexbox (on MudPaper/div)
✅ Good for:
- Header + content layouts
- Vertical stacking
- Fixed + scrollable areas

❌ Avoid when:
- Need responsive breakpoints
- Complex multi-column layouts
