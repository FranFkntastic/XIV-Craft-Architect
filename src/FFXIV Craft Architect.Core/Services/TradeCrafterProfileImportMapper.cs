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
        profile.LodestonePortraitUrl = NormalizeOptional(preview.PortraitUrl);
        profile.LodestoneFreeCompanyName = NormalizeOptional(preview.FreeCompanyName);
        profile.LodestoneRace = NormalizeOptional(preview.Race);
        profile.LodestoneClan = NormalizeOptional(preview.Clan);
        profile.LodestoneGender = NormalizeOptional(preview.Gender);
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
            Alias = profile.Alias,
            ContactHandle = profile.ContactHandle,
            DiscordHandle = profile.DiscordHandle,
            SocialProfileUrl = profile.SocialProfileUrl,
            WorldName = profile.WorldName,
            DataCenter = profile.DataCenter,
            LodestoneCharacterId = profile.LodestoneCharacterId,
            LodestoneProfileUrl = profile.LodestoneProfileUrl,
            LodestoneLastSyncedAtUtc = profile.LodestoneLastSyncedAtUtc,
            LodestoneAvatarUrl = profile.LodestoneAvatarUrl,
            LodestonePortraitUrl = profile.LodestonePortraitUrl,
            LodestoneFreeCompanyName = profile.LodestoneFreeCompanyName,
            LodestoneRace = profile.LodestoneRace,
            LodestoneClan = profile.LodestoneClan,
            LodestoneGender = profile.LodestoneGender,
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
