# A06 - SignalR client contract

This note defines the realtime contract implemented by the API at
`/hubs/multiplayer`. It supersedes the proposed hub path and message names in
the A02 architecture note. SignalR carries authenticated lobby notifications
and public game events only. REST remains the authority for actions and
seat-private snapshots.

## Authentication and connection

Connect to `{apiBaseUrl}/hubs/multiplayer` using a fresh Supabase access token
for every connection or reconnect. In browser clients, configure the SignalR
`accessTokenFactory` to read the current token rather than capturing the token
used at sign-in:

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${apiBaseUrl}/hubs/multiplayer`, {
    accessTokenFactory: async () => {
      const { data } = await supabase.auth.getSession();
      return data.session?.access_token ?? "";
    },
  })
  .withAutomaticReconnect()
  .build();
```

The server validates the JWT and reads identity from its `sub` claim. Each
`JoinRoom` and `JoinGame` call also checks that identity against current
repository membership. The methods accept no client-supplied user ID or seat.
Unsuccessful authorization rejects the subscription. Do not use a room ID,
game ID, invitation code, or connection ID as identity or as a substitute for
membership.

## Subscription methods

Start the connection, fetch the caller's REST state, then subscribe with the
cursor returned by that REST response. Subscribing joins the authorized group
and replays retained events after the supplied cursor while publication is
serialized, so events that race the REST read are replayed or reported as a
gap.

```javascript
await connection.start();

const lobby = await getMemberLobby(roomId);
await connection.invoke(
  "JoinRoom",
  lobby.roomId,
  lobby.eventRevision,
  lobby.eventSequence,
);

const snapshot = await getPrivateGameSnapshot(gameId);
await connection.invoke(
  "JoinGame",
  snapshot.gameId,
  snapshot.revision,
  snapshot.lastEventSequence,
);
```

`JoinRoom(roomId, afterRevision, afterSequence)` and
`JoinGame(gameId, afterRevision, afterSequence)` return:

```json
{
  "snapshotRequired": false,
  "latestRevision": 4,
  "latestSequence": 2
}
```

When retained history is insufficient or a cursor is ahead of the server, the
server emits `SnapshotRequired` to that connection and returns
`snapshotRequired: true`. Its payload is:

```json
{
  "resourceType": "game",
  "resourceId": "opaque-game-id",
  "revision": 4,
  "sequence": 2
}
```

For a game, fetch `GET /api/multiplayer/games/{gameId}` again and resubscribe
with its `revision` and `lastEventSequence`. For a room, fetch
`GET /api/multiplayer/lobbies/{roomId}` and resubscribe with
`eventRevision` and `eventSequence`. REST snapshots remain private to the
requesting member; never put their `state` on a shared client event channel.

## Server-to-client events

Register handlers before subscribing:

```javascript
connection.on("LobbyEvent", event => { /* update public room presentation */ });
connection.on("GameEvent", event => { /* update public table presentation */ });
connection.on("SnapshotRequired", notice => { /* fetch REST state and resubscribe */ });
```

`LobbyEvent` is a public room projection:

```json
{
  "roomId": "opaque-room-id",
  "revision": 17,
  "sequence": 0,
  "type": "player-ready",
  "seat": "North",
  "status": "WaitingForPlayers",
  "humanPlayerCount": 2,
  "maximumHumanPlayers": 4,
  "seats": [
    {
      "seat": "South",
      "team": "SouthNorth",
      "isOccupied": true,
      "isHost": true,
      "isReady": true,
      "botProfileId": null
    }
  ]
}
```

`type` is `player-joined`, `player-ready`, `player-not-ready`, or
`game-started`. `seat` is null for `game-started`. The event contains no user
IDs or invitation code. Lobby `revision` is process-wide and can jump because
changes in other rooms also advance it; do not infer missing room events from
numeric revision gaps. Lobby event `sequence` is currently zero. The room
REST response's `eventRevision` and `eventSequence` are captured with its seat
state under the same per-room serialization gate used by join, ready, and
start operations. Apply a REST lobby response only when its cursor is not older
than the latest lobby cursor already rendered; this prevents a late HTTP
response from overwriting state learned from a newer realtime event.

Within retained history, a lobby cursor is valid only when it is the room's
baseline or an actual event cursor. A client with a stale cursor after journal
eviction receives `SnapshotRequired`; fetching the room REST snapshot
re-establishes a cursor baseline, and subscribing with that returned cursor
does not cause a repeated snapshot-required loop. Numeric gaps caused by
changes in other rooms do not cause false gap notices. Use `SnapshotRequired`
and the room REST snapshot for recovery.

