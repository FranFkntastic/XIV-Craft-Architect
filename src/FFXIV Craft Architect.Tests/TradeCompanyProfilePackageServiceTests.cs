using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using System.Text.Json;

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
        Assert.Single(roundTripped.Crafters);
    }

    private static TradePaymentPolicy CreateLaborPolicy(DateTime effectiveFromUtc)
    {
        return new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            20m,
            new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5099,
                "Cobalt Rivets",
                999,
                true,
                120_000m,
                200,
                effectiveFromUtc));
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
