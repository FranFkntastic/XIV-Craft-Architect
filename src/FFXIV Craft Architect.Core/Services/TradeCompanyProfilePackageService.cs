using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class TradeCompanyProfilePackageService
{
    public TradeCompanyProfilePackage CreateExportPackage(
        TradeCompanyProfile profile,
        IReadOnlyList<TradeCrafterProfile> crafters,
        DateTime exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(crafters);

        return new TradeCompanyProfilePackage
        {
            FormatVersion = TradeCompanyProfilePackage.CurrentFormatVersion,
            PackageKind = TradeCompanyProfilePackage.PackageKindValue,
            ExportedAtUtc = exportedAtUtc,
            Profile = CopyProfile(profile),
            Crafters = crafters
                .Where(crafter => crafter.CompanyProfileId == profile.Id)
                .Select(CopyCrafter)
                .ToArray()
        };
    }

    public TradeCompanyProfileImportResult ImportAsNewProfile(
        TradeCompanyProfilePackage package,
        DateTime importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackage(package);

        var sourceProfile = package.Profile;
        var importedProfile = TradeCompanyProfile.CreateLocal(sourceProfile.Name, importedAtUtc);
        importedProfile.Description = sourceProfile.Description;
        importedProfile.PaymentPolicy = NormalizePaymentPolicy(sourceProfile.PaymentPolicy);

        var importedCrafters = package.Crafters
            .Select(crafter => ImportCrafter(crafter, importedProfile.Id, importedAtUtc))
            .ToArray();

        return new TradeCompanyProfileImportResult(importedProfile, importedCrafters);
    }

    private static void ValidatePackage(TradeCompanyProfilePackage package)
    {
        if (!string.Equals(package.PackageKind, TradeCompanyProfilePackage.PackageKindValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected file is not a Trade company profile package.");
        }

        if (package.FormatVersion != TradeCompanyProfilePackage.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Trade company profile package version {package.FormatVersion}. Expected {TradeCompanyProfilePackage.CurrentFormatVersion}.");
        }

        if (package.Profile == null)
        {
            throw new InvalidOperationException("The Trade company profile package does not contain a company profile.");
        }

        if (string.IsNullOrWhiteSpace(package.Profile.Name))
        {
            throw new InvalidOperationException("The Trade company profile package has an empty company name.");
        }
    }

    private static TradeCompanyProfile CopyProfile(TradeCompanyProfile profile)
    {
        return new TradeCompanyProfile
        {
            Id = profile.Id,
            SchemaVersion = profile.SchemaVersion,
            Name = profile.Name,
            Description = profile.Description,
            RemoteId = profile.RemoteId,
            SyncState = profile.SyncState,
            PaymentPolicy = NormalizePaymentPolicy(profile.PaymentPolicy),
            CreatedAtUtc = profile.CreatedAtUtc,
            UpdatedAtUtc = profile.UpdatedAtUtc
        };
    }

    private static TradePaymentPolicy NormalizePaymentPolicy(TradePaymentPolicy? policy)
    {
        return TradeLaborStandardCalibrationService.NormalizeManagedCobaltRivetsBenchmark(
            policy ?? TradePaymentPolicy.LegacyDefault);
    }

    private static TradeCrafterProfile CopyCrafter(TradeCrafterProfile crafter)
    {
        return new TradeCrafterProfile
        {
            Id = crafter.Id,
            CompanyProfileId = crafter.CompanyProfileId,
            DisplayName = crafter.DisplayName,
            Alias = crafter.Alias,
            ContactHandle = crafter.ContactHandle,
            DiscordHandle = crafter.DiscordHandle,
            SocialProfileUrl = crafter.SocialProfileUrl,
            WorldName = crafter.WorldName,
            DataCenter = crafter.DataCenter,
            LodestoneCharacterId = crafter.LodestoneCharacterId,
            LodestoneProfileUrl = crafter.LodestoneProfileUrl,
            LodestoneLastSyncedAtUtc = crafter.LodestoneLastSyncedAtUtc,
            LodestoneAvatarUrl = crafter.LodestoneAvatarUrl,
            LodestonePortraitUrl = crafter.LodestonePortraitUrl,
            LodestoneFreeCompanyName = crafter.LodestoneFreeCompanyName,
            LodestoneRace = crafter.LodestoneRace,
            LodestoneClan = crafter.LodestoneClan,
            LodestoneGender = crafter.LodestoneGender,
            AvailabilityNotes = crafter.AvailabilityNotes,
            PaymentNotes = crafter.PaymentNotes,
            OperatorNotes = crafter.OperatorNotes,
            JobLevels = crafter.JobLevels.ToArray(),
            RemoteId = crafter.RemoteId,
            SyncState = crafter.SyncState,
            CreatedAtUtc = crafter.CreatedAtUtc,
            UpdatedAtUtc = crafter.UpdatedAtUtc
        };
    }

    private static TradeCrafterProfile ImportCrafter(TradeCrafterProfile source, Guid companyProfileId, DateTime importedAtUtc)
    {
        var crafter = CopyCrafter(source);
        crafter.Id = Guid.NewGuid();
        crafter.CompanyProfileId = companyProfileId;
        crafter.RemoteId = null;
        crafter.SyncState = TradeSyncState.LocalOnly;
        crafter.CreatedAtUtc = importedAtUtc;
        crafter.UpdatedAtUtc = importedAtUtc;
        return crafter;
    }
}
