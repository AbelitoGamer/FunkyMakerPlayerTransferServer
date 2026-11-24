using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;

namespace FunkyMakerTransferServer.Tests;

public sealed class RoomFlowTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ── Session / browser ────────────────────────────────────────────────────

    [Fact]
    public async Task Health_reports_protocol_3()
    {
        using var http = _factory.CreateClient();
        var body = await http.GetStringAsync("/health");
        Assert.Contains("\"protocol\":3", body);
    }

    [Fact]
    public async Task Osu_set_is_packed_once_per_variant_and_sent_to_whoever_lacks_it()
    {
        var osu = TestLevel.Osu("Lagtrain", setSize: 3_000_000, sizeNoVideo: 1_000_000);
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", osu);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", osuSkipVideo: true);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 4 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);

        var listed = (await b.WaitState(s => s.Levels?.Count == 1)).Levels!.Single();
        Assert.Equal(Protocol.KindOsu, listed.Kind);
        Assert.Equal("set-lagtrain", listed.SetId);
        Assert.Equal("Hard", listed.Difficulty);
        Assert.Equal(1_000_000, listed.SizeNoVideo);

        Assert.True((await a.PickLocalAsync(osu)).Ok);
        Assert.Equal(Protocol.KindOsu, a.State.Song!.Kind);

        // B wants the full set, C the set without its video: one upload of each.
        var full = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload, u => u.Variant == Protocol.VariantFull);
        var noVideo = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload, u => u.Variant == Protocol.VariantNoVideo);
        Assert.Equal(Protocol.KindOsu, full.Kind);
        Assert.Equal(0, full.Size);
        Assert.True(full.MaxSize > 3_000_000);
        var fullBytes = TestLevel.Package(200_000);   // packed sizes differ from the estimates
        var smallBytes = TestLevel.Package(50_000);
        (await a.UploadAsync(full, fullBytes)).EnsureSuccessStatusCode();
        (await a.UploadAsync(noVideo, smallBytes)).EnsureSuccessStatusCode();

        foreach (var (p, expected, variant) in new[] { (b, fullBytes, Protocol.VariantFull), (c, smallBytes, Protocol.VariantNoVideo) })
        {
            var down = await p.WaitFor<TransferDownloadPacket>(Protocol.TransferDownload);
            Assert.Equal(variant, down.Variant);
            Assert.Equal(osu.Hash, down.Hash);
            Assert.Equal(expected.Length, down.Size);
            var bytes = await p.DownloadAsync(down);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), down.FileHash);
            Assert.Equal(down.FileHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            await p.Hub.InvokeAsync("CompleteDownload", down.TransferId, true, (string?)null);
        }
        Assert.Equal(2, a.All<TransferUploadPacket>(Protocol.TransferUpload).Count);

        var s = await a.WaitState(x => x.Members.All(m => m.LevelStatus == Protocol.LevelReady));
        var lvl = s.Levels!.Single();
        Assert.Equal(2, lvl.Holders.Count);
        Assert.Equal(Protocol.KindOsu, lvl.Kind);
    }

    [Fact]
    public async Task Song_types_limit_what_can_be_picked()
    {
        var fnf = TestLevel.Create("Fnf");
        var osu = TestLevel.Osu("Mania");
        var official = _factory.Official.Add("Official");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", fnf, osu);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 2, SongTypes = Protocol.SongTypesFnf });
        await b.JoinAsync(id);

        var st = await b.WaitState(s => s.Levels?.Count == 2);
        Assert.Equal(Protocol.SongTypesFnf, st.Settings.SongTypes);
        Assert.True(st.Levels!.Single(l => l.Kind == Protocol.KindFnf).Pickable);
        Assert.False(st.Levels!.Single(l => l.Kind == Protocol.KindOsu).Pickable);
        Assert.False((await a.PickLocalAsync(osu)).Ok);
        Assert.True((await a.PickLocalAsync(fnf)).Ok);

        var r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 2, SongTypes = Protocol.SongTypesOsu });
        Assert.True(r.Ok, r.Error);
        await b.WaitChat("song types");
        await b.WaitChat("cleared"); // the FNF pick is no longer allowed
        Assert.False((await a.PickLocalAsync(fnf)).Ok);
        var off = await a.Hub.InvokeAsync<OpResult>("SelectSong", new SongSelection { Source = Protocol.SourceOfficial, Hash = official.Hash });
        Assert.False(off.Ok);
        Assert.True((await a.PickLocalAsync(osu)).Ok);

        // Unknown values fall back to both.
        r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 2, SongTypes = "weird" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Protocol.SongTypesBoth, (await b.WaitState(s => s.Settings.SongTypes == Protocol.SongTypesBoth)).Settings.SongTypes);
    }

    [Fact]
    public async Task Osu_songs_are_only_played_in_mirror_rooms()
    {
        var osu = TestLevel.Osu("Mania");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", osu);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeVersus, MaxPlayers = 2 });
        await b.JoinAsync(id);

        var st = await b.WaitState(s => s.Levels?.Count == 1);
        Assert.False(st.Levels!.Single().Pickable);
        var pick = await a.PickLocalAsync(osu);
        Assert.False(pick.Ok);
        Assert.Contains("Mirror", pick.Error);

        // osu!mania only needs Mirror.
        var bad = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeVersus, MaxPlayers = 2, SongTypes = Protocol.SongTypesOsu });
        Assert.False(bad.Ok);

        var r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 2 });
        Assert.True(r.Ok, r.Error);
        Assert.True((await b.WaitState(s => s.Levels?.SingleOrDefault()?.Pickable == true)).Levels!.Single().Pickable);
        Assert.True((await a.PickLocalAsync(osu)).Ok);

        // Leaving Mirror clears the osu! pick.
        r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeTeams, MaxPlayers = 2 });
        Assert.True(r.Ok, r.Error);
        await b.WaitChat("cleared");
    }

    [Fact]
    public async Task Old_protocol_is_refused_with_an_update_message()
    {
        var server = _factory.Server;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/multiplayer"), o =>
            {
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            }).Build();
        await hub.StartAsync();
        var r = await hub.InvokeAsync<HelloResult>("Hello", new HelloRequest { Username = "Old", ProtocolVersion = 1 });
        Assert.False(r.Ok);
        Assert.Contains("Update the game", r.Error);
    }

    [Fact]
    public async Task Room_list_shows_rooms_and_password_is_checked()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "Alice");
        await using var b = await TestPlayer.ConnectAsync(_factory, "Bob");
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeTeams, MaxPlayers = 4, Password = "secret" }, "Alice's party");

        var list = await b.Hub.InvokeAsync<List<RoomSummary>>("ListRooms");
        var room = Assert.Single(list);
        Assert.Equal(id, room.Id);
        Assert.Equal("Alice's party", room.Name);
        Assert.True(room.HasPassword);
        Assert.Equal(Protocol.ModeTeams, room.Mode);
        Assert.Equal("Alice", room.HostName);
        Assert.Equal(1, room.PlayerCount);
        Assert.Equal(4, room.MaxPlayers);
        Assert.Equal("Alice", Assert.Single(room.Players).Username);

        var wrong = await b.TryJoinAsync(id, "nope");
        Assert.False(wrong.Ok);
        Assert.Contains("password", wrong.Error, StringComparison.OrdinalIgnoreCase);
        await b.JoinAsync(id, "secret");

        var state = await b.WaitState(s => s.Members.Count == 2);
        Assert.Equal("", state.Settings.Password); // only the host sees it
        Assert.True(state.HasPassword);
        Assert.Equal("secret", (await a.WaitState(s => s.Members.Count == 2)).Settings.Password);
        Assert.Equal(new[] { "Alice", "Bob" }, state.Members.Select(m => m.Username));
        Assert.True(state.Members[0].IsHost);
        await a.WaitChat("Bob joined");
    }

    [Fact]
    public async Task Full_room_refuses_joins()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        await using var c = await TestPlayer.ConnectAsync(_factory, "C");
        string id = await a.CreateAsync(new RoomSettings { MaxPlayers = 2 });
        await b.JoinAsync(id);
        var r = await c.TryJoinAsync(id);
        Assert.False(r.Ok);
        Assert.Contains("full", r.Error);
    }

    // ── Round flow ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_round_with_shared_level_ranks_players_and_passes_the_turn()
    {
        var lvl = TestLevel.Create("Shared");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        var s = await b.WaitState(x => x.Members.Count == 2);
        Assert.Equal(a.PlayerId, s.TurnPlayerId); // host has the first turn

        var notYourTurn = await b.PickLocalAsync(lvl);
        Assert.False(notYourTurn.Ok);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        s = await b.WaitState(x => x.Song != null);
        Assert.Equal(Protocol.PhaseSongSelected, s.Phase);
        Assert.Equal("Shared", s.Song!.Name);
        Assert.Equal(4.2f, s.Song.Stars);
        Assert.Equal(Protocol.StatusReady, s.Members.Single(m => m.Id == a.PlayerId).Status); // picker is ready
        Assert.Equal(Protocol.LevelReady, s.Members.Single(m => m.Id == b.PlayerId).LevelStatus);
        Assert.Contains("B", s.StartBlocker);

        Assert.True((await b.ReadyAsync()).Ok);
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        Assert.Equal(2, info.Players.Count);
        Assert.Equal(new[] { 0, 1 }, info.Players.Select(p => p.Side).OrderBy(x => x));

        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        // The others' loading screens learn who has loaded.
        var loadedState = await b.WaitState(x => x.Match != null && x.Match.Loaded.Contains(a.PlayerId));
        Assert.DoesNotContain(b.PlayerId, loadedState.Match!.Loaded);
        await b.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        var start = await b.WaitFor<MatchStartPacket>(Protocol.MatchStart);
        Assert.True(start.StartTimestamp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
        Assert.Equal(Protocol.PhasePlaying, (await a.WaitState(x => x.Phase == Protocol.PhasePlaying)).Phase);

        // Relay goes to the other player with the sender's id.
        await a.Hub.InvokeAsync("SendHit", new PlayerHitPacket { Lane = 2, Rating = "Sick!" });
        var hit = await b.WaitFor<PlayerHitPacket>(Protocol.PlayerHit);
        Assert.Equal(a.PlayerId, hit.PlayerId);
        Assert.Equal(2, hit.Lane);

        await a.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 5000, Accuracy = 95, Misses = 2 });
        Assert.Empty(a.All<RoundResult>(Protocol.MatchResults));
        await b.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 7000, Accuracy = 98, Misses = 0 });

        var result = await a.WaitFor<RoundResult>(Protocol.MatchResults);
        Assert.Equal("B", result.Players[0].Username);
        Assert.Equal(1, result.Players[0].Place);
        Assert.Equal(2, result.Players[1].Place);

        s = await a.WaitState(x => x.Phase == Protocol.PhaseLobby && x.History is { Count: 1 });
        Assert.Equal(b.PlayerId, s.TurnPlayerId); // turn moved on
        Assert.Null(s.Song);
        await a.WaitChat("B wins");

        // Recorded in the global leaderboard with a pairwise ELO change.
        using var http = _factory.CreateClient();
        var lb = await http.GetStringAsync("/api/leaderboard");
        Assert.Contains(result.MatchId, lb);
    }

    [Fact]
    public async Task Local_level_is_uploaded_once_and_downloaded_by_everybody_who_lacks_it()
    {
        var lvl = TestLevel.Create("OnlyHost", 400_000);
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        await using var c = await TestPlayer.ConnectAsync(_factory, "C");
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeTeams, MaxPlayers = 4 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        var up = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload);
        Assert.Equal(lvl.Hash, up.Hash);
        (await a.UploadAsync(up, lvl.Bytes)).EnsureSuccessStatusCode();

        foreach (var p in new[] { b, c })
        {
            var down = await p.WaitFor<TransferDownloadPacket>(Protocol.TransferDownload);
            var bytes = await p.DownloadAsync(down);
            Assert.Equal(lvl.Hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            await p.Hub.InvokeAsync("CompleteDownload", down.TransferId, true, (string?)null);
        }
        Assert.Single(a.All<TransferUploadPacket>(Protocol.TransferUpload));

        var s = await a.WaitState(x => x.Members.All(m => m.LevelStatus == Protocol.LevelReady));
        Assert.Contains("ready", s.StartBlocker);
        Assert.True((await b.ReadyAsync()).Ok);
        Assert.True((await c.ReadyAsync()).Ok);
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        Assert.Equal(3, info.Players.Count);
        Assert.Contains(info.Players, p => p.Team == 0);
        Assert.Contains(info.Players, p => p.Team == 1);
    }

    [Fact]
    public async Task Player_limit_follows_MaxRoomMembers()
    {
        using var factory = new ServerFactory();
        factory.Settings["Transfer:MaxRoomMembers"] = "12";
        await using var a = await TestPlayer.ConnectAsync(factory, "A");
        var hello = await a.Hub.InvokeAsync<HelloResult>("Hello", new HelloRequest
        {
            PlayerId = a.PlayerId, Username = "A", ColorHue = 120, ProtocolVersion = Protocol.Version,
        });
        Assert.Equal(12, hello.MaxRoomPlayers);

        var tooMany = await a.Hub.InvokeAsync<OpResult>("CreateRoom", new CreateRoomRequest { Name = "Big", Settings = new RoomSettings { MaxPlayers = 13 } });
        Assert.False(tooMany.Ok);
        string id = await a.CreateAsync(new RoomSettings { MaxPlayers = 12 });
        Assert.Equal(12, (await a.WaitState(s => s.RoomId == id)).Settings.MaxPlayers);
    }

    [Fact]
    public async Task Player_limit_defaults_to_8()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        var r = await a.Hub.InvokeAsync<OpResult>("CreateRoom", new CreateRoomRequest { Name = "Big", Settings = new RoomSettings { MaxPlayers = 9 } });
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Ready_requires_the_level()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        var r = await b.ReadyAsync();
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Forcestart_is_gone_and_just_a_chat_message()
    {
        var lvl = TestLevel.Create("Forced");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await a.WaitState(x => x.Members.All(m => m.LevelStatus == Protocol.LevelReady));

        await a.Hub.InvokeAsync("SendChat", "/forcestart");

        await b.WaitChat("/forcestart");
        Assert.Empty(b.All<MatchInfo>(Protocol.MatchLoading));
    }

    [Fact]
    public async Task A_picked_song_cannot_be_changed()
    {
        var one = TestLevel.Create("One");
        var two = TestLevel.Create("Two");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", one, two);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(one)).Ok);
        var again = await a.PickLocalAsync(two);
        Assert.False(again.Ok);
        Assert.Contains("can't be changed", again.Error);
        Assert.Equal(one.Hash, (await b.WaitState(s => s.Song != null)).Song!.Hash);
    }

    [Fact]
    public async Task Quitting_while_loading_takes_the_player_out_of_the_match()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", lvl);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 3 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        await c.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await c.Hub.InvokeAsync("MatchLoaded", info.MatchId);

        // B quits before loading: the others start without waiting for the loading timeout.
        await b.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Quit = true });
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);
        var st = await a.WaitState(x => x.Phase == Protocol.PhasePlaying);
        Assert.Equal(Protocol.StatusSpectating, st.Members.Single(m => m.Id == b.PlayerId).Status);
        Assert.DoesNotContain(st.Match!.Players, p => p.PlayerId == b.PlayerId);
    }

    [Fact]
    public async Task Silent_player_is_dropped_and_the_round_ends()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await b.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);

        // A finishes; B (still connected) never sends anything again, like a crashed game.
        await a.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 100, Sicks = 1 });
        int wait = (int)(RoomRegistry.SilenceTimeout.TotalMilliseconds + RoomRegistry.CountdownMs + 8000);
        await a.WaitChat("stopped responding", wait);
        var result = await a.WaitFor<RoundResult>(Protocol.MatchResults);
        Assert.True(result.Players.Single(p => p.Username == "B").Disconnected);
        Assert.Equal(Protocol.PhaseLobby, (await a.WaitState(x => x.Phase == Protocol.PhaseLobby)).Phase);
    }

    [Fact]
    public async Task Nothing_judged_is_0_percent_not_100()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await b.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);

        // An old client reports 100 % for an empty chart.
        await a.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 0, Accuracy = 100 });
        await b.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 50, Accuracy = 90, Sicks = 9, Misses = 1 });
        var result = await a.WaitFor<RoundResult>(Protocol.MatchResults);
        Assert.Equal(0f, result.Players.Single(p => p.Username == "A").Accuracy);
        Assert.Equal(90f, result.Players.Single(p => p.Username == "B").Accuracy);
    }

    [Fact]
    public async Task Upload_progress_is_its_own_field()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        // The uploader has the level (ready, 100 %) and an upload in progress (0 %).
        var st = await b.WaitState(x => x.Members.Single(m => m.Id == a.PlayerId).UploadProgress >= 0f);
        var am = st.Members.Single(m => m.Id == a.PlayerId);
        Assert.Equal(Protocol.LevelReady, am.LevelStatus);
        Assert.Equal(1f, am.LevelProgress);
        Assert.Equal(-1f, st.Members.Single(m => m.Id == b.PlayerId).UploadProgress);
    }

    [Fact]
    public async Task Official_level_needs_the_client_to_report_it_ready()
    {
        var off = _factory.Official.Add("Spirit Dance");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        var pick = await a.Hub.InvokeAsync<OpResult>("SelectSong", new SongSelection { Source = Protocol.SourceOfficial, Hash = off.Hash });
        Assert.True(pick.Ok, pick.Error);
        var s = await b.WaitState(x => x.Song != null);
        Assert.Equal("spirit dance", s.Song!.OfficialId);
        Assert.Equal(Protocol.LevelMissing, s.Members.Single(m => m.Id == b.PlayerId).LevelStatus);

        await b.Hub.InvokeAsync("SetLevelStatus", new LevelStatusUpdate { Hash = off.Hash, State = Protocol.LevelDownloading, Progress = 0.5f });
        s = await a.WaitState(x => x.Members.Any(m => m.Id == b.PlayerId && m.LevelStatus == Protocol.LevelDownloading));
        Assert.Equal(0.5f, s.Members.Single(m => m.Id == b.PlayerId).LevelProgress);

        await a.Hub.InvokeAsync("SetLevelStatus", new LevelStatusUpdate { Hash = off.Hash, State = Protocol.LevelReady });
        await b.Hub.InvokeAsync("SetLevelStatus", new LevelStatusUpdate { Hash = off.Hash, State = Protocol.LevelReady });
        Assert.True((await b.ReadyAsync()).Ok);
        await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
    }

    [Fact]
    public async Task Versus_with_three_players_is_blocked_until_one_spectates()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", lvl);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeVersus, MaxPlayers = 3 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        Assert.True((await b.ReadyAsync()).Ok);
        Assert.True((await c.ReadyAsync()).Ok);

        var s = await a.WaitState(x => x.Members.Count(m => m.Status == Protocol.StatusReady) == 3);
        Assert.Contains("Versus needs exactly 2", s.StartBlocker);
        Assert.Empty(a.All<MatchInfo>(Protocol.MatchLoading));

        await c.Hub.InvokeAsync("SetSpectate", true);
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        Assert.DoesNotContain(info.Players, p => p.PlayerId == c.PlayerId);
        // The spectator also gets MatchLoading (to watch).
        await c.WaitFor<MatchInfo>(Protocol.MatchLoading);
        s = await c.WaitState(x => x.Phase == Protocol.PhaseLoading);
        Assert.Equal(Protocol.StatusSpectating, s.Members.Single(m => m.Id == c.PlayerId).Status);
        Assert.Equal(Protocol.StatusPlaying, s.Members.Single(m => m.Id == a.PlayerId).Status);
    }

    [Fact]
    public async Task Turns_skip_spectators_and_host_only_rooms_always_give_the_host_the_turn()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", lvl);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 3 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        await b.Hub.InvokeAsync("SetSpectate", true);

        await PlayRoundAsync(lvl, a, a, c);
        var s = await a.WaitState(x => x.Phase == Protocol.PhaseLobby && x.History is { Count: 1 });
        Assert.Equal(c.PlayerId, s.TurnPlayerId); // B is spectating → skipped

        var r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings
        {
            Mode = Protocol.ModeMirror, SongSelection = Protocol.SelectHost, MaxPlayers = 3, AllowGuestLocalLevels = true,
        });
        Assert.True(r.Ok, r.Error);
        s = await a.WaitState(x => x.Settings.SongSelection == Protocol.SelectHost);
        Assert.Equal(a.PlayerId, s.TurnPlayerId);
        await c.WaitChat("song selection → host only");
    }

    [Fact]
    public async Task Mirror_majority_vote_picks_the_side_for_everybody()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", lvl);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 3 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        Assert.True((await a.Hub.InvokeAsync<OpResult>("VoteSide", 0)).Ok);
        Assert.True((await b.Hub.InvokeAsync<OpResult>("VoteSide", 1)).Ok);
        Assert.True((await c.Hub.InvokeAsync<OpResult>("VoteSide", 1)).Ok);
        await b.ReadyAsync();
        await c.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        Assert.Equal(1, info.MirrorSide);
        Assert.All(info.Players, p => Assert.Equal(1, p.Side));
    }

    [Fact]
    public async Task Host_can_kick_and_kicked_player_cannot_rejoin_for_a_while()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        var notHost = await b.Hub.InvokeAsync<OpResult>("Kick", a.PlayerId);
        Assert.False(notHost.Ok);

        Assert.True((await a.Hub.InvokeAsync<OpResult>("Kick", b.PlayerId)).Ok);
        await b.WaitFor<KickedPacket>(Protocol.Kicked);
        await a.WaitChat("B was kicked");
        var again = await b.TryJoinAsync(id);
        Assert.False(again.Ok);
        Assert.Contains("kicked", again.Error);
    }

    [Fact]
    public async Task Host_leaving_hands_the_room_to_the_next_player()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        await using var c = await TestPlayer.ConnectAsync(_factory, "C");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        await a.Hub.InvokeAsync("LeaveRoom");
        var s = await c.WaitState(x => x.Members.Count == 2);
        Assert.Equal(b.PlayerId, s.HostId);
        Assert.Equal(b.PlayerId, s.TurnPlayerId);
        await c.WaitChat("B is the new host");
    }

    [Fact]
    public async Task Guests_can_only_pick_official_or_host_levels_when_guest_levels_are_off()
    {
        var hostLvl = TestLevel.Create("HostLvl");
        var guestLvl = TestLevel.Create("GuestLvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", hostLvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", guestLvl);
        string id = await a.CreateAsync(new RoomSettings { AllowGuestLocalLevels = false, MaxPlayers = 2 });
        await b.JoinAsync(id);

        var s = await b.WaitState(x => x.Members.Count == 2 && x.Levels != null);
        var levels = b.State.Levels!.ToDictionary(l => l.Hash);
        Assert.True(levels[hostLvl.Hash].Pickable);
        Assert.False(levels[guestLvl.Hash].Pickable);

        var r = await a.PickLocalAsync(guestLvl);
        Assert.False(r.Ok);
        Assert.Contains("official", r.Error);
    }

    [Fact]
    public async Task Settings_cannot_change_during_a_match_and_changes_are_announced()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync(new RoomSettings { MaxPlayers = 4 });
        await b.JoinAsync(id);

        var r = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeTeams, MaxPlayers = 6, Password = "pw" });
        Assert.True(r.Ok, r.Error);
        await b.WaitChat("mode → Teams");
        await b.WaitChat("password protected");

        var tooSmall = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeTeams, MaxPlayers = 1 });
        Assert.False(tooSmall.Ok);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        var during = await a.Hub.InvokeAsync<OpResult>("UpdateSettings", new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 6 });
        Assert.False(during.Ok);
    }

    [Fact]
    public async Task Disconnect_mid_match_still_finishes_the_round()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await b.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);
        await b.Hub.InvokeAsync("SendState", new PlayerStatePacket { Score = 1234 });
        await b.DisposeAsync();

        await a.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 100 });
        var result = await a.WaitFor<RoundResult>(Protocol.MatchResults);
        var bResult = result.Players.Single(p => p.Username == "B");
        Assert.True(bResult.Disconnected);
        Assert.Equal(1234, bResult.Score);
        Assert.Equal(2, bResult.Place); // disconnected players are last
    }

    [Fact]
    public async Task Exit_match_marks_the_player_as_left_and_announces_it()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C", lvl);
        string id = await a.CreateAsync(new RoomSettings { Mode = Protocol.ModeMirror, MaxPlayers = 3 });
        await b.JoinAsync(id);
        await c.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        await c.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        foreach (var p in new[] { a, b, c }) await p.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);

        // B quits with a high score: announced, and ranked last as "left".
        await b.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 99999, Quit = true });
        await a.WaitChat("B left the match");
        Assert.Empty(a.All<RoundResult>(Protocol.MatchResults)); // the others are still playing
        // Statuses follow: B is back in the room (LEFT MATCH), not PLAYING until the round ends.
        await c.WaitState(x => x.Members.Single(m => m.Id == b.PlayerId).Status == Protocol.StatusLeftMatch);
        await a.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 500 });
        var finished = await c.WaitState(x => x.Members.Single(m => m.Id == a.PlayerId).Status == Protocol.StatusFinished);
        Assert.Equal(Protocol.StatusPlaying, finished.Members.Single(m => m.Id == c.PlayerId).Status);
        await c.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = 400 });
        var result = await a.WaitFor<RoundResult>(Protocol.MatchResults);
        var bRes = result.Players.Single(p => p.Username == "B");
        Assert.True(bRes.Disconnected);
        Assert.Equal("B", result.Players[^1].Username);
        Assert.Equal("A", result.Players[0].Username);
    }

    [Fact]
    public async Task Mid_match_joiner_spectates_and_gets_the_level()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B", lvl);
        await using var c = await TestPlayer.ConnectAsync(_factory, "C");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);
        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        await b.ReadyAsync();
        var info = await a.WaitFor<MatchInfo>(Protocol.MatchLoading);
        await a.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await b.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await a.WaitFor<MatchStartPacket>(Protocol.MatchStart);

        await c.JoinAsync(id);
        var s = await c.WaitState(x => x.Phase == Protocol.PhasePlaying);
        Assert.NotNull(s.Match);
        Assert.True(s.Match!.StartTimestamp > 0);
        Assert.Equal(Protocol.StatusSpectating, s.Members.Single(m => m.Id == c.PlayerId).Status);

        // Nobody had uploaded it yet (everybody playing had it): a holder is asked to upload now.
        var up = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload);
        (await a.UploadAsync(up, lvl.Bytes)).EnsureSuccessStatusCode();
        var down = await c.WaitFor<TransferDownloadPacket>(Protocol.TransferDownload);
        Assert.Equal(lvl.Hash, Convert.ToHexStringLower(SHA256.HashData(await c.DownloadAsync(down))));

        // Relays reach the spectator too.
        await a.Hub.InvokeAsync("SendState", new PlayerStatePacket { Score = 42 });
        var st = await c.WaitFor<PlayerStatePacket>(Protocol.PlayerState);
        Assert.Equal(a.PlayerId, st.PlayerId);
    }

    [Fact]
    public async Task Chat_is_broadcast_and_new_members_get_the_history()
    {
        await using var a = await TestPlayer.ConnectAsync(_factory, "A");
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await a.Hub.InvokeAsync("SendChat", "hello there");
        await b.JoinAsync(id);
        var msg = await b.WaitChat("hello there");
        Assert.Equal("A", msg.Username);
        Assert.False(msg.System);
    }

    [Fact]
    public async Task Slow_download_uses_completion_grace_after_server_stream_finishes()
    {
        using var factory = new ServerFactory();
        factory.Settings["Transfer:StallTimeoutSeconds"] = "1";
        factory.Settings["Transfer:DownloadCompletionGraceSeconds"] = "10";
        var lvl = TestLevel.Create("SlowPhone", 200_000);
        await using var a = await TestPlayer.ConnectAsync(factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        var up = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload);
        (await a.UploadAsync(up, lvl.Bytes)).EnsureSuccessStatusCode();
        var down = await b.WaitFor<TransferDownloadPacket>(Protocol.TransferDownload);
        var bytes = await b.DownloadAsync(down);
        Assert.Equal(lvl.Hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        // Exceed StallTimeoutSeconds (1 s) while still within DownloadCompletionGraceSeconds (10 s).
        await Task.Delay(1500);
        Assert.Empty(b.All<TransferFailedPacket>(Protocol.TransferFailed));

        await b.Hub.InvokeAsync("CompleteDownload", down.TransferId, true, (string?)null);
        var st = await a.WaitState(x => x.Members.Any(m => m.Id == b.PlayerId && m.LevelStatus == Protocol.LevelReady));
        Assert.Equal(Protocol.LevelReady, st.Members.Single(m => m.Id == b.PlayerId).LevelStatus);
    }

    [Fact]
    public async Task Late_complete_download_after_sweep_still_recovers_player_to_ready()
    {
        using var factory = new ServerFactory();
        factory.Settings["Transfer:StallTimeoutSeconds"] = "1";
        factory.Settings["Transfer:DownloadCompletionGraceSeconds"] = "1";
        var lvl = TestLevel.Create("LateFinish", 200_000);
        await using var a = await TestPlayer.ConnectAsync(factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        var up = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload);
        (await a.UploadAsync(up, lvl.Bytes)).EnsureSuccessStatusCode();
        var down = await b.WaitFor<TransferDownloadPacket>(Protocol.TransferDownload);
        _ = await b.DownloadAsync(down);

        // Wait until Sweep expires the download and sends TransferFailed.
        await b.WaitFor<TransferFailedPacket>(Protocol.TransferFailed, timeoutMs: 4000);

        // Even after Sweep expired it, a valid CompleteDownload(ok: true) still marks B ready.
        await b.Hub.InvokeAsync("CompleteDownload", down.TransferId, true, (string?)null);
        var st = await a.WaitState(x => x.Members.Any(m => m.Id == b.PlayerId && m.LevelStatus == Protocol.LevelReady));
        Assert.Equal(Protocol.LevelReady, st.Members.Single(m => m.Id == b.PlayerId).LevelStatus);
    }

    [Fact]
    public async Task Truncated_upload_reports_early_end_instead_of_file_changed()
    {
        var lvl = TestLevel.Create("Truncated", 200_000);
        await using var a = await TestPlayer.ConnectAsync(_factory, "A", lvl);
        await using var b = await TestPlayer.ConnectAsync(_factory, "B");
        string id = await a.CreateAsync();
        await b.JoinAsync(id);

        Assert.True((await a.PickLocalAsync(lvl)).Ok);
        var up = await a.WaitFor<TransferUploadPacket>(Protocol.TransferUpload);
        // Announce the full expected Content-Length but close the body after half the bytes.
        using var http = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{up.TransferId}/file")
        {
            Content = new TruncatedContent(lvl.Bytes, lvl.Bytes.Length / 2),
        };
        req.Headers.Add(Protocol.TokenHeader, up.Token);
        var resp = await http.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);

        var failed = await b.WaitFor<TransferFailedPacket>(Protocol.TransferFailed);
        Assert.Contains("ended early", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TruncatedContent(byte[] data, int sendBytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(data, 0, sendBytes);
        protected override bool TryComputeLength(out long length) { length = data.Length; return true; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task PlayRoundAsync(TestLevel lvl, TestPlayer picker, params TestPlayer[] players)
    {
        Assert.True((await picker.PickLocalAsync(lvl)).Ok);
        foreach (var p in players.Distinct().Where(p => p != picker))
            Assert.True((await p.ReadyAsync()).Ok);
        var info = await picker.WaitFor<MatchInfo>(Protocol.MatchLoading, i => true);
        foreach (var p in players.Distinct())
            await p.Hub.InvokeAsync("MatchLoaded", info.MatchId);
        await picker.WaitFor<MatchStartPacket>(Protocol.MatchStart, m => m.MatchId == info.MatchId);
        int score = 1000;
        foreach (var p in players.Distinct())
            await p.Hub.InvokeAsync("FinishMatch", new FinishMatchRequest { MatchId = info.MatchId, Score = score += 100 });
        await picker.WaitFor<RoundResult>(Protocol.MatchResults, r => r.MatchId == info.MatchId);
    }
}

public sealed class RankingTests
{
    [Fact]
    public void Teams_use_the_average_so_uneven_teams_are_fair()
    {
        var results = new List<PlayerResult>
        {
            new() { Username = "a", Team = 0, Score = 900 },
            new() { Username = "b", Team = 0, Score = 300 },
            new() { Username = "c", Team = 1, Score = 700 },
        };
        var round = new RoundResult { Mode = Protocol.ModeTeams };
        RoomRegistry.RankResults(results, round);
        Assert.Equal(600, round.TeamScores[0]);
        Assert.Equal(700, round.TeamScores[1]);
        Assert.Equal(1, round.WinningTeam); // a sum would have given team 0 the win
        Assert.Equal(new[] { "a", "c", "b" }, results.Select(r => r.Username));
    }

    [Fact]
    public void Ties_share_a_place_and_disconnected_players_are_last()
    {
        var results = new List<PlayerResult>
        {
            new() { Username = "gone", Score = 9999, Disconnected = true },
            new() { Username = "x", Score = 500 },
            new() { Username = "y", Score = 500 },
            new() { Username = "z", Score = 100 },
        };
        RoomRegistry.RankResults(results, new RoundResult { Mode = Protocol.ModeMirror });
        Assert.Equal(new[] { 1, 1, 3, 4 }, results.Select(r => r.Place));
        Assert.Equal("gone", results[^1].Username);
    }
}

public sealed class EloTests
{
    private static PlayerResult P(string id, int place, int team = -1) =>
        new() { PlayerId = id + Guid.NewGuid().ToString("N"), Username = id, Place = place, Team = team, Score = 1000 - place };

    [Fact]
    public void Mirror_three_players_pairwise()
    {
        var lb = new FunkyMakerServer.Services.LeaderboardService();
        var round = new RoundResult
        {
            MatchId = Guid.NewGuid().ToString("N"), Mode = Protocol.ModeMirror, Song = new SongInfo { Name = "S" },
            Players = { P("first", 1), P("second", 2), P("third", 3) },
        };
        Assert.True(lb.RecordRound(round));
        Assert.False(lb.RecordRound(round)); // same MatchId is ignored
        var m = lb.GetRecentMatches(take: 1).Single();
        Assert.Equal(new[] { 16, 0, -16 }, m.Players.Select(p => p.EloChange));
        Assert.Equal("first", m.Player1.Username);
        Assert.Equal(1, m.Winner);
    }

    [Fact]
    public void Teams_compare_only_against_the_other_team()
    {
        var lb = new FunkyMakerServer.Services.LeaderboardService();
        var round = new RoundResult
        {
            MatchId = Guid.NewGuid().ToString("N"), Mode = Protocol.ModeTeams, Song = new SongInfo { Name = "S" }, WinningTeam = 1,
            Players = { P("a", 1, team: 0), P("b", 2, team: 1), P("c", 3, team: 1) },
        };
        Assert.True(lb.RecordRound(round));
        var m = lb.GetRecentMatches(take: 1).Single();
        Assert.Equal(-16, m.Players.Single(p => p.Username == "a").EloChange);
        Assert.Equal(16, m.Players.Single(p => p.Username == "b").EloChange);
        Assert.Equal(16, m.Players.Single(p => p.Username == "c").EloChange);
    }
}
