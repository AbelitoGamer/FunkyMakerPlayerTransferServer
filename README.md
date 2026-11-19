# FunkyMaker Transfer Server

Small ASP.NET Core (.NET 10) server that lets players in a **FunkyMakerPlayer private room** send
each other local levels. It is separate from the main game server and never talks to it: once a
level has been transferred, the game server only sees an ordinary private match.

## How it works

1. When a player enters a private room, the game hashes their local levels (SHA-256) and calls
   `JoinRoom` with the room code and the list `{hash, name, fileName, size}`.
2. The server sends every member a personal view of the room (`RoomState`): every level anyone has,
   with `youHave`, `othersHave` and `waiting`. The game shows your levels as **READY** (someone else
   has the same file) or **ONLY YOU**, plus other players' levels as **DOWNLOAD NEEDED**.
3. Picking a level calls `JoinQueue(hash)`. Nothing is downloaded yet.
4. The server pairs queued players for the same hash, oldest first, **only if at least one of the
   two has the file**. Two players without it keep waiting until a player with it queues.
5. Both have it: both get `MatchReady` right away. One has it: a transfer starts.
   - `TransferStarted` goes to both (role `upload` / `download`, with a token for the HTTP endpoint).
   - Uploader: `PUT /api/transfers/{id}/file`. The server checks size, ZIP/RAR signature and SHA-256.
   - Downloader gets `ReadyToDownload` and does `GET /api/transfers/{id}/file`.
   - `TransferProgress` (phase `upload` → `download` → `verify`) goes to both players, so each sees
     "Uploading / Opponent uploading / Downloading / Opponent downloading – N%".
   - The downloader checks the hash and calls `CompleteTransfer(id, ok)`.
6. `MatchReady` carries a **fresh 6-character code unique to the pair**. Both clients call the game
   server's `FindMatch` with that code, so other people in the same room can't take the slot.

Uploaded files are kept for the room for `CacheMinutes` after their last use, so the next player
who needs the same level is served from the cache without a new upload. Files are on temporary
disk and the room state is in memory, so a restart just means players re-enter the room.

Cancellation rules: if your partner leaves, cancels or disconnects, you go back into the queue in
your old position (`Requeued = true`). Bad uploads, failed verification and stalls longer than
`StallTimeoutSeconds` cancel the transfer for both players.

## Endpoints

| Path | Purpose |
|---|---|
| `GET /health` | Status, protocol version, limits, counters. Also wakes a sleeping instance. |
| `/transfer` | SignalR hub (`JoinRoom`, `UpdateLevels`, `LeaveRoom`, `JoinQueue`, `LeaveQueue`, `CompleteTransfer`). |
| `PUT /api/transfers/{id}/file` | Upload (header `X-Transfer-Token`). |
| `GET /api/transfers/{id}/file` | Download (header `X-Transfer-Token`). |

The wire types are in `src/FunkyMakerTransferServer/Protocol.cs`. The game has a copy in
`FunkyMakerPlayer/Multiplayer/Transfer/TransferProtocol.cs`; keep them in sync and bump
`Protocol.Version` for breaking changes (the server rejects other versions with a clear message).

## Limits (config section `Transfer`, env vars `Transfer__Name`)

| Setting | Default | |
|---|---|---|
| `MaxUploadMegabytes` | 100 | Levels larger than this are not listed or accepted. |
| `MaxRoomMembers` | 16 | |
| `MaxLevelsPerMember` | 2000 | |
| `StallTimeoutSeconds` | 60 | No progress for this long cancels a transfer. |
| `CacheMinutes` | 15 | |
| `ProgressIntervalMs` | 200 | Progress packet throttle. |
| `HttpRequestsPerMinute` | 60 | Per IP, on `/api`. |
| `StorageDirectory` | temp | Wiped on start. |

## Run locally

```powershell
dotnet run --project src/FunkyMakerTransferServer      # http://0.0.0.0:5000
dotnet test                                             # 20 tests, real in-process server
```

Point the game at it without touching settings:

```powershell
$env:FUNKYMAKER_TRANSFER_URL = "http://127.0.0.1:5000"
dotnet run --project FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0 /p:TargetFrameworks=net10.0
```

To test with someone on another PC on your LAN, use your LAN IP instead of 127.0.0.1 (allow port
5000 through the Windows firewall). Alternatively set `"TransferServerUrl"` in
`%AppData%\FunkyMakerPlayer\multiplayer_settings.json`.

## Deploy on Render

1. Push this repo to GitHub.
2. Render dashboard → **New → Blueprint** → pick the repo. `render.yaml` creates a free Docker web
   service called `funkymaker-transfer` with `/health` as its health check.
3. Its URL will be `https://funkymaker-transfer.onrender.com` if the name is free, which is the
   game's default. If Render gives a different URL, set `TransferServerUrl` in the game's
   `multiplayer_settings.json` (or change `LevelTransferClient.DefaultServerUrl` in the game).

Free instances sleep after a period of inactivity and take a while to wake up; the game waits up
to ~100 s when connecting. Check Render's current free-tier bandwidth and hour limits before
relying on it for many players.
