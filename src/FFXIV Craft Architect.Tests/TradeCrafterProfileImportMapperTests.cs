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
        Assert.Equal(preview.PortraitUrl, profile.LodestonePortraitUrl);
        Assert.Equal(preview.FreeCompanyName, profile.LodestoneFreeCompanyName);
        Assert.Equal(preview.Race, profile.LodestoneRace);
        Assert.Equal(preview.Clan, profile.LodestoneClan);
        Assert.Equal(preview.Gender, profile.LodestoneGender);
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
            Alias = "LC",
            ContactHandle = "discord-user",
            DiscordHandle = "levelchecker",
            SocialProfileUrl = "https://example.com/levelchecker",
            PaymentNotes = "manual payment notes",
            OperatorNotes = "manual operator notes",
            AvailabilityNotes = "manual availability",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var mapper = new TradeCrafterProfileImportMapper();

        var updated = mapper.UpdateProfile(existing, CreatePreview(retrievedAt));

        Assert.Equal(existing.Id, updated.Id);
        Assert.Equal(existing.CompanyProfileId, updated.CompanyProfileId);
        Assert.Equal("LC", updated.Alias);
        Assert.Equal("discord-user", updated.ContactHandle);
        Assert.Equal("levelchecker", updated.DiscordHandle);
        Assert.Equal("https://example.com/levelchecker", updated.SocialProfileUrl);
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
            "https://img2.finalfantasyxiv.com/portrait.jpg",
            "Terms of Service",
            "Viera",
            "Veena",
            "Female",
            retrievedAt,
            [
                new TradeCraftingJobLevel(TradeCraftingJob.Carpenter, 100),
                new TradeCraftingJobLevel(TradeCraftingJob.Blacksmith, 100)
            ]);
    }
}
