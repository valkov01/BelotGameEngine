# A10.6 authenticated Team Match automation

The reusable runner in `TeamMatchHttpSimulator.cs` creates real authenticated
Team Matches through the Web API, joins distinct JWT identities, assigns bots,
starts the match, and submits legal human actions until both C# table engines
complete. It does not call a coordinator, scoring service, or fabricated result
when advancing a match.

## Executed supported matrix

Every checked cell is one completed full match for the shown number of scheduled
deals. Hanging points can extend the actual deal count.

| Humans / bots | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2 / 6 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 3 / 5 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 4 / 4 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 5 / 3 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 6 / 2 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 7 / 1 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |
| 8 / 0 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |

Recorded run on 2026-11-20:

- 119 / 119 supported configuration/deal-count matches passed.
-   The 17 one-human/seven-bot cases for deal counts 4-20 remain cataloged for A10.7.
  They are activated on the A10.7 feature branch as complete autonomous HTTP matches,
  bringing the supported total to 136 scenarios.
- 142 / 142 simulator tests passed with no skips.
- Every HTTP response was checked for an unexpected `429`.
- The 119 full matches took 5m29s of accumulated per-match time in a 5m35s run.
  Mean was 2.77 seconds and the slowest match was 11.23 seconds.
- Human commands accepted by the server included 5,604 bids, 1,054 declaration
  decisions containing 1,054 actual declarations, 37,321 cards and 794 belotes.
  Bot commands were driven by the production bot profiles and the C# engine;
  they are bounded and fail the scenario if automation stalls or submits an
  illegal action.

The data set intentionally varies both first-finished table and compass seats,
while avoiding a position/seed multiplier:

- Smoke: the seven distributions at four deals.
- Boundary: the seven distributions at twenty deals.
- Extended: the remaining fifteen deal counts for every distribution.
- Barometer ON and OFF alternate across the matrix.
- Board identity is checked from all 32 played cards for each numbered deal and
  absolute seat, together with the same dealer.

## Per-deal and Barometer oracle

The runner reads the two public engine round summaries after each table
finishes. For every deal it independently calculates:

```text
Team 1 TT = table 1 South/North + table 2 East/West
Team 2 TT = table 1 East/West  + table 2 South/North
Signed TT = Team 1 TT - Team 2 TT
```

The calculation does not call `TeamMatchComparisonTracker` or any production
comparison helper. After the first table finishes, no new row is visible. After
the second table finishes, an ON barometer must contain exactly one new row.
An OFF barometer remains empty and null-valued until true match completion.
Every old row, the independent cumulative totals, the final margin and the
actual C# table scores are checked after every paired deal.

Each xUnit result contains a readable table with deal number, both table
scores, expected signed TT, cumulative signed TT, release state, hanging points
and PASS/FAIL. The checked representative hanging fixture played five deals
from a four-deal schedule under both Barometer modes. Deal 4 left `13/13`
hanging at the two tables; deal 5 scored `0/45` at both and cleared the pot.
ON released deal 4 after both tables finished it. OFF withheld every row until
deal 5 completed the match.

Static independently reviewed fixtures cover:

- all-pass;
- changing Team 1/Team 2 lead;
- hanging-point scoring inputs;
- a scheduled-final deal followed by an extension.

## Running

```bash
# Fast normal-CI candidate: seven distributions at four deals.
dotnet test src/Tests/Belot.WebApi.Tests/Belot.WebApi.Tests.csproj \
  --filter "Category=TeamMatchSmoke"

# Full 119-case matrix plus unsupported and oracle fixtures.
dotnet test src/Tests/Belot.WebApi.Tests/Belot.WebApi.Tests.csproj \
  --filter "FullyQualifiedName~TeamMatchAutomatedSimulationTests"

# Existing Web API regressions without repeating the matrix.
dotnet test src/Tests/Belot.WebApi.Tests/Belot.WebApi.Tests.csproj \
  --filter "FullyQualifiedName!~TeamMatchAutomatedSimulationTests"

dotnet test src/Tests/Belot.Engine.Tests/Belot.Engine.Tests.csproj
```

## A10.7 autonomous activation

The A10.7 implementation lets the organizer be the only human. The four-bot table
is driven by the server-hosted bounded pump, while the simulator submits actions
only from the organizer's authenticated table seat. It uses the organizer and
results endpoints to observe the bot-only table and a test-only internal audit
handle to compare the completed hidden round records after true match completion.

The existing 17 one-human scenarios now use the normal `RunAsync` path. The matrix
workflow selects `TeamMatchSmoke`, `TeamMatchBoundary`, `TeamMatchMatrix` and
`TeamMatchAutonomous`, for 136 complete supported matches.

## CI recommendation

Keep the four-deal smoke category in normal CI. Run the complete matrix in a
dedicated scheduled or manually dispatched Team Match workflow: its measured
5m35s is too long for every unrelated pull request, while it is fast enough for
per-change A10 validation and nightly coverage.

## Deferred and unverified scope

- Autonomous processing and event/revision state remain process-local. The tests
  do not prove Render Free CPU capacity or restart recovery.
- These tests use the production authentication policies with test JWTs and
  the in-process test server. They do not prove deployed JWT, CORS, rate-limit,
  process-restart or Render configuration.
- No load, denial-of-service, distributed concurrency or real-browser
  acceptance is claimed. Frontend Barometer animation, timing and visual
  behavior require separate browser tests.
- Public event playback and private-seat isolation continue to be covered by
  the existing focused Web API tests; this runner additionally rejects
  organizer/result surfaces containing hands or playable-card data.
