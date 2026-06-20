# Trade Lodestone NetStone Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a dirty Trade Crafters import spike that can search Lodestone via NetStone, preview crafter job levels, and create or update local crafter profiles.

**Architecture:** Add a project-owned lookup boundary and DTOs in `.Core`, then implement a first NetStone-backed provider in the Blazor web project. The spike intentionally tries the direct WebAssembly path first so we can learn whether CORS/runtime constraints block it before adding a backend or local helper project.

**Tech Stack:** .NET 8, Blazor WebAssembly, MudBlazor, NetStone, IndexedDB-backed Trade persistence, xUnit.

---

## File Map

- Modify `src/FFXIV Craft Architect.Core/Models/TradeOperationsModels.cs`
  - Add Lodestone provenance fields to `TradeCrafterProfile`.
- Create `src/FFXIV Craft Architect.Core/Models/LodestoneCrafterImportModels.cs`
  - Define lookup DTOs, import preview DTOs, and explicit failure result types.
- Create `src/FFXIV Craft Architect.Core/Services/Interfaces/ILodestoneCrafterLookupService.cs`
  - Define search and import-preview methods consumed by UI.
- Create `src/FFXIV Craft Architect.Core/Services/TradeCrafterProfileImportMapper.cs`
  - Map lookup imports onto `TradeCrafterProfile` without touching manual notes/contact/payment fields.
- Modify `src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj`
  - Add `NetStone` package reference for the direct dirty spike.
- Create `src/FFXIV Craft Architect.Web/Services/NetStoneLodestoneCrafterLookupService.cs`
  - Implement the lookup interface using NetStone.
- Modify `src/FFXIV Craft Architect.Web/Program.cs`
  - Register `ILodestoneCrafterLookupService` and `TradeCrafterProfileImportMapper`.
- Modify `src/FFXIV Craft Architect.Web/Pages/TradeCrafters.razor`
  - Add an import panel/dialog path: search, select result, preview DoH levels, create/update crafter.
- Modify `src/FFXIV Craft Architect.Web/Pages/TradeCrafters.razor.css`
  - Add compact styles for the import controls and preview.
- Modify `src/FFXIV Craft Architect.Tests/TradeOperationsModelTests.cs`
  - Assert Lodestone provenance survives simple model use.
- Create `src/FFXIV Craft Architect.Tests/TradeCrafterProfileImportMapperTests.cs`
  - Assert imported fields and job levels merge correctly.
- Modify `src/FFXIV Craft Architect.Tests/TradeCraftersMarkupTests.cs`
  - Assert the Crafters page exposes import/search UI and calls the lookup service.
- Optional create `src/FFXIV Craft Architect.Tests/NetStoneLodestoneCrafterLookupServiceTests.cs`
  - Keep network-dependent tests skipped by default.
- Modify `plans/trade-lodestone-crafter-import-feasibility.md`
  - Add final spike outcome after implementation/browser test.

## Task 1: Core Import Contract

**Files:**
- Modify: `src/FFXIV Craft Architect.Core/Models/TradeOperationsModels.cs`
- Create: `src/FFXIV Craft Architect.Core/Models/LodestoneCrafterImportModels.cs`
- Create: `src/FFXIV Craft Architect.Core/Services/Interfaces/ILodestoneCrafterLookupService.cs`
- Test: `src/FFXIV Craft Architect.Tests/TradeOperationsModelTests.cs`

- [ ] **Step 1: Add a failing model/provenance test**

Add this test to `TradeOperationsModelTests`:

```csharp
[Fact]
public void TradeCrafterProfile_CanStoreLodestoneProvenance()
{
    var syncedAt = new DateTime(2026, 6, 20, 4, 0, 0, DateTimeKind.Utc);

    var crafter = new TradeCrafterProfile
    {
        DisplayName = "Level Checker",
        LodestoneCharacterId = "16331040",
        LodestoneProfileUrl = "https://na.finalfantasyxiv.com/lodestone/character/16331040/",
        LodestoneAvatarUrl = "https://img2.finalfantasyxiv.com/example.jpg",
        LodestoneFreeCompanyName = "Terms of Service",
        LodestoneLastSyncedAtUtc = syncedAt
    };

    Assert.Equal("16331040", crafter.LodestoneCharacterId);
    Assert.Equal("https://na.finalfantasyxiv.com/lodestone/character/16331040/", crafter.LodestoneProfileUrl);
    Assert.Equal("https://img2.finalfantasyxiv.com/example.jpg", crafter.LodestoneAvatarUrl);
    Assert.Equal("Terms of Service", crafter.LodestoneFreeCompanyName);
    Assert.Equal(syncedAt, crafter.LodestoneLastSyncedAtUtc);
}
```

