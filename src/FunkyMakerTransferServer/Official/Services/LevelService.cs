using FunkyMakerServer.Models;

namespace FunkyMakerServer.Services
{
    /// <summary>
    /// Service for managing official levels.
    /// </summary>
    public class LevelService
    {
        private readonly HashService _hashService;
        private readonly string _levelsPath;
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, (long Size, DateTime LastWriteUtc, OfficialLevelInfo Info)> _levelCache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _idToPathCache = new(StringComparer.OrdinalIgnoreCase);
        private List<OfficialLevelInfo> _cachedList = new();

        public LevelService(HashService hashService, IWebHostEnvironment env)
        {
            _hashService = hashService;
            _levelsPath = Path.Combine(env.ContentRootPath, "verified_levels");
            
            Console.WriteLine($"[LevelService] Initializing...");
            Console.WriteLine($"[LevelService] ContentRootPath: {env.ContentRootPath}");
            Console.WriteLine($"[LevelService] Looking for levels at: {_levelsPath}");
            
            // Ensure folder exists
            if (!Directory.Exists(_levelsPath))
            {
                Console.WriteLine($"[LevelService] ⚠️ verified_levels folder NOT FOUND - Creating it now");
                Directory.CreateDirectory(_levelsPath);
            }
            else
            {
                Console.WriteLine($"[LevelService] ✓ verified_levels folder exists");
            }
            
            // Log initial scan
            var initialFiles = Directory.GetFiles(_levelsPath, "*.FNMM", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"[LevelService] Initial scan: {initialFiles.Length} .FNMM files found");
            
            if (initialFiles.Length == 0)
            {
                Console.WriteLine($"[LevelService] ⚠️ WARNING: No .FNMM files found in verified_levels/");
                Console.WriteLine($"[LevelService] This means clients will see 0 official levels available");
                Console.WriteLine($"[LevelService] To fix: Add .FNMM files to verified_levels/ folder and redeploy");
            }
            else
            {
                foreach (var file in initialFiles)
                {
                    Console.WriteLine($"[LevelService]   - {Path.GetFileName(file)}");
                }
            }
        }

        /// <summary>
        /// Scan verified_levels folder and return list of official levels (cached by file size and mtime).
        /// </summary>
        public List<OfficialLevelInfo> ScanVerifiedLevels()
        {
            lock (_cacheLock)
            {
                try
                {
                    if (!Directory.Exists(_levelsPath))
                    {
                        _levelCache.Clear();
                        _idToPathCache.Clear();
                        _cachedList = new List<OfficialLevelInfo>();
                        return new List<OfficialLevelInfo>();
                    }

                    var fnmmFiles = Directory.GetFiles(_levelsPath, "*.FNMM", SearchOption.TopDirectoryOnly);
                    var currentPaths = new HashSet<string>(fnmmFiles, StringComparer.OrdinalIgnoreCase);
                    bool changed = fnmmFiles.Length != _levelCache.Count;

                    var levels = new List<OfficialLevelInfo>(fnmmFiles.Length);
                    var idToPath = new Dictionary<string, string>(fnmmFiles.Length, StringComparer.OrdinalIgnoreCase);

                    foreach (var filePath in fnmmFiles)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(filePath);
                            if (_levelCache.TryGetValue(filePath, out var cached) &&
                                cached.Size == fileInfo.Length &&
                                cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                            {
                                levels.Add(cached.Info);
                                idToPath[cached.Info.Id] = filePath;
                                continue;
                            }

                            changed = true;
                            var levelInfo = CreateLevelInfo(filePath, fileInfo);
                            if (levelInfo != null)
                            {
                                _levelCache[filePath] = (fileInfo.Length, fileInfo.LastWriteTimeUtc, levelInfo);
                                levels.Add(levelInfo);
                                idToPath[levelInfo.Id] = filePath;
                                Console.WriteLine($"[LevelService] Indexed: {levelInfo.Name} (Hash: {levelInfo.Hash[..8]}...)");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[LevelService] Error processing {filePath}: {ex.Message}");
                        }
                    }

                    // Remove deleted files from cache
                    foreach (var staleKey in _levelCache.Keys.Where(k => !currentPaths.Contains(k)).ToList())
                    {
                        _levelCache.Remove(staleKey);
                        changed = true;
                    }

                    if (changed || _cachedList.Count == 0 && levels.Count > 0)
                    {
                        _cachedList = levels;
                        _idToPathCache = idToPath;
                    }

                    return new List<OfficialLevelInfo>(_cachedList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LevelService] Error scanning: {ex.Message}");
                    return new List<OfficialLevelInfo>();
                }
            }
        }

        /// <summary>
        /// Get file path for a level by ID.
        /// </summary>
        public string? GetLevelFilePath(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId) || !Directory.Exists(_levelsPath))
                return null;

            string normalizedId = levelId.ToLowerInvariant();
            lock (_cacheLock)
            {
                if (_idToPathCache.TryGetValue(normalizedId, out string? cachedPath) && File.Exists(cachedPath))
                    return cachedPath;
            }

            ScanVerifiedLevels();
            lock (_cacheLock)
            {
                return _idToPathCache.TryGetValue(normalizedId, out string? path) && File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// Create OfficialLevelInfo from file path.
        /// </summary>
        private OfficialLevelInfo? CreateLevelInfo(string filePath, FileInfo? existingInfo = null)
        {
            var fileInfo = existingInfo ?? new FileInfo(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string hash = _hashService.CalculateFileHash(filePath);

            if (string.IsNullOrEmpty(hash))
            {
                Console.WriteLine($"[LevelService] Skipping {fileName} - hash calculation failed");
                return null;
            }

            return new OfficialLevelInfo
            {
                Id = GenerateLevelId(fileName),
                Name = fileName,
                Hash = hash,
                Filename = fileInfo.Name,
                FileSize = fileInfo.Length,
                Difficulty = "Medium", // Default - could be enhanced with metadata
                Description = $"Official level: {fileName}"
            };
        }

        /// <summary>
        /// Generate level ID from filename.
        /// </summary>
        private string GenerateLevelId(string fileName)
        {
            return fileName.ToLowerInvariant().Replace(" ", "_");
        }
    }
}
