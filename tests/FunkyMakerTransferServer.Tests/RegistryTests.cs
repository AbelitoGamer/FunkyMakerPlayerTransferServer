using Microsoft.Extensions.Options;

namespace FunkyMakerTransferServer.Tests;

public sealed class RegistryTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (RoomRegistry, ManualTime) Create(TransferOptions? options = null)
    {
        var time = new ManualTime();
        return (new RoomRegistry(Options.Create(options ?? new TransferOptions()), time), time);
    }

    private static JoinRoomRequest Join(string room, params TestLevel[] levels) => new()
    {
        RoomCode = room, PlayerId = Guid.NewGuid().ToString(), Username = "P",
        ProtocolVersion = Protocol.Version, Levels = levels.Select(l => l.Entry).ToList(),
    };

    [Fact]
    public void Stalled_transfer_times_out_without_requeue()
    {
        var (reg, time) = Create();
        var lvl = TestLevel.Create("L", 1000);
        reg.JoinRoom("a", Join("ROOM01", lvl), new Effects());
        reg.JoinRoom("b", Join("ROOM01"), new Effects());
        reg.JoinQueue("b", lvl.Hash, new Effects());
        var fx = new Effects();
        reg.JoinQueue("a", lvl.Hash, fx);
        Assert.Contains(fx.Messages, m => m.Method == Protocol.TransferStarted);

        time.Now = time.Now.AddSeconds(30);
        var early = new Effects();
        reg.Sweep(early);
        Assert.DoesNotContain(early.Messages, m => m.Method == Protocol.TransferCancelled);

        time.Now = time.Now.AddSeconds(31);
        var late = new Effects();
        reg.Sweep(late);
        var cancels = late.Messages.Where(m => m.Method == Protocol.TransferCancelled).ToList();
        Assert.Equal(2, cancels.Count);
        Assert.All(cancels, m => Assert.False(((TransferCancelledPacket)m.Payload).Requeued));
        Assert.Single(late.ToCancel);
    }

    [Fact]
    public void Room_full_is_rejected()
    {
        var (reg, _) = Create(new TransferOptions { MaxRoomMembers = 2 });
        Assert.True(reg.JoinRoom("a", Join("ROOM01"), new Effects()).Ok);
        Assert.True(reg.JoinRoom("b", Join("ROOM01"), new Effects()).Ok);
        Assert.False(reg.JoinRoom("c", Join("ROOM01"), new Effects()).Ok);
    }

    [Fact]
    public void Protocol_mismatch_and_bad_codes_are_rejected()
    {
        var (reg, _) = Create();
        var bad = Join("ROOM01");
        bad.ProtocolVersion = 999;
        Assert.False(reg.JoinRoom("a", bad, new Effects()).Ok);
        Assert.False(reg.JoinRoom("a", Join("abc"), new Effects()).Ok);
        Assert.True(reg.JoinRoom("a", Join("room01"), new Effects()).Ok); // normalised to upper case
    }

    [Fact]
    public void Invalid_level_entries_are_dropped()
    {
        var (reg, _) = Create(new TransferOptions { MaxUploadMegabytes = 1 });
        var req = Join("ROOM01");
        req.Levels.Add(new LevelEntry { Hash = "nothex", Name = "x", FileName = "x.fnmm", Size = 10 });
        req.Levels.Add(new LevelEntry { Hash = new string('a', 64), Name = "big", FileName = "big.fnmm", Size = 2 * 1024 * 1024 });
        req.Levels.Add(new LevelEntry { Hash = new string('b', 64), Name = "exe", FileName = "evil.exe", Size = 10 });
        req.Levels.Add(new LevelEntry { Hash = new string('c', 64), Name = "ok", FileName = "../../ok.FNMM", Size = 10 });
        var fx = new Effects();
        reg.JoinRoom("a", req, fx);
        var state = (RoomStatePacket)fx.Messages.Last(m => m.Method == Protocol.RoomState).Payload;
        var only = Assert.Single(state.Levels);
        Assert.Equal("ok.FNMM", only.FileName);
    }

    [Theory]
    [InlineData("level.fnmm", "level.fnmm")]
    [InlineData("C:\\x\\y\\level.zip", "level.zip")]
    [InlineData("../../etc/passwd", "")]
    [InlineData("a<b>.rar", "a_b_.rar")]
    [InlineData(".fnmm", "")]
    public void File_names_are_sanitised(string input, string expected) =>
        Assert.Equal(expected, RoomRegistry.CleanFileName(input));
}
