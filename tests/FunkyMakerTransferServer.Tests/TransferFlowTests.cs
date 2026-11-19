using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Security.Cryptography;

namespace FunkyMakerTransferServer.Tests;

public sealed class TransferFlowTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_reports_protocol()
    {
        using var http = _factory.CreateClient();
        var body = await http.GetStringAsync("/health");
        Assert.Contains("\"protocol\":1", body);
    }

    [Fact]
    public async Task Room_state_marks_shared_own_and_missing_levels()
    {
        var shared = TestLevel.Create("Shared");
        var onlyA = TestLevel.Create("OnlyA");
        var onlyB = TestLevel.Create("OnlyB");
        await using var a = await TestPlayer.JoinAsync(_factory, "A", "ROOM01", shared, onlyA);
        await using var b = await TestPlayer.JoinAsync(_factory, "B", "ROOM01", shared, onlyB);

        var state = await a.WaitFor<RoomStatePacket>(Protocol.RoomState, s => s.Members == 2);
        var byHash = state.Levels.ToDictionary(l => l.Hash);
        Assert.Equal(3, byHash.Count);
        Assert.True(byHash[shared.Hash].YouHave);
        Assert.Equal(1, byHash[shared.Hash].OthersHave);
        Assert.True(byHash[onlyA.Hash].YouHave);
        Assert.Equal(0, byHash[onlyA.Hash].OthersHave);
        Assert.False(byHash[onlyB.Hash].YouHave);
        Assert.Equal(1, byHash[onlyB.Hash].OthersHave);
        Assert.Equal("OnlyB", byHash[onlyB.Hash].Name);
    }

    [Fact]
    public async Task Rooms_are_isolated()
    {
        var lvl = TestLevel.Create("Lvl");
        await using var a = await TestPlayer.JoinAsync(_factory, "A", "ROOMAA", lvl);
        await using var b = await TestPlayer.JoinAsync(_factory, "B", "ROOMBB");
        var state = await b.WaitFor<RoomStatePacket>(Protocol.RoomState);
        Assert.Empty(state.Levels);
        var r = await b.Hub.InvokeAsync<QueueResult>("JoinQueue", lvl.Hash);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Both_players_with_the_level_get_the_same_match_code()
    {
        var lvl = TestLevel.Create("Both");
        await using var a = await TestPlayer.JoinAsync(_factory, "A", "ROOM02", lvl);
        await using var b = await TestPlayer.JoinAsync(_factory, "B", "ROOM02", lvl);
        await a.QueueAsync(lvl);
        await b.QueueAsync(lvl);

        var ma = await a.WaitFor<MatchReadyPacket>(Protocol.MatchReady);
        var mb = await b.WaitFor<MatchReadyPacket>(Protocol.MatchReady);
        Assert.Equal(ma.MatchCode, mb.MatchCode);
        Assert.Matches("^[A-Z0-9]{6}$", ma.MatchCode);
        Assert.Equal("B", ma.OpponentName);
        Assert.Empty(a.All<TransferStartedPacket>(Protocol.TransferStarted));
    }

    [Fact]
    public async Task Players_without_the_level_wait_until_a_holder_queues_then_the_oldest_is_served()
    {
        var lvl = TestLevel.Create("Song");
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM03", lvl);
        await using var first = await TestPlayer.JoinAsync(_factory, "First", "ROOM03");
        await using var second = await TestPlayer.JoinAsync(_factory, "Second", "ROOM03");

        await first.QueueAsync(lvl);
        await second.QueueAsync(lvl);
        var q = await second.WaitFor<QueueStatePacket>(Protocol.QueueState, s => s.Waiting == 2);
        Assert.False(q.YouHave);
        Assert.Equal(1, q.HoldersInRoom);
        await Task.Delay(200);
        Assert.Empty(first.All<TransferStartedPacket>(Protocol.TransferStarted));
        Assert.Empty(second.All<MatchReadyPacket>(Protocol.MatchReady));

        // Holder joins matchmaking → paired with the player who waited longest.
        await holder.QueueAsync(lvl);
        var up = await holder.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);
        var down = await first.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);
        Assert.Equal(Protocol.RoleUpload, up.Role);
        Assert.Equal(Protocol.RoleDownload, down.Role);
        Assert.Equal(Protocol.PhaseUpload, up.Phase);
        Assert.Equal("First", up.OpponentName);
        Assert.Equal(lvl.Hash, down.Hash);
        Assert.Empty(second.All<TransferStartedPacket>(Protocol.TransferStarted));

        // Upload; both see upload progress reach 100 %, then the downloader is told to fetch.
        var resp = await holder.UploadAsync(up, lvl.Bytes);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await first.WaitFor<TransferProgressPacket>(Protocol.TransferProgress, p => p.Phase == Protocol.PhaseUpload && p.Bytes == lvl.Bytes.Length);
        await holder.WaitFor<TransferProgressPacket>(Protocol.TransferProgress, p => p.Phase == Protocol.PhaseUpload && p.Bytes == lvl.Bytes.Length);
        await first.WaitFor<ReadyToDownloadPacket>(Protocol.ReadyToDownload, r => r.TransferId == down.TransferId);

        var bytes = await first.DownloadAsync(down);
        Assert.Equal(lvl.Hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await holder.WaitFor<TransferProgressPacket>(Protocol.TransferProgress, p => p.Phase == Protocol.PhaseVerify);

        await first.Hub.InvokeAsync("CompleteTransfer", down.TransferId, true, (string?)null);
        var m1 = await holder.WaitFor<MatchReadyPacket>(Protocol.MatchReady);
        var m2 = await first.WaitFor<MatchReadyPacket>(Protocol.MatchReady);
        Assert.Equal(m1.MatchCode, m2.MatchCode);

        // Second is still waiting. "First" now has the level, so when they queue again they serve
        // Second — straight from the server's cache, with no new upload.
        Assert.Empty(second.All<TransferStartedPacket>(Protocol.TransferStarted));
        await first.Hub.InvokeAsync("UpdateLevels", new List<LevelEntry> { lvl.Entry });
        await first.QueueAsync(lvl);
        var up2 = await first.WaitFor<TransferStartedPacket>(Protocol.TransferStarted, t => t.TransferId != down.TransferId);
        var down2 = await second.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);
        Assert.Equal(Protocol.PhaseDownload, up2.Phase);
        await second.WaitFor<ReadyToDownloadPacket>(Protocol.ReadyToDownload, r => r.TransferId == down2.TransferId);
        var bytes2 = await second.DownloadAsync(down2);
        Assert.Equal(lvl.Bytes, bytes2);
        await second.Hub.InvokeAsync("CompleteTransfer", down2.TransferId, true, (string?)null);
        await second.WaitFor<MatchReadyPacket>(Protocol.MatchReady);
    }

    [Fact]
    public async Task Downloader_is_requeued_when_the_uploader_leaves()
    {
        var lvl = TestLevel.Create("Leave");
        var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM04", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM04");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        var down = await taker.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);

        await holder.DisposeAsync();
        var c = await taker.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
        Assert.Equal(down.TransferId, c.TransferId);
        Assert.True(c.Requeued);
    }

    [Fact]
    public async Task Upload_with_wrong_content_cancels_for_both()
    {
        var lvl = TestLevel.Create("Wrong");
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM05", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM05");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        var up = await holder.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);

        var other = TestLevel.Create("Other", lvl.Bytes.Length);
        var resp = await holder.UploadAsync(up, other.Bytes);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var c1 = await holder.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
        var c2 = await taker.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
        Assert.False(c1.Requeued);
        Assert.False(c2.Requeued);
    }

    [Fact]
    public async Task Non_archive_upload_is_rejected()
    {
        var lvl = TestLevel.Create("NotZip");
        lvl.Bytes[0] = (byte)'X';
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM06", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM06");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        var up = await holder.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);

        var resp = await holder.UploadAsync(up, lvl.Bytes);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        await taker.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
    }

    [Fact]
    public async Task Wrong_token_is_refused()
    {
        var lvl = TestLevel.Create("Token");
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM07", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM07");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        var down = await taker.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);

        // The downloader's token must not allow uploading.
        var resp = await taker.UploadAsync(down, lvl.Bytes);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Failed_verification_cancels_and_does_not_match()
    {
        var lvl = TestLevel.Create("Verify");
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM08", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM08");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        var up = await holder.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);
        var down = await taker.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);
        await holder.UploadAsync(up, lvl.Bytes);
        await taker.WaitFor<ReadyToDownloadPacket>(Protocol.ReadyToDownload);
        await taker.DownloadAsync(down);

        await taker.Hub.InvokeAsync("CompleteTransfer", down.TransferId, false, "hash mismatch");
        var c = await holder.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
        Assert.Contains("hash mismatch", c.Reason);
        Assert.Empty(holder.All<MatchReadyPacket>(Protocol.MatchReady));
    }

    [Fact]
    public async Task Leaving_the_queue_mid_transfer_requeues_the_partner()
    {
        var lvl = TestLevel.Create("Cancel");
        await using var holder = await TestPlayer.JoinAsync(_factory, "Holder", "ROOM09", lvl);
        await using var taker = await TestPlayer.JoinAsync(_factory, "Taker", "ROOM09");
        await taker.QueueAsync(lvl);
        await holder.QueueAsync(lvl);
        await holder.WaitFor<TransferStartedPacket>(Protocol.TransferStarted);

        await taker.Hub.InvokeAsync("LeaveQueue");
        var c = await holder.WaitFor<TransferCancelledPacket>(Protocol.TransferCancelled);
        Assert.True(c.Requeued);
        var q = await holder.WaitFor<QueueStatePacket>(Protocol.QueueState, s => s.Hash == lvl.Hash && s.Waiting == 1);
        Assert.True(q.YouHave);
    }
}
