using System.Security.Cryptography;

namespace FunkyMakerServer.Services
{
    /// <summary>
    /// Service for calculating SHA256 hashes of files.
    /// </summary>
    public class HashService
    {
        /// <summary>
        /// Calculate SHA256 hash of a file.
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <returns>Hex string of the hash, or empty string on error</returns>
        public string CalculateFileHash(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"[HashService] File not found: {filePath}");
                    return "";
                }

                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HashService] Error calculating hash for {filePath}: {ex.Message}");
                return "";
            }
        }
    }
}
