# Belot Web API

An ASP.NET Core API for one human playing South against the repository's original bots. It uses
`Belot.Engine.GameMechanics.BelotMatch` for all game state and rules, and keeps each bot's original
decision implementation. Normal gameplay responses expose only South's hand and public game data.

## Run locally

From the repository root:

```sh
ASPNETCORE_URLS=http://0.0.0.0:10000 dotnet run --no-launch-profile \
  --project src/Belot.WebApi/Belot.WebApi.csproj
```

This listens on port 10000 on all interfaces (including Codespaces port forwarding). To use the
`http` launch profile instead, run `dotnet run --project src/Belot.WebApi/Belot.WebApi.csproj`;
it listens on `http://localhost:5266`.

## Endpoints

- `GET /api/health` — returns `{"status":"ok"}`.
- `GET /api/bots` — returns profile IDs and display names.
- `POST /api/games` — starts a match. North, East and West independently accept `random`,
  `beginner`, `skilled`, `expert` or `master`; all default to `skilled`.
- `GET /api/games/{gameId}` — current South-seat view.
- `POST /api/games/{gameId}/actions` — submits South's current bid, declarations or card.
- `GET /api/games/{gameId}/debug/hands` — developer-only live hands, disabled by default and
  omitted from the public OpenAPI document. Requires `X-Belot-Debug-Key`.
- `GET /openapi/v1.json` — public OpenAPI 3.0 specification for the endpoints and JSON contract.

The profiles match the app: Random is `RandomPlayer`, Beginner `DummyPlayer`, Skilled
`SmartPlayer`, Expert `ClaudePlayerProfiles.CreateExpert()`, and Master
`ClaudePlayerProfiles.CreateHeuristic()`.

### Team Match public-event playback

Authenticated Team Match members can fetch their current private seat snapshot and replay the
public gameplay events for their table:

- `GET /api/team-matches/{matchId}/game?afterSequence={cursor}`
- `POST /api/team-matches/{matchId}/game/actions`

Each table has its own monotonically increasing integer event cursor, beginning at `-1`. A GET
without `afterSequence` remains a snapshot-only read and includes the current `lastEventSequence`
with an empty `events` array. Use `afterSequence=-1` after an initial snapshot to replay all
retained events; use the last observed `sequence` to continue. Cursors from one table must not be
used for the other table.

The action body retains `expectedTableRevision`, `commandId`, and `action`, and accepts optional
`afterSequence`. If omitted, a successful POST returns the events produced by that accepted
action and its automatic bot continuation. If supplied, it returns events after that cursor.
Retries of an accepted `commandId` remain conflicts and do not apply or publish the action again.

Both endpoints return the existing seat snapshot fields plus `lastEventSequence`,
`snapshotRequired`, and `events`. If a cursor is ahead of the journal or its history has expired,
the response sets `snapshotRequired` to `true` and returns an empty `events` array; the included
`state` is the authoritative current view for that authenticated seat. Replace the client state
and resume from `lastEventSequence`; do not attempt to animate a partial replay. A cursor less
than `-1` is invalid.

The event list contains only public bids/contracts, declarations, played cards, completed tricks,
deal results, deal starts, and match completion, ordered by `sequence`. It never contains private
hands, legal actions, account IDs, authentication tokens, or invitation secrets. The snapshot's
`state` continues to contain only the requesting member's private hand and legal actions. Example
request and abridged response:

```http
GET /api/team-matches/match-id/game?afterSequence=8
Authorization: Bearer <access-token>
```

