using Microsoft.AspNetCore.Mvc;
using FunkyMakerServer.Models;
using FunkyMakerServer.Services;

namespace FunkyMakerServer.Controllers
{
    /// <summary>
    /// API Controller for official level management.
    /// </summary>
    [ApiController]
    [Route("api/verified_levels")]
    public class LevelsController : ControllerBase
    {
        private readonly LevelService _levelService;

        public LevelsController(LevelService levelService)
        {
            _levelService = levelService;
        }

        /// <summary>
        /// GET /api/verified_levels - Returns list of all official levels.
        /// </summary>
        [HttpGet]
        public IActionResult GetLevels()
        {
            var levels = _levelService.ScanVerifiedLevels();

            var response = new LevelListPacket
            {
                Levels = levels,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            return Ok(response);
        }

        /// <summary>
        /// GET /api/verified_levels/{id}/download - Downloads a specific level file.
        /// </summary>
        [HttpGet("{id}/download")]
        public IActionResult DownloadLevel(string id)
        {
            try
            {
                string? filePath = _levelService.GetLevelFilePath(id);

                if (filePath == null)
                {
                    Console.WriteLine($"[LevelsController] Level not found: {id}");
                    return NotFound(new { error = $"Level '{id}' not found in verified_levels/" });
                }

                Console.WriteLine($"[LevelsController] Serving: {Path.GetFileName(filePath)}");

                return PhysicalFile(filePath, "application/octet-stream", Path.GetFileName(filePath), enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelsController] Error serving level {id}: {ex.Message}");
                return Problem($"Error serving level: {ex.Message}");
            }
        }
    }
}
