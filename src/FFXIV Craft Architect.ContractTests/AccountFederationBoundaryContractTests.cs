using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class AccountFederationBoundaryContractTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("canonical")]
    [InlineData("staging")]
    public void DisabledBoundaryAcceptsOnlyNamedServiceEnvironments(string serviceEnvironment)
    {
        var options = new AccountFederationBoundaryOptions
        {
            ServiceEnvironment = serviceEnvironment,
            Mode = "disabled"
        };

        options.Validate();
    }

    [Fact]
    public void UnknownServiceEnvironmentRefusesStartupWhileFederationIsDisabled()
    {
        var options = new AccountFederationBoundaryOptions
        {
            ServiceEnvironment = "development",
            Mode = "disabled"
        };

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("local, canonical, or staging", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumerModeRefusesCanonicalSelfConsumptionBeforeRuntimeActivation()
    {
        var options = CompleteConsumer("canonical");

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("cannot consume its own", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumerModeRefusesInsecureCanonicalAuthorityBeforeRuntimeActivation()
    {
        var options = CompleteConsumer(
            "staging",
            canonicalAccountAuthority: "http://accounts.example.test/");

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("absolute HTTPS URI", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteReviewedConfigurationRemainsLockedUntilRuntimeExists()
    {
        var options = CompleteConsumer("staging");

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("runtime is not implemented", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthReportsEnvironmentAndDisabledModeWithoutCreatingFederationRoute()
    {
        using var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<AccountFederationBoundaryOptions>();
                services.AddSingleton(new AccountFederationBoundaryOptions
                {
                    ServiceEnvironment = "staging",
                    Mode = "disabled"
                });
            }));
        using var client = application.CreateClient();

        var health = await client.GetFromJsonAsync<ProfileHostHealthResponse>(
            "/profile-host/health");
        using var exchange = await client.PostAsync(
            "/identity/v1/federation/exchange",
            content: null);

        Assert.Equal("staging", health!.ServiceEnvironment);
        Assert.Equal("disabled", health.AccountFederationMode);
        Assert.Equal(HttpStatusCode.NotFound, exchange.StatusCode);
    }

    [Fact]
    public void DeploymentWritesAndVerifiesTargetDerivedDisabledBoundary()
    {
        var workflow = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(),
            ".github",
            "workflows",
            "deploy-vps-lodestone.yml"));

        Assert.Contains(
            "printf 'AuthorityBoundary__Environment=%s\\n' \"$target\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "printf 'AuthorityBoundary__AccountFederationMode=disabled\\n'",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "grep -Fxq \"AuthorityBoundary__Environment=$target\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "health.get(\"serviceEnvironment\") != os.environ[\"EXPECTED_ENVIRONMENT\"]",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "health.get(\"accountFederationMode\") != \"disabled\"",
            workflow,
            StringComparison.Ordinal);
    }

    private static AccountFederationBoundaryOptions CompleteConsumer(
        string serviceEnvironment,
        string canonicalAccountAuthority = "https://accounts.xivcraftarchitect.com/") =>
        new()
        {
            ServiceEnvironment = serviceEnvironment,
            Mode = "consumer",
            CanonicalAccountAuthority = canonicalAccountAuthority,
            TokenIssuer = "https://accounts.xivcraftarchitect.com/",
            TokenAudience = "trade-staging",
            ActivationPermit = AccountFederationBoundaryOptions.ReviewedActivationPermit
        };

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
