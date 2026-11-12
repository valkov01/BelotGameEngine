# A02 — Multiplayer-Ready Player and Seat Architecture

Status: approved architecture baseline. A03 implements the authenticated identity and in-memory
seat-ownership foundation; persistence, lobby lifecycle, and gameplay remain later work.

## Goals and boundaries

- Keep the four engine seats fixed as `South`, `East`, `North`, `West`, in counterclockwise play order.
- Keep the existing solo API contract and all five bot profile IDs (`random`, `beginner`,
  `skilled`, `expert`, `master`) unchanged.
- Support multiplayer tables with two, three, or four authenticated human seats. Every unoccupied
  seat is filled by a configured bot before the first deal.
- Assign the host to absolute South. Joining players choose an available absolute seat; partners
  and opponents may both be human.
- Use private invitation links/codes, not public matchmaking. The host may start with at least two
  authenticated humans once every joined participant is ready; all other seats become bots.
- A disconnect never transfers seat ownership. Abandoned games may initially be ended explicitly;
  bot takeover is not part of this foundation.
- Let each client render its authenticated player's seat at visual South, without changing the
  engine's absolute seats or team identities.
- Keep the server authoritative: clients submit an intended action; the server decides whose turn
  it is, validates and applies the action using `BelotMatch`, runs bots, and calculates results.
- Return only the authenticated human's `BelotSeatView` plus data public to all players. Never send
  another seat's hand or the full match record during play.
- Add identity, seat ownership, reconnect, and realtime interfaces without changing the production
  Master or deploying anything.

## Current architecture and constraints

The engine already has the right core abstraction. `BelotMatch` owns the deal, bidding, legal
actions, turn progression, bots' opportunities to act, and scoring. It exposes absolute
`ToMove`/`Decision`, `Validate`/`Act`, and `GetView(seat)`. `BelotSeatView` is explicitly
seat-scoped; the full record is only available after the match ends. `IPlayer` is suitable for
bot-controlled seats. Human clients do not need to be represented as `IPlayer`: they submit
actions to the server, which applies them to the same match.

The current Web API is a solo adapter around this engine:

- `POST /api/games` creates South as the human and the other three seats as independently chosen
  bots. Its defaults and response shape are existing frontend dependencies.
- `GET /api/games/{gameId}` and the action response expose a South-seat view.
- `POST /api/games/{gameId}/actions` accepts only South's action, serializes access with a
  per-game gate, validates the action, and runs bots until South is next to act.
- `GameStore` and `ActiveGame` are in-memory single-process storage. Restarting the process loses
  games; the current API has no authenticated identity or multiplayer membership.
- `Belot.WebApi` allows the Lovable preview origin, but the Lovable frontend itself is not in this
  repository. There is no Supabase SDK/auth configuration or SignalR implementation in this
  checkout.

Keep the solo routes, DTOs, default profiles, action behavior, and response semantics stable.
Implement multiplayer as an additive API and UI path. Do not change bot profile implementations or
the current production Master mapping as part of A02/A03.

## Proposed architecture

```text
Lovable client
  Supabase Auth access token
  REST commands / private snapshot  <---->  ASP.NET Core Web API
  SignalR public event stream                | JWT validation and seat authorization
                                              | Multiplayer application service
                                              | one serialized writer per match
                                              | BelotMatch + IPlayer bots
                                              | match/session repository
                                              v
                                  Supabase Postgres (membership and durable game data)
```

### Fixed engine seats, viewer-relative UI

The server stores and emits only absolute engine seat identifiers. The client positions the
authenticated seat at visual South, then rotates every other seat around the table. With
counterclockwise absolute indices `South=0`, `East=1`, `North=2`, `West=3`, the visual index is
`(absoluteIndex - viewerIndex + 4) % 4`, with visual indices 0–3 denoting bottom, right, top, and
left. Thus a viewer seated East sees East at the bottom, North on the right, West at the top, and
South on the left.

This is a presentation mapping only. Never rewrite a bid, card, team, score, or engine view to
viewer-relative seat names. Team membership and scoring remain South-North versus East-West.

### Seat ownership and bot fill

Represent every game with exactly four absolute seat records. A seat is either:

- `Human`: owned by one authenticated Supabase user ID (`sub`), or
- `Bot`: configured with one of the existing five profile IDs.

Seat assignment is server-side and transactional. The host owns South; joining users may select
any available absolute seat, including either a partner or opponent seat. Enforce at most one human
seat per user per game. Reject actions for bot seats and derive the acting seat from authenticated
membership, never from a client-supplied seat or user ID. Support two through four distinct human
players; at start, fill all remaining seats with their selected profiles. Solo remains the existing
one-human API path. The teams are always South-North and East-West.