- [ ] **Step 2: Run the focused failing test**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCrafterProfile_CanStoreLodestoneProvenance" -p:UseSharedCompilation=false
```

Expected: fail because the `TradeCrafterProfile` Lodestone properties do not exist.

- [ ] **Step 3: Add Lodestone provenance fields**

Add these properties to `TradeCrafterProfile` in `TradeOperationsModels.cs` after `DataCenter`:

```csharp
public string? LodestoneCharacterId { get; set; }
public string? LodestoneProfileUrl { get; set; }
public DateTime? LodestoneLastSyncedAtUtc { get; set; }
public string? LodestoneAvatarUrl { get; set; }
public string? LodestoneFreeCompanyName { get; set; }
```

- [ ] **Step 4: Add import DTOs**

Create `LodestoneCrafterImportModels.cs`:

```csharp
namespace FFXIV_Craft_Architect.Core.Models;

public sealed record LodestoneCrafterSearchRequest(
    string CharacterName,
    string? WorldName,
    string? DataCenter);

public sealed record LodestoneCrafterSearchCandidate(
    string LodestoneCharacterId,
    string DisplayName,
    string? WorldName,
    string? DataCenter,
    string LodestoneProfileUrl);

public sealed record LodestoneCrafterImportPreview(
    string LodestoneCharacterId,
    string DisplayName,
    string? WorldName,
    string? DataCenter,
    string LodestoneProfileUrl,
    string? AvatarUrl,
    string? FreeCompanyName,
    DateTime RetrievedAtUtc,
    IReadOnlyList<TradeCraftingJobLevel> JobLevels);

public enum LodestoneCrafterLookupFailureKind
{
    None,
    InvalidRequest,
    NotFound,
    NetworkUnavailable,
    BrowserCorsBlocked,
    ParseFailed,
    Unknown
}

