# Belot Web API

An ASP.NET Core API for one human playing South against the repository's original bots. It uses
`Belot.Engine.GameMechanics.BelotMatch` for all game state and rules, and keeps each bot's original
decision implementation. The API does not expose the internal match or hidden hands.

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
- `GET /openapi/v1.json` — public OpenAPI 3.0 specification for the endpoints and JSON contract.

The profiles match the app: Random is `RandomPlayer`, Beginner `DummyPlayer`, Skilled
`SmartPlayer`, Expert `ClaudePlayerProfiles.CreateExpert()`, and Master
`ClaudePlayerProfiles.CreateHeuristic()`.

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
allowed. The CORS policy permits GET, POST and OPTIONS, and the Content-Type and Accept request
headers; credentials are not enabled.

The public API contract is available at `https://belotgameengine.onrender.com/openapi/v1.json`.
The global IP-based rate limit defaults to 120 requests per 60 seconds; tune it with
`RateLimit__PermitLimit` and `RateLimit__WindowSeconds`. `Games__MaximumActive` limits stored
games (default 500).

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
