namespace Belot.WebApi.Tests;

internal sealed record TeamMatchDealAudit(
    int DealNumber,
    int Table1SouthNorth,
    int Table1EastWest,
    int Table2SouthNorth,
    int Table2EastWest,
    int ExpectedSignedTt,
    int CumulativeSignedTt,
    bool ComparisonReleased,
    int Table1Hanging,
    int Table2Hanging);