```json
{
  "matchId": "match-id",
  "table": 2,
  "yourSeat": "South",
  "revision": 4,
  "isPaused": false,
  "automationPending": false,
  "lastEventSequence": 10,
  "snapshotRequired": false,
  "events": [
    { "sequence": 9, "type": "bid", "roundNumber": 1, "seat": "South", "bid": "Pass", "automatic": false },
    { "sequence": 10, "type": "bid", "roundNumber": 1, "seat": "East", "bid": "Pass", "automatic": true }
  ],
  "state": {
    "gameId": "match-id",
    "bots": { "East": "skilled", "North": "skilled", "West": "skilled" },
    "isFinished": false,
    "winner": null,
    "toMove": "North",
    "decision": "Bid",
    "roundNumber": 1,
    "firstToPlay": "South",
    "southNorthPoints": 0,
    "eastWestPoints": 0,
    "hangingPoints": 0,
    "contract": { "seat": "Unknown", "type": "Pass" },
    "bids": [
      { "seat": "South", "type": "Pass" },
      { "seat": "East", "type": "Pass" }
    ],
    "announces": [],
    "tricks": [],
    "currentTrick": [],
    "cardCounts": [5, 5, 5, 5],
    "hand": [
      { "suit": "Club", "type": "Ace" },
      { "suit": "Club", "type": "Seven" },
      { "suit": "Diamond", "type": "Jack" },
      { "suit": "Heart", "type": "Ten" },
      { "suit": "Spade", "type": "King" }
    ],
    "availableBids": ["Clubs", "Diamonds", "Hearts", "Spades", "NoTrumps", "AllTrumps"],
    "availableAnnounces": [],
    "playableCards": [],
    "beloteCards": [],
    "previousRounds": []
  }
}
```

For an action, include the last processed sequence when you want the response to replay from it:

```json
{
  "expectedTableRevision": 4,
  "commandId": "client-generated-retry-key",
  "afterSequence": 8,
  "action": { "bid": "Pass" }
}
```

History is bounded per table to 512 events by default. `TeamMatch:PublicEventsPerTable` may be
configured from 1 to 4096; changing retention affects only how far back clients can replay, not
the current game state.

### Example requests

```sh
curl -s http://localhost:5266/api/bots

curl -s -X POST http://localhost:5266/api/games \
  -H 'Content-Type: application/json' \
  -d '{"north":"beginner","east":"expert","west":"master"}'
```

The create response includes an unpredictable `gameId` and the initial South view. Treat the ID
as a bearer token: anyone who knows it can inspect and play that game. The initial view is in the
auction phase, after the first five cards have been dealt:

```json
{
  "gameId": "unpredictable-game-token",
  "bots": { "North": "beginner", "East": "expert", "West": "master" },
  "isFinished": false,
  "toMove": "South",
  "decision": "Bid",
  "cardCounts": [5, 5, 5, 5],
  "hand": [
    { "suit": "Club", "type": "Ace" },
    { "suit": "Club", "type": "Seven" },
    { "suit": "Diamond", "type": "Jack" },
    { "suit": "Heart", "type": "Ten" },
    { "suit": "Spade", "type": "King" }
  ],
  "availableBids": ["Clubs", "Diamonds", "Hearts", "Spades", "NoTrumps", "AllTrumps"]
}
```

The actual hand and available bids depend on the shuffled deal. Use only a bid in
`availableBids`:

```sh
curl -s -X POST http://localhost:5266/api/games/<gameId>/actions \
  -H 'Content-Type: application/json' \
  -d '{"bid":"Clubs"}'
```

For an `Announce` decision, post `{"announces":[]}` to declare none, or supply entries copied from
`availableAnnounces`, for example `{"announces":[{"type":"SequenceOf3","card":{"suit":"Heart","type":"Jack"}}]}`.
For a `PlayCard` decision, choose from `playableCards`; announce a belote when appropriate by
setting `belote` true:

```json
{"card":{"suit":"Heart","type":"Ace"},"belote":false}
```

Each successful action response contains the updated South-visible `state` and ordered `events`
for bids, declarations, played cards, completed tricks and deals. Bots act automatically until
South must decide again or the match ends. Invalid actions return HTTP 400; unknown game IDs
return 404 and actions after match completion return 409.

## Configuration and deployment

The default allowed origin is `https://preview--belote-bridge-battle.lovable.app`. In Render, set
the environment variable `Cors__AllowedOrigins__0` to that exact origin (scheme and host, with a
port only when applicable; no path or trailing slash). This is the ASP.NET Core environment-variable
form of `Cors:AllowedOrigins:0`. Additional trusted origins can be configured as
`Cors__AllowedOrigins__1`, `Cors__AllowedOrigins__2`, and so on. Only configured origins are
allowed; wildcard origins are rejected. The CORS policy permits GET, POST, DELETE and OPTIONS,
and the Authorization, Content-Type, Accept and X-Belot-Debug-Key request headers. Credentials
are enabled for these explicitly configured origins, including SignalR negotiation requests.