Suggested persistence records:

- `game_sessions`: opaque game ID, lifecycle status, host user ID, creation/start/end timestamps,
  current revision, and persistence format version.
- `game_seats`: game ID, absolute seat, seat kind, nullable owner user ID, nullable bot profile ID,
  and membership status. Unique constraints cover `(game_id, seat)` and one human seat per
  `(game_id, owner_user_id)`.
- `game_events` (or equivalent append-only action journal): game ID, monotonically increasing
  revision/sequence, authenticated actor seat, accepted action, resulting public events, and
  timestamp. Do not store viewer snapshots in a client-readable table.

Use backend service credentials for database access; clients must not write seats or game events
directly. If Supabase RLS is enabled, deny direct client reads of private game/session state by
default. Public metadata can be exposed through explicit API projections instead of relying on
database rows as the UI contract.

### Engine-facing interfaces

Keep `Belot.Engine` independent of ASP.NET, Supabase, and SignalR. Add these concepts in
`Belot.WebApi` (names are proposed, not implemented):

```csharp
public interface IMultiplayerGameService
{
    Task<LobbyDto> CreateLobbyAsync(
        ClaimsPrincipal user, CreateLobbyRequest request, CancellationToken cancellationToken);

    Task<GameSnapshotDto> GetSnapshotAsync(
        ClaimsPrincipal user, string gameId, CancellationToken cancellationToken);

    Task<GameActionResponse> SubmitActionAsync(
        ClaimsPrincipal user, string gameId, SubmitGameActionRequest request,
        CancellationToken cancellationToken);
}

public interface IGameSessionRepository
{
    Task<GameSession?> FindAsync(string gameId, CancellationToken cancellationToken);
    Task SaveAsync(GameSession session, CancellationToken cancellationToken);
}

public interface IGameUpdatePublisher
{
    Task PublishPublicEventsAsync(
        string gameId, IReadOnlyList<PublicGameEventDto> events,
        CancellationToken cancellationToken);
}
```

The service resolves the caller's seat from the verified `sub`, checks lifecycle and revision,
then executes a single serialized command:

1. Reject non-members, finished games, stale revisions, or a request that is not the caller's turn.
2. Build the engine `BelotAction` from the allow-listed request fields.
3. Call `BelotMatch.Validate(seat, action)` and only then `Act(seat, action)`.
4. Continue executing the existing bot policy while the next seat is a bot; never accept a
   client command on behalf of a bot.
5. Append the accepted action and resulting events atomically with the new revision.
6. Publish only public events; the caller obtains a fresh private snapshot from the authorized
   snapshot endpoint.

Include a monotonic revision and an idempotency key in multiplayer commands. A duplicate retry
returns the original result; a stale revision returns HTTP 409 with the current revision. Do not
rely on a process-local `SemaphoreSlim` for correctness once more than one API instance can own a
match. Use a single-writer lease or transactional compare-and-swap on the durable revision.

### Supabase authentication

Reuse Supabase Auth as the identity provider; do not copy credentials or implement a second
password system. The Lovable client signs in with Supabase and sends its short-lived access token
as `Authorization: Bearer ...` to the API. Configure ASP.NET Core JWT bearer validation against
the project's issuer and signing keys/JWKS, with expected issuer and audience. Read the user ID
from the validated `sub` claim. The API must reject missing, expired, wrong-project, or invalid
tokens on multiplayer routes.

Do not treat CORS, a game ID, an invitation code, or a user ID in JSON as authentication. Keep
the current solo endpoints unauthenticated if that is their existing deployed contract; apply
authorization to the new multiplayer routes only. Keep Supabase service-role credentials on the
server. For SignalR browser connections, pass the access token using the supported access-token
factory and constrain query-token extraction to the hub path; ensure access tokens are redacted
from proxy and application logs.

### SignalR recommendation

SignalR is appropriate for pushing table updates to connected browsers, but it is a transport,
not the game authority or persistence layer. Use it for authenticated subscription and public
event delivery. Initially keep commands and authoritative state retrieval on REST:

- `POST /api/multiplayer/games/{gameId}/actions` submits the action with expected revision and
  idempotency key.
- `GET /api/multiplayer/games/{gameId}` returns a fresh snapshot scoped to the caller's seat.
- `/hubs/games` authorizes `JoinGame(gameId)` against seat membership and streams only sequenced
  public events for that game. The event includes the new revision and whose turn/decision it is,
  but no hand, playable-card list, private declarations, or other seat-private data.
