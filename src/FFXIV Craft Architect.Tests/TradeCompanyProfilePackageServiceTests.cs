using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeCompanyProfilePackageServiceTests
{
    [Fact]
    public void CreateExportPackage_CapturesProfilePolicyAndRoster()
    {
        var service = new TradeCompanyProfilePackageService();
        var exportedAt = new DateTime(2026, 6, 25, 18, 0, 0, DateTimeKind.Utc);
        var profile = TradeCompanyProfile.CreateLocal("Aether Works", exportedAt.AddDays(-2));
        profile.Description = "Main branch roster";
        profile.PaymentPolicy = CreateLaborPolicy(exportedAt.AddDays(-1));
        var crafter = CreateCrafter(profile.Id, "Riviene Cahernaut", "Faerie", "Aether");

        var package = service.CreateExportPackage(profile, [crafter], exportedAt);

        Assert.Equal(TradeCompanyProfilePackage.CurrentFormatVersion, package.FormatVersion);
        Assert.Equal(TradeCompanyProfilePackage.PackageKindValue, package.PackageKind);
        Assert.Equal(exportedAt, package.ExportedAtUtc);
        Assert.Equal(profile.Id, package.Profile.Id);
        Assert.Equal("Aether Works", package.Profile.Name);
        Assert.Equal(profile.PaymentPolicy, package.Profile.PaymentPolicy);
        var exportedCrafter = Assert.Single(package.Crafters);
        Assert.Equal(crafter.Id, exportedCrafter.Id);
        Assert.Equal(profile.Id, exportedCrafter.CompanyProfileId);
        Assert.Equal("Riviene Cahernaut", exportedCrafter.DisplayName);
    }

    [Fact]
    public void ImportAsNewProfile_RemapsCompanyAndCrafterIdentity()
    {
        var service = new TradeCompanyProfilePackageService();
        var sourceCreatedAt = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
        var importedAt = new DateTime(2026, 6, 25, 18, 30, 0, DateTimeKind.Utc);
        var sourceProfile = TradeCompanyProfile.CreateLocal("Aether Works", sourceCreatedAt);
        sourceProfile.PaymentPolicy = CreateLaborPolicy(sourceCreatedAt);
        var sourceCrafter = CreateCrafter(sourceProfile.Id, "Riviene Cahernaut", "Faerie", "Aether");
        var package = service.CreateExportPackage(sourceProfile, [sourceCrafter], sourceCreatedAt);

        var imported = service.ImportAsNewProfile(package, importedAt);

        Assert.NotEqual(sourceProfile.Id, imported.Profile.Id);
        Assert.Equal("Aether Works", imported.Profile.Name);
        Assert.Equal(sourceProfile.PaymentPolicy, imported.Profile.PaymentPolicy);
        Assert.Equal(importedAt, imported.Profile.CreatedAtUtc);
        Assert.Equal(importedAt, imported.Profile.UpdatedAtUtc);
        Assert.Equal(TradeSyncState.LocalOnly, imported.Profile.SyncState);

        var importedCrafter = Assert.Single(imported.Crafters);
        Assert.NotEqual(sourceCrafter.Id, importedCrafter.Id);
        Assert.Equal(imported.Profile.Id, importedCrafter.CompanyProfileId);
        Assert.Equal("Riviene Cahernaut", importedCrafter.DisplayName);
        Assert.Equal("Faerie", importedCrafter.WorldName);
        Assert.Equal("Aether", importedCrafter.DataCenter);
        Assert.Equal(sourceCrafter.JobLevels, importedCrafter.JobLevels);
        Assert.Equal(importedAt, importedCrafter.CreatedAtUtc);
        Assert.Equal(importedAt, importedCrafter.UpdatedAtUtc);
        Assert.Equal(TradeSyncState.LocalOnly, importedCrafter.SyncState);
    }

    [Fact]
    public void ImportAsNewProfile_PreservesAllRosterEntriesInNewProfile()
    {
        var service = new TradeCompanyProfilePackageService();
        var timestamp = new DateTime(2026, 6, 25, 19, 0, 0, DateTimeKind.Utc);
        var sourceProfile = TradeCompanyProfile.CreateLocal("Aether Works", timestamp.AddDays(-1));
        var firstCrafter = CreateCrafter(sourceProfile.Id, "Riviene Cahernaut", "Faerie", "Aether");
        var secondCrafter = CreateCrafter(sourceProfile.Id, "Riviene Cahernaut", "Gilgamesh", "Aether");
        var package = service.CreateExportPackage(sourceProfile, [firstCrafter, secondCrafter], timestamp.AddHours(-1));

        var imported = service.ImportAsNewProfile(package, timestamp);

        Assert.Equal(2, imported.Crafters.Count);
        Assert.All(imported.Crafters, crafter => Assert.Equal(imported.Profile.Id, crafter.CompanyProfileId));
        Assert.Contains(imported.Crafters, crafter => crafter.WorldName == "Faerie");
        Assert.Contains(imported.Crafters, crafter => crafter.WorldName == "Gilgamesh");
    }

    [Fact]
    public void ExportPackage_RoundTripsThroughJson()
    {
        var service = new TradeCompanyProfilePackageService();
        var timestamp = new DateTime(2026, 6, 25, 19, 30, 0, DateTimeKind.Utc);
        var sourceProfile = TradeCompanyProfile.CreateLocal("Aether Works", timestamp.AddDays(-1));
        sourceProfile.PaymentPolicy = CreateLaborPolicy(timestamp);
        var package = service.CreateExportPackage(
            sourceProfile,
            [CreateCrafter(sourceProfile.Id, "Riviene Cahernaut", "Faerie", "Aether")],
            timestamp);

        var json = JsonSerializer.Serialize(package, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTripped = JsonSerializer.Deserialize<TradeCompanyProfilePackage>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });

        Assert.NotNull(roundTripped);
        Assert.Equal(TradeCompanyProfilePackage.PackageKindValue, roundTripped.PackageKind);
        Assert.Equal("Aether Works", roundTripped.Profile.Name);
        Assert.Equal(sourceProfile.PaymentPolicy, roundTripped.Profile.PaymentPolicy);
        Assert.NotNull(roundTripped.Profile.PaymentPolicy.LaborStandard);
        Assert.Equal(TradeLaborBenchmarkMode.CobaltRivets, roundTripped.Profile.PaymentPolicy.LaborStandard.BenchmarkMode);
        Assert.Single(roundTripped.Crafters);
    }

    [Fact]
    public void ExportPackage_CustomLaborBenchmarkRoundTripsThroughJson()
    {
        var service = new TradeCompanyProfilePackageService();
        var timestamp = new DateTime(2026, 6, 25, 19, 45, 0, DateTimeKind.Utc);
        var sourceProfile = TradeCompanyProfile.CreateLocal("Custom Works", timestamp.AddDays(-1));
        sourceProfile.PaymentPolicy = CreateCustomLaborPolicy(timestamp);
        var package = service.CreateExportPackage(sourceProfile, [], timestamp);

        var json = JsonSerializer.Serialize(package, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTripped = JsonSerializer.Deserialize<TradeCompanyProfilePackage>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.Profile.PaymentPolicy.LaborStandard);
        Assert.Equal(TradeLaborBenchmarkMode.Custom, roundTripped.Profile.PaymentPolicy.LaborStandard.BenchmarkMode);
        Assert.True(roundTripped.Profile.PaymentPolicy.LaborStandard.IsCustomBenchmark);
        Assert.Equal("Custom policy", roundTripped.Profile.PaymentPolicy.LaborStandard.CalibrationEvidence);
    }

    [Fact]
    public void ImportAsNewProfile_LegacyPackageWithoutBenchmarkModeImportsAsManagedCobaltRivets()
    {
        var service = new TradeCompanyProfilePackageService();
        var importedAt = new DateTime(2026, 6, 25, 20, 0, 0, DateTimeKind.Utc);
        const string json = """
        {
          "formatVersion": 1,
          "packageKind": "ffxiv-craft-architect.trade-company-profile",
          "exportedAtUtc": "2026-06-25T19:30:00Z",
          "profile": {
            "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "schemaVersion": 1,
            "name": "Legacy Works",
            "syncState": 0,
            "paymentPolicy": {
              "activeContract": 1,
              "legacyCommissionPercent": 20,
              "laborStandard": {
                "name": "Cobalt Rivets benchmark",
                "benchmarkItemId": 5094,
                "benchmarkItemName": "Cobalt Rivets",
                "benchmarkQuantity": 999,
                "benchmarkRequiresHq": true,
                "benchmarkLaborPayout": 120000,
                "benchmarkSynthCount": 200,
                "effectiveFromUtc": "2026-06-25T18:00:00Z"
              }
            },
            "createdAtUtc": "2026-06-24T18:00:00Z",
            "updatedAtUtc": "2026-06-24T18:00:00Z"
          },
          "crafters": []
        }
        """;
        var package = JsonSerializer.Deserialize<TradeCompanyProfilePackage>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true })!;

        var imported = service.ImportAsNewProfile(package, importedAt);

        Assert.NotNull(imported.Profile.PaymentPolicy.LaborStandard);
        Assert.Equal(TradeLaborBenchmarkMode.CobaltRivets, imported.Profile.PaymentPolicy.LaborStandard.BenchmarkMode);
        Assert.True(imported.Profile.PaymentPolicy.LaborStandard.IsManagedCobaltRivets);
        Assert.False(imported.Profile.PaymentPolicy.LaborStandard.BenchmarkRequiresHq);
    }

    private static TradePaymentPolicy CreateLaborPolicy(DateTime effectiveFromUtc)
    {
        return new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            20m,
            new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5094,
                "Cobalt Rivets",
                999,
                false,
                120_000m,
                200,
                effectiveFromUtc));
    }

    private static TradePaymentPolicy CreateCustomLaborPolicy(DateTime effectiveFromUtc)
    {
        return new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            20m,
            new TradeLaborStandard(
                "Custom benchmark",
                42,
                "Custom Item",
                12,
                false,
                60_000m,
                10,
                effectiveFromUtc,
                TradeLaborBenchmarkMode.Custom,
                effectiveFromUtc,
                "Custom policy"));
    }

    private static TradeCrafterProfile CreateCrafter(
        Guid companyProfileId,
        string name,
        string world,
        string dataCenter)
    {
        var createdAt = new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);
        return new TradeCrafterProfile
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyProfileId,
            DisplayName = name,
            WorldName = world,
            DataCenter = dataCenter,
            LodestoneCharacterId = "123456",
            LodestoneProfileUrl = "https://na.finalfantasyxiv.com/lodestone/character/123456/",
            JobLevels =
            [
                new TradeCraftingJobLevel(TradeCraftingJob.Blacksmith, 100),
                new TradeCraftingJobLevel(TradeCraftingJob.Armorer, 97)
            ],
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            SyncState = TradeSyncState.LocalOnly
        };
    }
}
