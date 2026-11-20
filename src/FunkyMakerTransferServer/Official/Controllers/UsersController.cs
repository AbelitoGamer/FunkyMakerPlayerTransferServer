using Microsoft.AspNetCore.Mvc;
using FunkyMakerServer.Services;

namespace FunkyMakerServer.Controllers
{
    /// <summary>
    /// HTTP endpoint for looking up persisted & active multiplayer user profiles/usernames.
    /// </summary>
    [ApiController]
    [Route("api/users")]
    public class UsersController : ControllerBase
    {
        private readonly LeaderboardService _leaderboardService;

        public UsersController(LeaderboardService leaderboardService)
        {
            _leaderboardService = leaderboardService;
        }

        /// <summary>
        /// GET /api/users - Returns known players and their usernames from the database and active sessions.
        /// </summary>
        [HttpGet]
        public IActionResult GetUsers([FromQuery] int skip = 0, [FromQuery] int take = 200)
        {
            var users = _leaderboardService.GetAllUsers(skip, take);
            return Ok(new
            {
                Users = users,
                TotalUsers = _leaderboardService.GetTotalKnownUsers(),
                Database = new
                {
                    Provider = _leaderboardService.IsMongoDbEnabled ? "MongoDB" : "LocalJson",
                    MongoDbEnabled = _leaderboardService.IsMongoDbEnabled,
                    DatabaseName = _leaderboardService.DatabaseName
                },
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        /// <summary>
        /// GET /api/users/{playerId} - Looks up a single player profile/username by PlayerId.
        /// </summary>
        [HttpGet("{playerId}")]
        public IActionResult GetUserById(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return BadRequest(new { Error = "PlayerId is required" });

            var user = _leaderboardService.GetUserById(playerId);
            if (user == null)
                return NotFound(new { Error = $"User '{playerId}' not found" });

            return Ok(new
            {
                User = user,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
    }
}