- After a public event indicates the caller must act (or after reconnect), the client fetches its
  private snapshot over REST. This avoids broadcasting a seat-specific snapshot to a whole room.

Use a game group only after server-side membership validation. Never publish four seat views to
one group and depend on the browser to hide three. On disconnect, retain seat ownership; reconnect
is re-authentication plus re-authorization, then snapshot fetch and replay from the last seen event
sequence (or snapshot when history is unavailable). For multiple API instances, add a shared SignalR
backplane/managed service and shared repository before horizontal scaling. Supabase Realtime is
not recommended for game commands or private-hand delivery; it would duplicate authorization and
turn authority and create a second realtime contract.

### Reconnect and durability

Socket loss must not relinquish or transfer a seat. A reconnecting user proves the same Supabase
identity, re-fetches their seat-scoped snapshot, and resumes from the latest revision. No
connection ID is a seat credential.

The current in-memory `BelotMatch` cannot be resumed from a serialized snapshot, and the current
`GameStore` loses all state on restart. Therefore distinguish two milestones:

1. **Connection reconnect:** supported while the authoritative process is alive by resolving the
   durable seat owner and fetching a current snapshot.
2. **Process restart / multi-instance recovery:** not safe until the engine exposes a supported,
   versioned checkpoint/restore mechanism or a deterministic replay contract that records enough
   information to reconstruct the exact deal order and every accepted action. Do not claim restart
   safety from storing only `BelotMatchRecord` (available only after finish) or an RNG seed.

Before public multiplayer launch, persist match checkpoints/action journal and verify recovery
against recorded action histories, or explicitly scope launch to a single process with
interrupt-on-restart behavior. A database event journal must not be used to replay untrusted client
commands without re-running all engine validation.

## Proposed multiplayer API contract

These are additive routes; exact naming can be finalized before implementation.

| Route | Authorization | Purpose |
|---|---|---|
| `POST /api/multiplayer/lobbies` | Supabase user | Create a lobby with a human-seat target (2–4), optional chosen absolute seat, and bot profiles for seats that will be vacant. |
| `POST /api/multiplayer/lobbies/{lobbyId}/join` | Supabase user | Claim an available human seat; seat is selected/confirmed server-side. |
| `GET /api/multiplayer/lobbies/{lobbyId}` | Lobby member | Read public lobby/seat occupancy and readiness. |
| `POST /api/multiplayer/lobbies/{lobbyId}/start` | Host | Atomically fill vacancies with configured bots and start the match once valid. |
| `GET /api/multiplayer/games/{gameId}` | Game member | Return public state plus only the caller's hand/options. |
| `POST /api/multiplayer/games/{gameId}/actions` | Current human seat | Submit bid, declaration, or card; seat is inferred from identity. |
| `POST /api/multiplayer/games/{gameId}/leave` | Game member | Voluntarily leave according to the agreed post-start policy; do not silently free a seat. |
| `GET /hubs/games` | Authenticated connection and authorized member | SignalR public-event subscription; no game actions or private hands over the shared group. |

Use a multiplayer-specific request/response DTO and route namespace. Do not add seat ownership,
auth, or revision requirements to the existing `/api/games` request/response shape. Reuse engine
view/action mapping only behind server-side projections and add tests that keep the old solo JSON
contract stable.

## Exact change inventory

### Backend (`src/Belot.WebApi`)

1. **A03:** Add configurable Supabase JWT bearer validation and protect only multiplayer routes.
   Define identity, room membership, and absolute seat-ownership models and interfaces; enforce
   server-authoritative unique membership/seat assignment. A03's in-memory repository is
   single-process and is not durable.
2. Add lobby/session/seat persistence and SQL uniqueness constraints for membership, lifecycle,
   revision, and event sequence.
3. Add a multiplayer coordinator that owns one `BelotMatch`, maps each absolute seat to either an
   authenticated human owner or an existing bot `IPlayer`, calls `GetView(seat)` only for the
   requesting owner, and serializes action processing.
4. Add the multiplayer routes and dedicated DTOs for create/join/start/snapshot/action/leave.
   Derive the actor from claims; include revision/idempotency handling and structured conflict
   responses.
5. Add a public-event projection and SignalR hub. Authorize room membership server-side and
   broadcast only non-private, sequenced events. Keep per-seat state on the REST snapshot path.
6. Add repository-backed restart/recovery support before claiming durable multiplayer. This
   requires an explicit engine checkpoint/restore or deterministic replay design; it is not
   implemented by the current in-memory API.
