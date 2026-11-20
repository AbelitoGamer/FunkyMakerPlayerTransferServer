using Microsoft.AspNetCore.Mvc;
using FunkyMakerServer.Services;

namespace FunkyMakerServer.Controllers
{
    /// <summary>
    /// Server information and health check endpoint.
    /// </summary>
    [ApiController]
    [Route("api/server")]
    public class ServerInfoController : ControllerBase
    {
        private readonly LevelService _levelService;
        private readonly LeaderboardService _leaderboardService;
        private const string SERVER_VERSION = "0.4.6-alpha";

        public ServerInfoController(LevelService levelService, LeaderboardService leaderboardService)
        {
            _levelService = levelService;
            _leaderboardService = leaderboardService;
        }

        /// <summary>
        /// GET /api/server/info - Returns server information and status.
        /// </summary>
        [HttpGet("info")]
        public IActionResult GetServerInfo()
        {
            var levels = _levelService.ScanVerifiedLevels();
            
            var serverInfo = new
            {
                Version = SERVER_VERSION,
                Status = "Online",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Architecture = new
                {
                    Type = "Reorganized",
                    Features = new[]
                    {
                        "Dependency Injection",
                        "Controller-based API",
                        "Service Layer",
                        "Real-time Level Scanning"
                    }
                },
                Levels = new
                {
                    Count = levels.Count,
                    Names = levels.Select(l => l.Name).ToList(),
                    TotalSize = levels.Sum(l => l.FileSize)
                },
                Database = new
                {
                    Provider = _leaderboardService.IsMongoDbEnabled ? "MongoDB" : "LocalJson",
                    MongoDbEnabled = _leaderboardService.IsMongoDbEnabled,
                    DatabaseName = _leaderboardService.DatabaseName,
                    Players = _leaderboardService.GetTotalPlayers(),
                    Matches = _leaderboardService.GetTotalMatches(),
                    LastError = _leaderboardService.LastMongoError
                },
                Endpoints = new[]
                {
                    "GET /api/server/info",
                    "GET /api/leaderboard",
                    "GET /api/users",
                    "GET /api/users/{playerId}",
                    "GET /api/verified_levels",
                    "GET /api/verified_levels/{id}/download",
                    "WS /multiplayer (SignalR)"
                }
            };

            return Ok(serverInfo);
        }

        /// <summary>
        /// GET /api/server/health - Simple health check endpoint.
        /// </summary>
        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new
            {
                Status = "Healthy",
                Version = SERVER_VERSION,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
    }
}
