namespace Belot.WebApi.Tests;

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
}