7. Leave `/api/games`, `BotProfiles`, `BelotMatch` gameplay rules, and all production bot/master
   source untouched unless a separately approved engine interface change proves necessary.

### Frontend (Lovable app; source not present in this checkout)

1. Preserve the existing solo screen/API client. Add a separate multiplayer entry and lobby flow;
   do not convert the solo route to require sign-in.
2. Reuse the existing Supabase Auth session. Add an API client that attaches the current access
   token to multiplayer REST calls and refreshes/retries once after token renewal.
3. Add lobby occupancy/readiness UI for two to four authenticated people and display the selected
   bot profiles for remaining seats. Do not accept client authority over seat ownership or game
   state.
4. Store the assigned absolute seat from the authorized server response. Add one tested
   viewer-relative seat-position helper for rendering every player at the correct visual position;
   continue sending absolute engine names in actions and rendering labels.
5. Add a multiplayer table data adapter for the private snapshot plus public events. Render only
   the caller's hand and legal options from the snapshot; render public bids, exposed cards, tricks,
   turn, and score from the public projection.
6. Add SignalR connect/subscribe/reconnect handling: obtain the Supabase token, join the authorized
   game, track event sequence, fetch a fresh private snapshot after reconnect or sequence gaps, and
   show an explicit disconnected/reconnecting state. Never cache or display another player's hand.
7. Add graceful leave/rejoin behavior and preserve the existing solo API/table paths and
   regression coverage.

Because this repository contains only the API and MAUI client, actual Lovable component/file names
and current frontend auth conventions must be confirmed from the Lovable source before that
frontend work begins.

## Ordered implementation plan and gates

1. **Freeze compatibility contracts.** Capture current solo OpenAPI/JSON examples and add API
   regression tests for create, fetch, action, all five bot profiles, and public event ordering.
   No behavior changes.
2. **Define identity boundary.** Configure Supabase project settings outside source control; add
   JWT validation and tests for valid, expired, wrong-issuer, and unauthenticated requests. Keep
   solo route tests proving current behavior.
3. **Add lobby persistence and authorization.** Add migration/repository and atomic seat claim,
   host start, duplicate-user prevention, and invitation/lobby access tests. Do not put match hands
   in browser-readable Supabase tables.
4. **Add server-authoritative game coordinator.** Use fixed engine seats and per-seat views; add
   two-, three-, and four-human integration tests, bot-fill tests, wrong-seat/illegal/stale/duplicate
   action tests, and hidden-hand leakage tests. The solo endpoints remain on their current path.
5. **Choose and implement durability before scaling.** Design engine checkpoint/restore or exact
   replay, persist accepted actions and revisions, test process recovery, then enable multiple
   writers only with a distributed single-writer mechanism.
6. **Add SignalR public updates.** Add authenticated membership checks, sequence numbering,
   disconnect/reconnect, stale-event recovery, and assertions that hub payloads never contain
   private hand/options/full records. Keep REST as command and snapshot authority.
7. **Integrate Lovable UI.** Implement authenticated multiplayer lobby/table adapters and relative
   seat rotation in the separate multiplayer path; run the existing solo UI regression suite and
   browser checks for every viewer seat.
8. **Operational readiness.** Configure production secrets, database migration process, rate
   limits per authenticated user, connection limits, observability/redaction, backups, and
   restart/recovery alerts. Deploy only after explicit approval and a staging soak.

## Approved product decisions and A04 boundary

- The host is assigned absolute South. Joining authenticated users choose an available absolute
  seat, and two humans may be partners or opponents. Teams stay South-North and East-West.
- Two, three, or four human players are supported. The host may start when at least two
  authenticated humans are present and all joined participants are ready; every unoccupied seat
  becomes a bot at start.
- Rooms are private and invitation-only; public matchmaking is out of scope. A disconnect never
  transfers ownership. Initial abandoned games may be ended explicitly without bot takeover.
- Full match recovery after process restart is a pre-launch requirement, not part of A03.
- A03 provides configurable JWT validation and an in-memory room/seat-ownership contract only.
  Production use still requires Supabase project configuration and durable persistence.
- **A04 implementation:** `src/Belot.WebApi/MultiplayerRooms.cs` now adds lobby configuration,
  one-time invitation codes, member readiness and host-only start. `POST
  /api/multiplayer/lobbies` returns the invitation secret once; only its SHA-256 hash is stored.
  Joining requires both that secret and an available absolute seat. A host starts with at least two
  human members and all joined members ready; the operation assigns configured bots to every
  remaining seat. Lobby reads are member-only and do not return user IDs or invitation secrets.
  The former `/rooms` create/read/join paths remain aliases, and joining through the alias also
  requires the invitation secret.