public sealed class LodestoneCrafterLookupResult<T>
{
    public T? Value { get; init; }
    public LodestoneCrafterLookupFailureKind FailureKind { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Succeeded => FailureKind == LodestoneCrafterLookupFailureKind.None;

    public static LodestoneCrafterLookupResult<T> Success(T value)
    {
        return new LodestoneCrafterLookupResult<T>
        {
            Value = value,
            FailureKind = LodestoneCrafterLookupFailureKind.None
        };
    }

    public static LodestoneCrafterLookupResult<T> Failure(
        LodestoneCrafterLookupFailureKind failureKind,
        string errorMessage)
    {
        return new LodestoneCrafterLookupResult<T>
        {
            FailureKind = failureKind,
            ErrorMessage = errorMessage
        };
    }
}
```

- [ ] **Step 5: Add lookup interface**

Create `ILodestoneCrafterLookupService.cs`:

```csharp
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface ILodestoneCrafterLookupService
{
    Task<LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>> SearchAsync(
        LodestoneCrafterSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>> GetImportPreviewAsync(
        string lodestoneCharacterId,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 6: Run focused tests**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCrafterProfile_CanStoreLodestoneProvenance" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add "src\FFXIV Craft Architect.Core\Models\TradeOperationsModels.cs" "src\FFXIV Craft Architect.Core\Models\LodestoneCrafterImportModels.cs" "src\FFXIV Craft Architect.Core\Services\Interfaces\ILodestoneCrafterLookupService.cs" "src\FFXIV Craft Architect.Tests\TradeOperationsModelTests.cs"
git commit -m "Add Lodestone crafter import contract"
```

## Task 2: Import Mapper

**Files:**
- Create: `src/FFXIV Craft Architect.Core/Services/TradeCrafterProfileImportMapper.cs`
- Create: `src/FFXIV Craft Architect.Tests/TradeCrafterProfileImportMapperTests.cs`

- [ ] **Step 1: Write mapper tests**

Create `TradeCrafterProfileImportMapperTests.cs`:

```csharp
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class TradeCrafterProfileImportMapperTests
{
    [Fact]
    public void CreateProfile_CopiesLodestoneFieldsAndJobLevels()
    {
        var companyProfileId = Guid.NewGuid();
        var retrievedAt = new DateTime(2026, 6, 20, 5, 0, 0, DateTimeKind.Utc);
        var preview = CreatePreview(retrievedAt);
        var mapper = new TradeCrafterProfileImportMapper();

        var profile = mapper.CreateProfile(companyProfileId, preview);

        Assert.Equal(companyProfileId, profile.CompanyProfileId);
        Assert.Equal("Level Checker", profile.DisplayName);
        Assert.Equal("Behemoth", profile.WorldName);
        Assert.Equal("Primal", profile.DataCenter);
        Assert.Equal("16331040", profile.LodestoneCharacterId);
        Assert.Equal(preview.LodestoneProfileUrl, profile.LodestoneProfileUrl);
        Assert.Equal(preview.AvatarUrl, profile.LodestoneAvatarUrl);
        Assert.Equal(preview.FreeCompanyName, profile.LodestoneFreeCompanyName);
        Assert.Equal(retrievedAt, profile.LodestoneLastSyncedAtUtc);
        Assert.Contains(profile.JobLevels, level => level.Job == TradeCraftingJob.Carpenter && level.Level == 100);
    }

    [Fact]
    public void UpdateProfile_PreservesManualFields()
    {
        var retrievedAt = new DateTime(2026, 6, 20, 5, 0, 0, DateTimeKind.Utc);
        var existing = new TradeCrafterProfile
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            DisplayName = "Old Name",
            ContactHandle = "discord-user",
            PaymentNotes = "manual payment notes",
            OperatorNotes = "manual operator notes",
            AvailabilityNotes = "manual availability",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var mapper = new TradeCrafterProfileImportMapper();

        var updated = mapper.UpdateProfile(existing, CreatePreview(retrievedAt));

        Assert.Equal(existing.Id, updated.Id);
        Assert.Equal(existing.CompanyProfileId, updated.CompanyProfileId);
        Assert.Equal("discord-user", updated.ContactHandle);
        Assert.Equal("manual payment notes", updated.PaymentNotes);
        Assert.Equal("manual operator notes", updated.OperatorNotes);
        Assert.Equal("manual availability", updated.AvailabilityNotes);
        Assert.Equal(existing.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal("Level Checker", updated.DisplayName);
        Assert.Equal("16331040", updated.LodestoneCharacterId);
    }

    private static LodestoneCrafterImportPreview CreatePreview(DateTime retrievedAt)
    {
        return new LodestoneCrafterImportPreview(
            "16331040",
            "Level Checker",
            "Behemoth",
            "Primal",
            "https://na.finalfantasyxiv.com/lodestone/character/16331040/",
            "https://img2.finalfantasyxiv.com/example.jpg",
            "Terms of Service",
            retrievedAt,
            new[]
            {
                new TradeCraftingJobLevel(TradeCraftingJob.Carpenter, 100),
                new TradeCraftingJobLevel(TradeCraftingJob.Blacksmith, 100)
            });
    }
}
```

- [ ] **Step 2: Run failing mapper tests**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCrafterProfileImportMapperTests" -p:UseSharedCompilation=false
```

Expected: fail because `TradeCrafterProfileImportMapper` does not exist.

- [ ] **Step 3: Add mapper implementation**

Create `TradeCrafterProfileImportMapper.cs`:

```csharp
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class TradeCrafterProfileImportMapper
{
    public TradeCrafterProfile CreateProfile(Guid companyProfileId, LodestoneCrafterImportPreview preview)
    {
        var now = DateTime.UtcNow;
        return ApplyPreview(
            new TradeCrafterProfile
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = companyProfileId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            preview);
    }

    public TradeCrafterProfile UpdateProfile(TradeCrafterProfile existing, LodestoneCrafterImportPreview preview)
    {
        return ApplyPreview(CopyProfile(existing), preview);
    }

    private static TradeCrafterProfile ApplyPreview(
        TradeCrafterProfile profile,
        LodestoneCrafterImportPreview preview)
    {
        profile.DisplayName = preview.DisplayName;
        profile.WorldName = NormalizeOptional(preview.WorldName);
        profile.DataCenter = NormalizeOptional(preview.DataCenter);
        profile.LodestoneCharacterId = preview.LodestoneCharacterId;
        profile.LodestoneProfileUrl = preview.LodestoneProfileUrl;
        profile.LodestoneAvatarUrl = NormalizeOptional(preview.AvatarUrl);
        profile.LodestoneFreeCompanyName = NormalizeOptional(preview.FreeCompanyName);
        profile.LodestoneLastSyncedAtUtc = preview.RetrievedAtUtc;
        profile.JobLevels = preview.JobLevels
            .Where(level => level.Level > 0)
            .OrderBy(level => level.Job)
            .ToArray();
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return profile;
    }

    private static TradeCrafterProfile CopyProfile(TradeCrafterProfile profile)
    {
        return new TradeCrafterProfile
        {
            Id = profile.Id,
            CompanyProfileId = profile.CompanyProfileId,
            DisplayName = profile.DisplayName,
            ContactHandle = profile.ContactHandle,
            WorldName = profile.WorldName,
            DataCenter = profile.DataCenter,
            LodestoneCharacterId = profile.LodestoneCharacterId,
            LodestoneProfileUrl = profile.LodestoneProfileUrl,
            LodestoneLastSyncedAtUtc = profile.LodestoneLastSyncedAtUtc,
            LodestoneAvatarUrl = profile.LodestoneAvatarUrl,
            LodestoneFreeCompanyName = profile.LodestoneFreeCompanyName,
            AvailabilityNotes = profile.AvailabilityNotes,
            PaymentNotes = profile.PaymentNotes,
            OperatorNotes = profile.OperatorNotes,
            JobLevels = profile.JobLevels.ToArray(),
            RemoteId = profile.RemoteId,
            SyncState = profile.SyncState,
            CreatedAtUtc = profile.CreatedAtUtc,
            UpdatedAtUtc = profile.UpdatedAtUtc
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
```

- [ ] **Step 4: Run mapper tests**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCrafterProfileImportMapperTests" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add "src\FFXIV Craft Architect.Core\Services\TradeCrafterProfileImportMapper.cs" "src\FFXIV Craft Architect.Tests\TradeCrafterProfileImportMapperTests.cs"
git commit -m "Add Trade crafter import mapper"
```

## Task 3: NetStone Lookup Provider

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj`
- Create: `src/FFXIV Craft Architect.Web/Services/NetStoneLodestoneCrafterLookupService.cs`
- Modify: `src/FFXIV Craft Architect.Web/Program.cs`

- [ ] **Step 1: Add NetStone package**

Run:

```powershell
dotnet add "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" package NetStone --version 1.4.1
```

Expected: package restore succeeds.

- [ ] **Step 2: Add NetStone lookup implementation**

Create `NetStoneLodestoneCrafterLookupService.cs`:

```csharp
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using NetStone;
using NetStone.Search.Character;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class NetStoneLodestoneCrafterLookupService : ILodestoneCrafterLookupService
{
    private const string LodestoneProfileBaseUrl = "https://na.finalfantasyxiv.com/lodestone/character/";
    private readonly ILogger<NetStoneLodestoneCrafterLookupService> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private LodestoneClient? _client;

    public NetStoneLodestoneCrafterLookupService(ILogger<NetStoneLodestoneCrafterLookupService> logger)
    {
        _logger = logger;
    }

    public async Task<LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>> SearchAsync(
        LodestoneCrafterSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CharacterName))
        {
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.InvalidRequest,
                "Character name is required.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = await GetClientAsync(cancellationToken);
            var page = await client.SearchCharacter(new CharacterSearchQuery
            {
                CharacterName = request.CharacterName.Trim(),
                World = request.WorldName?.Trim() ?? string.Empty,
                DataCenter = string.IsNullOrWhiteSpace(request.WorldName)
                    ? request.DataCenter?.Trim() ?? string.Empty
                    : string.Empty
            });

            var candidates = page?.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Id))
                .Take(10)
                .Select(result => new LodestoneCrafterSearchCandidate(
                    result.Id!,
                    result.Name,
                    request.WorldName,
                    request.DataCenter,
                    $"{LodestoneProfileBaseUrl}{result.Id}/"))
                .ToArray() ?? Array.Empty<LodestoneCrafterSearchCandidate>();

            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Success(candidates);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lodestone character search failed for {CharacterName}", request.CharacterName);
            return ToNetworkFailure<IReadOnlyList<LodestoneCrafterSearchCandidate>>(ex);
        }
        catch (Exception ex) when (IsLikelyBrowserCorsFailure(ex))
        {
            _logger.LogWarning(ex, "Lodestone character search was blocked by the browser runtime");
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.BrowserCorsBlocked,
                "The browser blocked direct Lodestone lookup. This spike needs a local helper or hosted lookup endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Lodestone character search failure");
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.Unknown,
                ex.Message);
        }
    }

    public async Task<LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>> GetImportPreviewAsync(
        string lodestoneCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lodestoneCharacterId))
        {
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.InvalidRequest,
                "Lodestone character id is required.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = await GetClientAsync(cancellationToken);
            var character = await client.GetCharacter(lodestoneCharacterId.Trim());
            if (character == null)
            {
                return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                    LodestoneCrafterLookupFailureKind.NotFound,
                    "Lodestone character was not found.");
            }

            var jobs = await client.GetCharacterClassJob(lodestoneCharacterId.Trim());
            if (jobs == null)
            {
                return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                    LodestoneCrafterLookupFailureKind.ParseFailed,
                    "Lodestone class/job data could not be loaded.");
            }

            var preview = new LodestoneCrafterImportPreview(
                lodestoneCharacterId.Trim(),
                character.Name,
                character.Server,
                null,
                $"{LodestoneProfileBaseUrl}{lodestoneCharacterId.Trim()}/",
                character.Avatar?.ToString(),
                character.FreeCompany?.Name,
                DateTime.UtcNow,
                new[]
                {
                    new TradeCraftingJobLevel(TradeCraftingJob.Carpenter, jobs.Carpenter.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Blacksmith, jobs.Blacksmith.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Armorer, jobs.Armorer.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Goldsmith, jobs.Goldsmith.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Leatherworker, jobs.Leatherworker.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Weaver, jobs.Weaver.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Alchemist, jobs.Alchemist.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Culinarian, jobs.Culinarian.Level)
                });

            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Success(preview);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lodestone import preview failed for {LodestoneCharacterId}", lodestoneCharacterId);
            return ToNetworkFailure<LodestoneCrafterImportPreview>(ex);
        }
        catch (Exception ex) when (IsLikelyBrowserCorsFailure(ex))
        {
            _logger.LogWarning(ex, "Lodestone import preview was blocked by the browser runtime");
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.BrowserCorsBlocked,
                "The browser blocked direct Lodestone lookup. This spike needs a local helper or hosted lookup endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Lodestone import preview failure");
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.Unknown,
                ex.Message);
        }
    }

    private async Task<LodestoneClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            return _client;
        }

        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            _client ??= await LodestoneClient.GetClientAsync();
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static LodestoneCrafterLookupResult<T> ToNetworkFailure<T>(Exception ex)
    {
        return LodestoneCrafterLookupResult<T>.Failure(
            IsLikelyBrowserCorsFailure(ex)
                ? LodestoneCrafterLookupFailureKind.BrowserCorsBlocked
                : LodestoneCrafterLookupFailureKind.NetworkUnavailable,
            ex.Message);
    }

    private static bool IsLikelyBrowserCorsFailure(Exception ex)
    {
        return ex.Message.Contains("CORS", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
               ex.ToString().Contains("BrowserHttpHandler", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Register services**

In `Program.cs`, add:

```csharp
builder.Services.AddScoped<TradeCrafterProfileImportMapper>();
builder.Services.AddScoped<ILodestoneCrafterLookupService, NetStoneLodestoneCrafterLookupService>();
```

Also add any missing `using` statements:

```csharp
using FFXIV_Craft_Architect.Core.Services.Interfaces;
```

- [ ] **Step 4: Build web project**

Run:

```powershell
dotnet build "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" -p:UseSharedCompilation=false
```

Expected: either pass, or fail with an explicit WebAssembly/package incompatibility. If it fails due NetStone/browser incompatibility at compile time, stop and pivot the implementation plan to a local helper/server endpoint.

- [ ] **Step 5: Commit**

```powershell
git add "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" "src\FFXIV Craft Architect.Web\Services\NetStoneLodestoneCrafterLookupService.cs" "src\FFXIV Craft Architect.Web\Program.cs"
git commit -m "Add NetStone Lodestone lookup provider spike"
```

## Task 4: Crafters Page Import UI

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradeCrafters.razor`
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradeCrafters.razor.css`
- Modify: `src/FFXIV Craft Architect.Tests/TradeCraftersMarkupTests.cs`

- [ ] **Step 1: Add markup test assertions**

Add assertions to the existing Trade Crafters markup test:

```csharp
Assert.Contains("ILodestoneCrafterLookupService", source);
Assert.Contains("Import from Lodestone", source);
Assert.Contains("Search Lodestone", source);
Assert.Contains("PreviewLodestoneCandidateAsync", source);
Assert.Contains("CreateCrafterFromLodestoneAsync", source);
```

- [ ] **Step 2: Run failing markup test**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCraftersMarkupTests" -p:UseSharedCompilation=false
```

Expected: fail because the import UI is not wired yet.

- [ ] **Step 3: Inject lookup and mapper**

In `TradeCrafters.razor`, add:

```razor
@inject ILodestoneCrafterLookupService LodestoneCrafterLookup
@inject TradeCrafterProfileImportMapper LodestoneImportMapper
```

- [ ] **Step 4: Add import state fields**

Add fields in the `@code` block:

```csharp
private string _lodestoneSearchName = string.Empty;
private string _lodestoneSearchWorld = string.Empty;
private bool _lodestoneIsBusy;
private string? _lodestoneError;
private IReadOnlyList<LodestoneCrafterSearchCandidate> _lodestoneCandidates = [];
private LodestoneCrafterImportPreview? _lodestonePreview;
```

- [ ] **Step 5: Add import panel markup**

Place this panel above the existing Create Crafter panel:

```razor
<section class="trade-crafters-panel trade-crafters-lodestone-import">
    <MudText Typo="Typo.subtitle1">Import from Lodestone</MudText>
    <div class="trade-crafters-create-row">
        <MudTextField @bind-Value="_lodestoneSearchName"
                      Label="Character name"
                      Variant="Variant.Outlined"
                      Margin="Margin.Dense"
                      Immediate="true" />
        <MudSelect T="string"
                   @bind-Value="_lodestoneSearchWorld"
                   Label="World"
                   Variant="Variant.Outlined"
                   Margin="Margin.Dense">
            <MudSelectItem Value="@string.Empty">Use selected data center</MudSelectItem>
            @foreach (var world in GetWorldsForDataCenter(GetDefaultDataCenter()))
            {
                <MudSelectItem Value="@world">@world</MudSelectItem>
            }
        </MudSelect>
        <MudButton Variant="Variant.Filled"
                   Color="Color.Primary"
                   OnClick="SearchLodestoneAsync"
                   Disabled="@(_lodestoneIsBusy || string.IsNullOrWhiteSpace(_lodestoneSearchName))">
            Search Lodestone
        </MudButton>
    </div>
    @if (!string.IsNullOrWhiteSpace(_lodestoneError))
    {
        <MudAlert Severity="Severity.Warning" Dense="true">@_lodestoneError</MudAlert>
    }
    @if (_lodestoneCandidates.Count > 0)
    {
        <div class="trade-crafters-lodestone-results">
            @foreach (var candidate in _lodestoneCandidates)
            {
                <button type="button"
                        class="trade-crafters-lodestone-result"
                        @onclick="@(() => PreviewLodestoneCandidateAsync(candidate))">
                    <span>@candidate.DisplayName</span>
                    <small>@candidate.WorldName @candidate.LodestoneCharacterId</small>
                </button>
            }
        </div>
    }
    @if (_lodestonePreview != null)
    {
        <div class="trade-crafters-lodestone-preview">
            <strong>@_lodestonePreview.DisplayName</strong>
            <span>@_lodestonePreview.WorldName</span>
            <span>@FormatImportedJobSummary(_lodestonePreview)</span>
            <MudButton Variant="Variant.Filled"
                       Color="Color.Primary"
                       OnClick="CreateCrafterFromLodestoneAsync"
                       Disabled="@_lodestoneIsBusy">
                Create Crafter From Lodestone
            </MudButton>
            @if (_selectedCrafter != null)
            {
                <MudButton Variant="Variant.Outlined"
                           Color="Color.Secondary"
                           OnClick="UpdateSelectedCrafterFromLodestoneAsync"
                           Disabled="@_lodestoneIsBusy">
                    Update Selected Crafter
                </MudButton>
            }
        </div>
    }
</section>
```

- [ ] **Step 6: Add import methods**

Add methods in the `@code` block:

```csharp
private async Task SearchLodestoneAsync()
{
    _lodestoneIsBusy = true;
    _lodestoneError = null;
    _lodestonePreview = null;
    try
    {
        var result = await LodestoneCrafterLookup.SearchAsync(new LodestoneCrafterSearchRequest(
            _lodestoneSearchName,
            NormalizeOptionalText(_lodestoneSearchWorld),
            string.IsNullOrWhiteSpace(_lodestoneSearchWorld) ? GetDefaultDataCenter() : null));

        if (!result.Succeeded || result.Value == null)
        {
            _lodestoneCandidates = [];
            _lodestoneError = result.ErrorMessage ?? "Lodestone search failed.";
            return;
        }

        _lodestoneCandidates = result.Value;
        if (_lodestoneCandidates.Count == 0)
        {
            _lodestoneError = "No Lodestone characters matched that search.";
        }
    }
    finally
    {
        _lodestoneIsBusy = false;
    }
}

private async Task PreviewLodestoneCandidateAsync(LodestoneCrafterSearchCandidate candidate)
{
    _lodestoneIsBusy = true;
    _lodestoneError = null;
    try
    {
        var result = await LodestoneCrafterLookup.GetImportPreviewAsync(candidate.LodestoneCharacterId);
        if (!result.Succeeded || result.Value == null)
        {
            _lodestonePreview = null;
            _lodestoneError = result.ErrorMessage ?? "Lodestone preview failed.";
            return;
        }

        _lodestonePreview = result.Value;
    }
    finally
    {
        _lodestoneIsBusy = false;
    }
}

private async Task CreateCrafterFromLodestoneAsync()
{
    if (_companyProfile == null || _lodestonePreview == null)
    {
        return;
    }

    var crafter = LodestoneImportMapper.CreateProfile(_companyProfile.Id, _lodestonePreview);
    var saved = await TradeOperationsPersistence.SaveCrafterAsync(crafter);
    if (!saved)
    {
        Snackbar.Add("Failed to save Lodestone crafter.", Severity.Error);
        return;
    }

    await LoadAsync();
    SelectCrafterAfterReload(crafter.Id, "Crafter imported, but it could not be loaded.");
    Snackbar.Add("Crafter imported from Lodestone", Severity.Success);
}

private async Task UpdateSelectedCrafterFromLodestoneAsync()
{
    if (_selectedCrafter == null || _lodestonePreview == null)
    {
        return;
    }

    var crafter = LodestoneImportMapper.UpdateProfile(_selectedCrafter, _lodestonePreview);
    var saved = await TradeOperationsPersistence.SaveCrafterAsync(crafter);
    if (!saved)
    {
        Snackbar.Add("Failed to update crafter from Lodestone.", Severity.Error);
        return;
    }

    await LoadAsync();
    SelectCrafterAfterReload(crafter.Id, "Crafter updated, but it could not be loaded.");
    Snackbar.Add("Crafter updated from Lodestone", Severity.Success);
}

private static string FormatImportedJobSummary(LodestoneCrafterImportPreview preview)
{
    var maxLevel = preview.JobLevels.Count == 0 ? 0 : preview.JobLevels.Max(level => level.Level);
    return maxLevel == 0
        ? "No crafter levels found"
        : $"{preview.JobLevels.Count(level => level.Level > 0):N0} jobs / {maxLevel:N0}";
}
```

- [ ] **Step 7: Preserve provenance in local copy helper**

Update `CopyCrafter` in `TradeCrafters.razor` to copy the new Lodestone properties:

```csharp
LodestoneCharacterId = crafter.LodestoneCharacterId,
LodestoneProfileUrl = crafter.LodestoneProfileUrl,
LodestoneLastSyncedAtUtc = crafter.LodestoneLastSyncedAtUtc,
LodestoneAvatarUrl = crafter.LodestoneAvatarUrl,
LodestoneFreeCompanyName = crafter.LodestoneFreeCompanyName,
```

- [ ] **Step 8: Add CSS**

Add to `TradeCrafters.razor.css`:

```css
.trade-crafters-lodestone-results,
.trade-crafters-lodestone-preview {
  display: grid;
  gap: 8px;
  margin-top: 10px;
}

.trade-crafters-lodestone-result {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  width: 100%;
  padding: 8px 10px;
  border: 1px solid rgba(255, 255, 255, 0.14);
  background: rgba(255, 255, 255, 0.03);
  color: inherit;
  text-align: left;
}

.trade-crafters-lodestone-result:hover {
  border-color: rgba(146, 198, 99, 0.55);
}

.trade-crafters-lodestone-result small,
.trade-crafters-lodestone-preview span {
  color: rgba(255, 255, 255, 0.68);
}
```

- [ ] **Step 9: Run markup and web build checks**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~TradeCraftersMarkupTests" -p:UseSharedCompilation=false
dotnet build "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 10: Commit**

```powershell
git add "src\FFXIV Craft Architect.Web\Pages\TradeCrafters.razor" "src\FFXIV Craft Architect.Web\Pages\TradeCrafters.razor.css" "src\FFXIV Craft Architect.Tests\TradeCraftersMarkupTests.cs"
git commit -m "Add Lodestone import spike UI"
```

## Task 5: Browser Runtime Verification

**Files:**
- Modify: `plans/trade-lodestone-crafter-import-feasibility.md`

- [ ] **Step 1: Start the web app**

Run:

```powershell
dotnet run --project "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" --urls "http://localhost:5001"
```

Expected: app serves at `http://localhost:5001`.

- [ ] **Step 2: Try the import flow**

In the browser:

1. Open `http://localhost:5001/trade/crafters`.
2. Search for `Level Checker`.
3. Use world `Behemoth` if the world selector is available.
4. Select the returned candidate.
5. Confirm whether the preview shows eight crafter jobs at level `100`.
6. Create the crafter and refresh the page.
7. Confirm the crafter remains in the roster with imported job levels.

Expected dirty-spike result A: import works directly in Blazor WASM.

Expected dirty-spike result B: import fails with `BrowserCorsBlocked`; the UI shows a clear warning and no data is saved.

- [ ] **Step 3: Document runtime outcome**

Append one of these outcomes to `plans/trade-lodestone-crafter-import-feasibility.md`:

```markdown
## Dirty Spike Runtime Result

Direct NetStone lookup from Blazor WebAssembly succeeded in local development. The first production implementation can continue with the direct provider, while keeping `ILodestoneCrafterLookupService` as the replacement seam.
```

or:

```markdown
## Dirty Spike Runtime Result

Direct NetStone lookup from Blazor WebAssembly failed because the browser blocked Lodestone/definition requests. The next implementation slice should keep the UI and core mapper, replace `NetStoneLodestoneCrafterLookupService` with a local helper or hosted endpoint client, and run NetStone outside the browser.
```

- [ ] **Step 4: Commit runtime notes**

```powershell
git add "plans\trade-lodestone-crafter-import-feasibility.md"
git commit -m "Document Lodestone import spike runtime result"
```

## Task 6: Verification Sweep

**Files:**
- No new files unless verification uncovers a bug.

- [ ] **Step 1: Run focused Trade tests**

Run:

```powershell
dotnet test "src\FFXIV Craft Architect.Tests\FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~Trade" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 2: Run web build**

Run:

```powershell
dotnet build "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 3: Inspect git status**

Run:

```powershell
git status --short --branch
```

Expected: no uncommitted source changes except intentionally running dev-server artifacts, if any.

## Self-Review

- Spec coverage: The plan covers NetStone as the parser dependency, a project-owned lookup service, Lodestone provenance fields, user-confirmed search/preview/import, persistence through existing Trade crafters, and explicit handling of browser/CORS failure.
- Placeholder scan: No placeholder markers or unspecified implementation steps remain.
- Type consistency: `LodestoneCrafterSearchRequest`, `LodestoneCrafterSearchCandidate`, `LodestoneCrafterImportPreview`, `LodestoneCrafterLookupResult<T>`, and `ILodestoneCrafterLookupService` are introduced before use.
- Known rough edge: Search candidates will not have authoritative world/data center until preview fetch unless NetStone exposes more search row fields. That is acceptable for the dirty spike because the selected preview fetch is authoritative enough to create/update the local crafter.