`GameEvent` carries one public action:

```json
{
  "gameId": "opaque-game-id",
  "revision": 8,
  "sequence": 1,
  "event": {
    "sequence": 1,
    "type": "bid",
    "roundNumber": 1,
    "seat": "East",
    "automatic": true
  }
}
```

The nested `event` uses the public event DTO fields documented as
`PublicGameEvent` in `wwwroot/openapi/v1.json`. In particular `seat` is the
absolute engine seat (`South`, `East`, `North`, `West`) and `automatic`
identifies bot-generated events. The DTO may include public bids, played
cards, tricks, declarations and scoring. It never includes a hand, playable
cards, available options, another seat's snapshot, or a full match record.
Missing optional DTO fields are omitted or null according to the existing API
JSON contract.

The unique game event identity is `(gameId, revision, sequence)`. Compare
`(revision, sequence)` lexicographically within one game; sequence starts at
zero for each accepted human action and includes automatic bot actions. Keep
the last processed cursor and ignore duplicate identities. A gap within a
revision, an unexpected game revision, or `SnapshotRequired` should lead to a
fresh private REST snapshot before the UI offers a decision. A REST retry with
the same `idempotencyKey` returns the accepted action without broadcasting its
events again.

## Commands, retries, and bot recovery

There are no hub methods for bids, declarations, card plays, or bot decisions.
Submit a game command only through
`POST /api/multiplayer/games/{gameId}/actions` with the snapshot's
`expectedRevision` and a stable `idempotencyKey`. Reuse the exact body and key
after a timeout. The action response contains the caller's private state and
the public events produced, but that response must not be broadcast to other
clients.

If automatic bot work remains pending after a bot failure, any authenticated
room member can call
`POST /api/multiplayer/games/{gameId}/automation/resume`. This advances only
pending bot automation; it never replays the human action and returns only
`revision` and `automationPending`, not the original submitter's private
snapshot. A `503` means bounded work remains; retry this recovery request or
the original exact idempotent REST request. Other members can continue to
receive public events without the original submitter reconnecting.

## Reconnect, retention, and deployment limits

On disconnect, do not release or transfer a seat. Reconnect with a newly
validated token, fetch the current member lobby and/or private game snapshot
through REST, then invoke the corresponding `Join*` method with that snapshot's
cursor. Automatic reconnect restores only the transport; group membership is
not assumed to survive and must be reauthorized explicitly.

Event journals are process-local and bounded: by default the API keeps at most
512 game events per game and 128 lobby events per room. Configuration can set
`Multiplayer:Realtime:EventsPerGame` from 32 to 4096 and
`Multiplayer:Realtime:EventsPerLobby` from 16 to 1024. At most 512 topic
journals are retained; evicting a journal does not remove a match or lobby,
and clients recover through one fresh REST snapshot, which seeds the journal's
new cursor baseline. A fixed set of 64 per-room lock stripes bounds lock
memory; hash collisions may serialize unrelated lobby operations. The
authoritative game
store has its own separate active-game capacity and completed-game retention
policy documented in `MULTIPLAYER_A05_1.md`.

The game, replay journal, and SignalR groups are single-process state. A
process restart loses games and replay history; PostgreSQL persists lobby
membership only. There is no cross-instance backplane, shared event journal,
or cross-restart replay. Do not horizontally scale realtime traffic or claim
restart-safe match recovery until those are designed and tested.

## Lovable integration requirements

For `valkov01/belote-bridge-battle`, the client must:

1. Use the existing Supabase session token through `accessTokenFactory`.
2. Connect to `/hubs/multiplayer` and invoke `JoinRoom`/`JoinGame` only with
   the cursor from a successful authenticated REST response.
3. Render lobby/game notifications as public projections; use REST for all
   private state and every game command.
4. Persist the full event cursor, deduplicate game events by the complete
   `(gameId, revision, sequence)` tuple, and process automatic events in the
   same seat order as human events.
5. On reconnect, expired authentication, `SnapshotRequired`, or a detected
   game-event gap, refresh through REST and invoke the relevant `Join*` method
   again. Never infer that a reconnect restored a subscription.
6. Retry actions with the exact request and idempotency key. Use the
   automation-resume REST route after a bot failure when the original
   submitting client is unavailable.
7. Treat socket delivery as best-effort notification, not action acceptance;
   only REST action responses and snapshots establish authoritative state.

This repository does not modify or validate the Lovable frontend. The frontend
must separately add a compatible SignalR client package and integration tests.
