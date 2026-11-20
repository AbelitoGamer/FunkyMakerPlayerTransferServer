using Microsoft.AspNetCore.Mvc;
using FunkyMakerServer.Services;

namespace FunkyMakerServer.Controllers
{
    /// <summary>
    /// HTTP endpoint for reading the global leaderboard and recent match history
    /// without requiring an active SignalR session.
    /// </summary>
    [ApiController]
    [Route("api/leaderboard")]
    public class LeaderboardController : ControllerBase
    {
        private readonly LeaderboardService _leaderboardService;

        public LeaderboardController(LeaderboardService leaderboardService)
        {
            _leaderboardService = leaderboardService;
        }

        /// <summary>
        /// GET /api/leaderboard - Returns top players, recent matches, and persistence status.
        /// </summary>
        [HttpGet]
        public IActionResult GetLeaderboard(
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            [FromQuery] int matchesSkip = 0,
            [FromQuery] int matchesTake = 50,
            [FromQuery] string? playerId = null)
        {
            int safeSkip = Math.Max(0, skip);
            int safeTake = Math.Clamp(take, 1, 100);
            int safeMatchesSkip = Math.Max(0, matchesSkip);
            int safeMatchesTake = Math.Clamp(matchesTake, 1, 100);

            var entries = _leaderboardService.GetTopPlayers(safeSkip, safeTake);
            var matches = _leaderboardService.GetRecentMatches(playerId, safeMatchesSkip, safeMatchesTake);

            return Ok(new
            {
                Entries = entries,
                TotalPlayers = _leaderboardService.GetTotalPlayers(),
                Matches = matches,
                TotalMatches = _leaderboardService.GetTotalMatches(),
                Database = new
                {
                    Provider = _leaderboardService.IsMongoDbEnabled ? "MongoDB" : "LocalJson",
                    MongoDbEnabled = _leaderboardService.IsMongoDbEnabled,
                    DatabaseName = _leaderboardService.DatabaseName
                },
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
    }
}
