# FunkyMaker Online Server

ASP.NET Core (.NET 10) server for FunkyMakerPlayer's online mode (room system, protocol 2). It merges
the official game server (leaderboard, users, verified levels) with this repo's level transfers and
replay exchange, and adds rooms:

- **Room browser**: name, open / password, mode, song being played (+ stars), host, players / max.
- **Rooms** (2-8 players): host (first in the list; the next player takes over if they leave), chat,
  kick (5 min ban), settings editable between rounds (announced in the chat).
- **Settings**: game mode (Versus / Teams / Mirror), song selection (by turns / host only), password,
  player limit, whether guests may pick their local levels.
- **Rounds**: the player whose turn it is picks a song → everybody gets the level (official: download
  from `/api/verified_levels`; local: one holder uploads it once, everybody who lacks it downloads it)
  → players mark themselves ready (the picker is ready automatically, spectators don't count) →
  the match loads → when every player has loaded, a synced start time is sent → players report their
  result → the server ranks them, records the round and passes the turn on (spectators are skipped).
- **Modes**: Versus (exactly 2 players, random sides), Teams (random split; the higher *average*
  score wins), Mirror (everybody plays the same side, chosen by majority vote, ties random).
- **Leaderboard**: pairwise ELO (K = 32 split over the comparisons; Teams compares only against the
  other team). Disconnected players stay in the history but get no stats.

## Layout

| Path | |
|---|---|
| `src/FunkyMakerTransferServer/Protocol.cs` | Wire types. The game has a copy in `FunkyMakerPlayer/Multiplayer/Rooms/RoomProtocol.cs`; keep them in sync and bump `Protocol.Version` on breaking changes (the server refuses other versions in `Hello` with "Update the game"). |
| `RoomRegistry.cs` | All session / room / round / match / transfer state behind one lock; side effects (`Effects`) run after it is released. |
| `RoomHub.cs` | SignalR hub `/multiplayer`. |
| `TransferEndpoints.cs` | `PUT` / `GET /api/transfers/{id}/file` (token header `X-Transfer-Token`). |
| `ReplayExchange.cs` | `PUT` / `GET /api/replays` (online replay tracks). |
| `Official/` | Copied from the official server (namespace `FunkyMakerServer.*`): leaderboard (MongoDB when `MONGODB_URI` is set, else JSON in `FUNKYMAKER_DATA_DIR`), users, verified levels, `/api/server/info`. `LeaderboardService.RecordRound` is new (N players, pairwise ELO). |
| `verified_levels/` | Official levels (copied from the official server). |

## Hub methods (client → server)

`Hello`, `Ping`, `ListRooms`, `CreateRoom`, `JoinRoom`, `LeaveRoom`, `UpdateLevels`, `UpdateSettings`
(host), `Kick` (host), `SendChat`, `SetSpectate`, `SelectSong` (turn player), `SetReady`,
`SetLevelStatus`, `VoteSide` (Mirror), `CompleteDownload`, `MatchLoaded`, `SendState`, `SendHit`,
`SendKeyPress`, `FinishMatch`, `GetLeaderboard`, `GetMatchHistory`.

Server → client: `RoomState` (full, personalised view; `Levels` / `History` are null when unchanged),
`Chat`, `Kicked`, `TransferUpload`, `TransferDownload`, `TransferFailed`, `MatchLoading`, `MatchStart`,
`MatchResults`, `PlayerState`, `PlayerHit`, `PlayerKeyPress`, `Pong`, `LeaderboardResponse`,
`MatchHistoryResponse`.

Note: the server sends the first `RoomState` and the chat history *before* `CreateRoom` / `JoinRoom`
return; the client buffers them.

## Timeouts

| | |
|---|---|
| Loading | 60 s; players who haven't loaded become spectators (round cancelled if fewer than 2 remain). |
| Finishing | 30 s after the first player finishes; missing players are ranked with their last live state. |
| Transfers | `StallTimeoutSeconds` (60) without progress; a failed upload tries the next holder. |

## HTTP

| Path | |
|---|---|
| `GET /health` | Status, protocol, limits, counters. Also wakes a sleeping instance. |
| `GET /api/server/info`, `/api/leaderboard`, `/api/users`, `/api/users/{id}` | Official server API. |
| `GET /api/verified_levels`, `/api/verified_levels/{id}/download` | Official levels. |
| `PUT/GET /api/transfers/{id}/file` | Level transfers. |
| `PUT/GET /api/replays` | Replay tracks. |
| `GET /api/testing` | Test-build gate: `{"open": bool, "message": string}`. |

## Test-build gate (config section `Testing`, env vars `Testing__Name`)

Player builds made with `-p:FunkyTestBuild=true` only start while this says open.
Normal player builds never ask.

| Setting | Default | |
|---|---|---|
| `Open` | true | `false` closes testing: test builds show "testing is closed" and can't be played. |
| `Message` | "" | Shown to players when closed (empty = the client's default text). |

On Render: set `Testing__Open=false` (and optionally `Testing__Message`) in the service's
Environment tab; saving redeploys the service.

## Limits (config section `Transfer`, env vars `Transfer__Name`)

| Setting | Default | |
|---|---|---|
| `MaxUploadMegabytes` | 100 | Levels larger than this are not listed or accepted. |
| `MaxLevelsPerMember` | 2000 | |
| `StallTimeoutSeconds` | 60 | |
| `CacheMinutes` | 15 | Uploaded levels stay reusable in the room this long. |
| `ProgressIntervalMs` | 200 | Room state throttle while downloading. |
| `HttpRequestsPerMinute` | 60 | Per IP, on `/api/transfers` and `/api/replays`. |
| `StorageDirectory` | temp | Wiped on start. |

## Run / test locally

```powershell
dotnet test                                              # 24 tests, real in-process server
$env:PORT = "5097"; dotnet run --project src/FunkyMakerTransferServer
```

Point the game at it without touching settings, and give each window its own profile (separate
PlayerId / name / colour) to test two players on one PC:

```powershell
$env:FUNKYMAKER_SERVER_URL = "http://127.0.0.1:5097"
$env:FUNKYMAKER_MP_PROFILE = "p2"     # second window only
dotnet run --project FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0 -p:TargetFrameworks=net10.0
```

The game's default server is `https://funkymaker-transfer.onrender.com` (this repo on Render);
`"ServerUrl"` in `%AppData%\FunkyMakerPlayer\multiplayer_settings.json` overrides it.

## Deploy on Render

`render.yaml` creates a free Docker web service (`funkymaker-transfer`, health check `/health`,
auto-deploy on push to `main`). For a persistent leaderboard set `MONGODB_URI` (and optionally
`MONGODB_DB`); without it the JSON files are lost when the instance restarts.

## Moving this into the official server repo

Copy `src/FunkyMakerTransferServer/` (rename the project if you like), `tests/`, `Dockerfile` and
`render.yaml`. The official repo's `Hubs/MultiplayerHub.cs` (1v1 protocol) is replaced by `RoomHub`;
old game builds can't use the new server ("Update the game").