The public API contract is available at `https://belotgameengine.onrender.com/openapi/v1.json`.
The global IP-based rate limit defaults to 120 requests per 60 seconds; tune it with
`RateLimit__PermitLimit` and `RateLimit__WindowSeconds`. `Games__MaximumActive` limits stored
games (default 500).

### Multiplayer seat display names

Member-only lobby REST responses and `LobbyEvent` seat projections include an optional nullable
`displayName`. Names are looked up using the room repository's authenticated seat owner IDs, never
client-submitted IDs. The Render API calls Lovable Cloud's server-to-server profile lookup endpoint
with at most four UUIDs per request. It receives only each matching ID and public `displayName`;
login usernames, emails, and other profile fields are never read or returned to the C# backend.
Bot and vacant seats, missing profiles, lookup failures, and missing configuration produce
`displayName: null` so the frontend can retain its role-based fallback. Names are trimmed,
whitespace is collapsed, control characters removed, and length is limited to 40 Unicode
characters. Lookup results are server-side cached by user ID for at most five minutes and failures
briefly cached; played-card events do not trigger profile requests.

Configure these variables on the **Render staging API service**:

- `Multiplayer__ProfileLookup__Url` — full HTTPS lookup URL. The current staging preview is
  `https://project--ad084d7c-ff0d-4db3-aa3d-63b4a7bc4884-dev.lovable.app/api/public/multiplayer-profile-lookup`.
- `MULTIPLAYER_PROFILE_LOOKUP_SECRET` — the same strong shared secret configured in Lovable Cloud,
  stored only in Render's protected environment settings.

Both settings are optional at startup; if either is missing or lookup fails, multiplayer continues
with null display names. Never put the shared secret in logs, response bodies, browser/frontend
configuration, source control, or documentation examples. The `-dev.lovable.app` preview endpoint
may change or become unavailable; configure a stable published Lovable endpoint before production.
The Render API does not connect directly to the Lovable Cloud profiles database and does not
require `Supabase__ServiceRoleKey`.

### Developer-only hand inspection

The debug route is unavailable unless both settings are configured. For temporary bot testing in
Render, set:

- `Debug__RevealHandsEnabled=true`
- `Debug__AccessKey` to a long, randomly generated secret stored only in Render's environment
  settings (never in source control or frontend code)

Disable the feature again by setting `Debug__RevealHandsEnabled=false` or removing the key. An
enabled endpoint rejects requests without the exact key. It returns:

```json
{
  "gameId": "game-id",
  "hands": [
    { "seat": "South", "role": "Human", "cards": [{ "suit": "Club", "type": "Ace" }] },
    { "seat": "North", "role": "Partner bot", "cards": [{ "suit": "Heart", "type": "Seven" }] },
    { "seat": "East", "role": "Opponent bot", "cards": [{ "suit": "Spade", "type": "Queen" }] },
    { "seat": "West", "role": "Opponent bot", "cards": [{ "suit": "Diamond", "type": "Ten" }] }
  ]
}
```

Each `cards` array contains all cards currently held by that seat, in the same format as normal
game responses. The example is abbreviated; real hands contain the current cards remaining. Reads
are synchronized with actions and do not change game state. Only the already configured trusted
origins receive the CORS permission for this header.

An authenticated developer request can be made from a secure terminal:

```sh
curl -s 'https://belotgameengine.onrender.com/api/games/<gameId>/debug/hands' \
  -H 'X-Belot-Debug-Key: <your-secret>'
```

Lovable should call this through a server-side function or private development proxy that stores
the Render key in its server environment, then return the result only to authorized testers.
Do not place the key in browser JavaScript, frontend environment variables, or local storage:
browser-held keys are visible to users and defeat the developer-only protection. CORS is not an
authorization boundary; the server-side key check is.

Build and run the root Dockerfile:

```sh
docker build -t belot-web-api .
docker run --rm -p 10000:10000 \
  -e 'Cors__AllowedOrigins__0=https://your-app.lovable.app' \
  belot-web-api
```

The image uses the .NET 10 SDK to publish and the .NET 10 ASP.NET runtime to serve on
`0.0.0.0:10000`, the Render web-service port. On Render, create a Docker web service from this
repository and use the root `Dockerfile`. The service stores games only in process memory;
**all active games are lost when the service restarts or a Render Free instance sleeps/restarts**.
Use a persistent/shared store before scaling to multiple instances or requiring games to survive
restarts.
