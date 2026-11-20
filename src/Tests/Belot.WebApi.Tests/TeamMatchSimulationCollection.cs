namespace Belot.WebApi.Tests;

using Xunit;

[CollectionDefinition(Name)]
public sealed class TeamMatchSimulationCollection : ICollectionFixture<TeamMatchSimulationFixture>
{
    public const string Name = "Team Match HTTP simulation";
}
