namespace Belot.WebApi.Tests;

using Microsoft.Extensions.DependencyInjection;

public sealed class TeamMatchSimulationFixture : IDisposable
{
    public TeamMatchSimulationFixture()
    {
        this.Factory = new ApiFactory(teamMatchDeckSeed: 10_620);
        this.Client = this.Factory.CreateClient();
    }

    public ApiFactory Factory { get; }

    public HttpClient Client { get; }

    public void Dispose()
    {
        this.Client.Dispose();
        this.Factory.Dispose();
    }

    internal TeamMatchCoreCoordinator? GetCoordinator(string matchId) =>
        this.Factory.Services.GetRequiredService<TeamMatchLobbyService>()
            .GetCoordinatorForAudit(matchId);
}
