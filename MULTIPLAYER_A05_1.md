# A05.1 - Multiplayer gameplay corrections

## In-memory limits and cleanup

`MultiplayerGameStore` admits at most 200 games by default. Configure
`Multiplayer:Games:MaximumActive` to change the limit (bounded to 1-10,000).
When the store is full, a lobby start returns HTTP 503; existing games are never
evicted to make room.

Completed matches remain available for 60 minutes by default, configurable with
`Multiplayer:Games:CompletedRetentionMinutes` (1 minute to 7 days). Expired
completed matches are removed opportunistically on store access or before a
new match is admitted. They are terminal, so expiration cannot interrupt play.
Started but unfinished matches are not removed based on age or disconnect: a
disconnected player may reconnect, and this version has no durable signal that
distinguishes an abandoned match from a temporarily inactive one. An explicit,
persisted abandon lifecycle is needed before unfinished matches can be safely
collected. A process restart still loses all in-memory matches; PostgreSQL only
persists lobby lifecycle and membership, not a match checkpoint or action log.

## Public event ordering for A06

The initial game revision is `0`. Each accepted human action advances the
revision exactly once, before automatic bot actions are attempted. Its public
events—including any bot actions—are emitted in their existing `sequence`
order, starting at `0` for that revision. If bot execution fails, the accepted
action and new revision remain committed; retrying the identical request with
the same idempotency key resumes only bot actions and does not apply the human
action again.

For A06, identify an event by `(gameId, revision, sequence)`. Within one game,
order events lexicographically by `(revision, sequence)`: revision orders
accepted human actions, and sequence orders all public events produced by that
action. The tuple is unique because sequence numbers restart only when revision
advances. A client cursor should retain both values, deduplicate on the full
tuple, and fetch a seat-scoped REST snapshot after reconnect or a detected gap.
This is a contract for the future event stream; A05.1 does not add SignalR or
change the current event DTO.

## PostgreSQL integration checks

For local-only API integration tests, set `BELOT_TEST_POSTGRES` to a PostgreSQL
connection string. Apply migrations `001_multiplayer_lobbies.sql` and
`002_multiplayer_starting.sql` to that local database before running
`MultiplayerGameIntegrationTests`. The test class then exercises start,
authenticated seat snapshots, gameplay, idempotency and bot-failure recovery
through the PostgreSQL-backed repository. No migration is applied by the app
or test fixture.