- `src/Belot.WebApi/PostgresMultiplayerRoomRepository.cs` implements room and seat persistence
  with row-locked transactions for joining, readiness and start. The
  `src/Belot.WebApi/Migrations/001_multiplayer_lobbies.sql` migration creates the room and seat
  tables, unique room/seat and room/owner constraints, a unique invitation hash, server-only RLS
  access and bot-profile constraints. This stores lobby membership and bot assignments only; it
  does not persist `BelotMatch` state or make a running game restart-safe.
- Configure either `ConnectionStrings:Multiplayer` or `Multiplayer:PostgresConnectionString` to
  select Postgres. Apply the checked-in migration manually to the Supabase database before
  enabling that setting; migrations are not applied by application startup. Without a connection
  string, the API logs a warning and uses the process-local implementation, which loses rooms on
  restart and is unsuitable for production. For production, use a backend-only database
  credential and TLS (`SSL Mode=Require`); review and apply the SQL once through the approved
  Supabase migration process before configuring the API. No migration or deployment was run for
  A04.
- Supabase JWT validation allows `RS256` and `ES256` through the configured HTTPS OIDC Authority
  and its published signing keys. `HS256` is allowed only when the server also receives
  `Authentication:Supabase:JwtSecret` (the legacy/shared-secret value) with at least 32 UTF-8
  bytes. Tests validate local RSA, ECDSA and configured HMAC tokens, but do not use a production
  Supabase project. The project URL, Auth issuer/audience and signing-key algorithm were not
  provided, so compatibility with that particular project's live key set remains an integration
  check. Confirm the algorithm shown under the project's Supabase Auth JWT Signing Keys and set
  `Authentication:Supabase:Authority` to its HTTPS Auth issuer, `Authentication:Supabase:Issuer`
  to the exact token issuer if different, and `Authentication:Supabase:Audience` to its access
  token audience (normally `authenticated`). Do not configure a shared secret for asymmetric
  keys or vice versa.
- A04 tests cover invitation secrecy and rejection, host-South assignment, member-only reconnect
  reads, readiness reversal, host-only start, maximum capacity, concurrent claims, bot fill, and
  two/three/four human seats. Gameplay actions, SignalR, the Lovable UI, process-restart recovery,
  migrations against a remote database and deployment remain out of scope.

## A03 implementation record

- `src/Belot.WebApi/Program.cs` configures JWT bearer validation and a `sub`-claim authorization
  policy; only `/api/multiplayer/**` uses that policy. Existing `/api/games/**` and solo bot routes
  remain unchanged and unauthenticated.
- `src/Belot.WebApi/MultiplayerRooms.cs` defines room, membership, seat, and fixed-team models;
  repository, membership, and seat-assignment interfaces; authenticated identity resolution; and
  an atomic, single-process in-memory implementation. The host owns South, and clients cannot set
  owner IDs or team values.
- `src/Belot.WebApi/MultiplayerRoomEndpoints.cs` adds authorized create, join-by-absolute-seat,
  and member-only room-read endpoints. Responses expose seat occupancy and fixed teams, not user
  IDs.
- `src/Tests/Belot.WebApi.Tests/ApiFactory.cs` creates isolated RSA-signed test JWTs. The tests in
  `src/Tests/Belot.WebApi.Tests/ApiIntegrationTests.cs` cover missing/malformed, expired, wrong
  issuer, and valid tokens; unauthenticated solo use; host-South assignment; fixed seats/teams;
  duplicate users, occupied/reserved seats, forged request user IDs, and member-only reads. The
  existing full-match solo API test remains part of the same suite.
- Runtime configuration is external to source: `Authentication:Supabase:Authority` (the project's
  HTTPS Auth issuer, used for OIDC metadata and signing keys/JWKS), optional
  `Authentication:Supabase:Issuer` (defaults to Authority), and
  `Authentication:Supabase:Audience` (defaults to `authenticated`). No project URL, key, service
  role credential, or production token is committed. Integration tests use only a generated local
  RSA key. The frontend repository URL supplied for A03 returned GitHub 404 to anonymous access,
  so the authentication implementation uses standard Supabase bearer-token validation without
  reading or modifying that repository.
- A03 does not include Postgres migrations/persistence, invitation lifecycle, readiness/start,
  bot fill, match sessions/actions, SignalR, or process-restart recovery. The in-memory room store
  is explicitly single-process and is not a production persistence boundary.

This A03 change does not include or authorize deployment.
