namespace FunkyMakerTransferServer;

/// <summary>
/// Gate for test builds of the player (client built with <c>-p:FunkyTestBuild=true</c>).
/// Bound from the "Testing" config section, so on Render it is set with environment variables:
/// <c>Testing__Open=false</c> closes testing, <c>Testing__Message=...</c> is shown to the player.
/// Normal (non-test) client builds never ask, so this has no effect on them.
/// </summary>
public sealed class TestingOptions
{
    /// <summary>false = test builds refuse to start and show <see cref="Message"/>.</summary>
    public bool Open { get; set; } = true;

    /// <summary>Text shown to players when testing is closed; empty = the client's default text.</summary>
    public string Message { get; set; } = "Testing's currently closed. Come back later!";
}
