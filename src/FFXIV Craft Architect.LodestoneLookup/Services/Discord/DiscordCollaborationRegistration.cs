using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class DiscordCollaborationRegistration
{
    public static IServiceCollection AddDiscordCollaboration(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DiscordRequestVerifier>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<SqliteDiscordCollaborationStore>();
        services.TryAddSingleton<IDiscordVolunteerInteractionService>(
            static provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>());
        services.TryAddSingleton<IDiscordOutboxLeaseStore>(
            static provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>());
        services.TryAddSingleton<IDiscordInstallationRegistry>(
            static provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>());
        services.TryAddSingleton<IDiscordInstallationBindingWriter>(
            static provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>());
        services.TryAddScoped<DiscordPublicationService>();
        services.TryAddScoped<DiscordClaimService>();
        services.TryAddScoped<DiscordProjectionService>();
        services.TryAddSingleton<IDiscordPublicationRevocationSink, DiscordPublicationRevocationSink>();
        services.AddHttpClient<IDiscordApiClient, DiscordApiClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<DiscordCommissionOptions>();
            var apiBaseUrl = options.ApiBaseUrl.EndsWith("/", StringComparison.Ordinal)
                ? options.ApiBaseUrl
                : options.ApiBaseUrl + "/";
            client.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, DiscordOutboxDispatcher>());
        return services;
    }
}
