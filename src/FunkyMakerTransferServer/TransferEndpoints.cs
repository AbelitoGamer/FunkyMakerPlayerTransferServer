using System.Buffers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace FunkyMakerTransferServer;

/// <summary>
/// HTTP streaming of level files:
/// <list type="bullet">
/// <item><c>PUT /api/transfers/{id}/file</c> — uploader sends the raw file (header X-Transfer-Token).</item>
/// <item><c>GET /api/transfers/{id}/file</c> — downloader receives it (header X-Transfer-Token).</item>
/// </list>
/// Both report byte progress to the registry, which shows it in the room (RoomState level progress).
/// One upload per level; every player who lacks it downloads it with their own token.
/// </summary>
public static class TransferEndpoints
{
    private const int BufferSize = 64 * 1024;

    public static async Task<IResult> UploadAsync(string id, HttpContext ctx, RoomRegistry registry,
        TransferStore store, EffectRunner runner, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("Upload");
        string token = ctx.Request.Headers[Protocol.TokenHeader].ToString();
        var ticket = registry.BeginUpload(id, token, out string error);
        if (ticket == null)
            return Results.Problem(error, statusCode: StatusCodes.Status409Conflict);

        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = ticket.Size;

        var minReqRate = ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (minReqRate != null)
            minReqRate.MinDataRate = null;

        string path = store.NewUploadPath(id);
        // FNF level files have an exact size; packed osu! sets may be anything up to the limit.
        if (ctx.Request.ContentLength is long announced && (ticket.ExactSize ? announced != ticket.Size : announced > ticket.Size))
        {
            await Fail(ticket.ExactSize ? "The upload size does not match the level." : "The packed set is larger than this server allows.");
            return Results.Problem("Content-Length does not match the level size.", statusCode: 400);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, ticket.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            var header = new byte[4];
            int headerLen = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int n;
                while ((n = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token)) > 0)
                {
                    if (headerLen < header.Length)
                    {
                        int take = Math.Min(n, header.Length - headerLen);
                        Array.Copy(buffer, 0, header, headerLen, take);
                        headerLen += take;
                        if (headerLen == header.Length && !LevelFile.HasKnownSignature(header))
                            throw new InvalidDataException("The file is not a level archive (.fnmm/.zip/.rar/.osz).");
                    }

                    total += n;
                    if (total > ticket.Size)
                        throw new InvalidDataException("The upload is larger than the level.");

                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), linked.Token);

                    var fx = new Effects();
                    registry.ReportUploadBytes(id, total, fx);
                    if (fx.HasWork)
                        await runner.RunAsync(fx);
                }
            }

            if (headerLen < header.Length)
                throw new InvalidDataException("The file is too small to be a level.");
            if (ticket.ExactSize && total < ticket.Size)
                throw new InvalidDataException("The upload ended early (connection closed before finishing).");

            string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            var done = new Effects();
            bool ok = registry.CompleteUpload(id, path, hash, total, done, out string completeError);
            await runner.RunAsync(done);
            return ok ? Results.Ok() : Results.Problem(completeError, statusCode: 400);
        }
        catch (InvalidDataException ex)
        {
            await Fail(ex.Message);
            return Results.Problem(ex.Message, statusCode: 400);
        }
        catch (OperationCanceledException)
        {
            await Fail("The upload was interrupted.");
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Upload {Id} failed", id);
            await Fail("The upload failed.");
            return Results.Problem("Upload failed.", statusCode: 400);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        async Task Fail(string reason)
        {
            var fx = new Effects();
            registry.FailUpload(id, reason, path, fx);
            await runner.RunAsync(fx);
        }
    }

    /// <summary>
    /// Chunked upload endpoint (<c>PUT /api/transfers/{id}/chunks?offset=N</c>): each chunk is a small
    /// HTTP request (e.g. 4 MB) so slow connections never hit reverse-proxy request timeouts (Cloudflare/Render
    /// ~100 s limit) and failed chunks can be retried idempotently at the same offset.
    /// </summary>
    public static async Task<IResult> UploadChunkAsync(string id, HttpContext ctx, RoomRegistry registry,
        TransferStore store, EffectRunner runner, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("UploadChunk");
        string token = ctx.Request.Headers[Protocol.TokenHeader].ToString();
        if (!long.TryParse(ctx.Request.Query["offset"], out long offset) || offset < 0)
            return Results.Problem("Missing or invalid offset query parameter.", statusCode: 400);

        long announcedLen = ctx.Request.ContentLength ?? 0;
        const long MaxSingleChunkBytes = 20L * 1024 * 1024;
        if (announcedLen > MaxSingleChunkBytes)
            return Results.Problem("Chunk is too large.", statusCode: 413);

        var ticket = registry.BeginChunkUpload(id, token, offset, announcedLen, store, out string error);
        if (ticket == null)
            return Results.Problem(error, statusCode: StatusCodes.Status409Conflict);

        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxSingleChunkBytes;

        var minReqRate = ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (minReqRate != null)
            minReqRate.MinDataRate = null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, ticket.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        bool finished = false;
        try
        {
            long written = 0;
            var header = new byte[4];
            int headerLen = 0;
            await using (var file = new FileStream(ticket.FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                file.Seek(offset, SeekOrigin.Begin);
                int n;
                while ((n = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token)) > 0)
                {
                    if (offset == 0 && headerLen < header.Length)
                    {
                        int take = Math.Min(n, header.Length - headerLen);
                        Array.Copy(buffer, 0, header, headerLen, take);
                        headerLen += take;
                        if (headerLen == header.Length && !LevelFile.HasKnownSignature(header))
                            throw new InvalidDataException("The file is not a level archive (.fnmm/.zip/.rar/.osz).");
                    }

                    written += n;
                    if (written > MaxSingleChunkBytes || offset + written > ticket.MaxSize)
                        throw new InvalidDataException("The upload is larger than the level.");

                    await file.WriteAsync(buffer.AsMemory(0, n), linked.Token);
                }
            }

            if (offset == 0 && headerLen < header.Length && ticket.CurrentLength < 4)
                throw new InvalidDataException("The file is too small to be a level.");

            if (ctx.Request.ContentLength is long expected && written != expected)
            {
                // Transient proxy truncation on this chunk: release lock without failing the whole transfer so client can retry this offset.
                registry.AbortChunkWrite(id);
                finished = true;
                return Results.Problem("Chunk body ended early; retry this offset.", statusCode: 400);
            }

            var fx = new Effects();
            registry.FinishChunkUpload(id, offset + written, fx);
            finished = true;
            if (fx.HasWork)
                await runner.RunAsync(fx);

            return Results.Ok(new { written = offset + written });
        }
        catch (InvalidDataException ex)
        {
            var fx = new Effects();
            registry.FailUpload(id, ex.Message, ticket.FilePath, fx);
            finished = true;
            await runner.RunAsync(fx);
            return Results.Problem(ex.Message, statusCode: 400);
        }
        catch (OperationCanceledException)
        {
            // Keep transfer alive for client retry unless the transfer itself was cancelled.
            if (!ticket.Token.IsCancellationRequested)
                registry.AbortChunkWrite(id);
            finished = true;
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Chunk upload {Id} at offset {Offset} failed", id, offset);
            registry.AbortChunkWrite(id);
            finished = true;
            return Results.Problem("Chunk upload failed; retry this offset.", statusCode: 500);
        }
        finally
        {
            if (!finished)
                registry.AbortChunkWrite(id);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Finalizes a chunked upload (<c>POST /api/transfers/{id}/complete</c>): streams the assembled file
    /// on disk to compute SHA-256 and exact size, validates against the transfer ticket, and triggers downloads.
    /// </summary>
    public static async Task<IResult> CompleteChunkedUploadAsync(string id, HttpContext ctx, RoomRegistry registry,
        EffectRunner runner, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("CompleteChunkedUpload");
        string token = ctx.Request.Headers[Protocol.TokenHeader].ToString();
        var ticket = registry.BeginCompleteChunkedUpload(id, token, out string error);
        if (ticket == null)
            return Results.Problem(error, statusCode: StatusCodes.Status409Conflict);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, ticket.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        bool completedOrFailed = false;
        try
        {
            long total = 0;
            var header = new byte[4];
            int headerLen = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(ticket.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            {
                if (file.Length > ticket.CurrentLength)
                    total = 0; // hash only up to recorded CurrentLength below
                long remaining = Math.Min(file.Length, ticket.CurrentLength);
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(BufferSize, remaining);
                    int n = await file.ReadAsync(buffer.AsMemory(0, toRead), linked.Token);
                    if (n <= 0) break;
                    if (headerLen < header.Length)
                    {
                        int take = Math.Min(n, header.Length - headerLen);
                        Array.Copy(buffer, 0, header, headerLen, take);
                        headerLen += take;
                    }
                    sha.AppendData(buffer, 0, n);
                    total += n;
                    remaining -= n;
                }
            }

            if (headerLen < header.Length || !LevelFile.HasKnownSignature(header))
                throw new InvalidDataException("The file is not a level archive (.fnmm/.zip/.rar/.osz).");
            if (ticket.ExactSize && total < ticket.MaxSize)
                throw new InvalidDataException("The upload ended early (connection closed before finishing).");
            if (total > ticket.MaxSize)
                throw new InvalidDataException("The upload is larger than the level.");

            string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            var done = new Effects();
            bool ok = registry.CompleteUpload(id, ticket.FilePath, hash, total, done, out string completeError);
            completedOrFailed = true;
            await runner.RunAsync(done);
            return ok ? Results.Ok() : Results.Problem(completeError, statusCode: 400);
        }
        catch (InvalidDataException ex)
        {
            var fx = new Effects();
            registry.FailUpload(id, ex.Message, ticket.FilePath, fx);
            completedOrFailed = true;
            await runner.RunAsync(fx);
            return Results.Problem(ex.Message, statusCode: 400);
        }
        catch (OperationCanceledException)
        {
            if (!ticket.Token.IsCancellationRequested)
                registry.AbortChunkWrite(id);
            completedOrFailed = true;
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Completing chunked upload {Id} failed", id);
            registry.AbortChunkWrite(id);
            completedOrFailed = true;
            return Results.Problem("Failed to finalize upload.", statusCode: 500);
        }
        finally
        {
            if (!completedOrFailed)
                registry.AbortChunkWrite(id);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task DownloadAsync(string id, HttpContext ctx, RoomRegistry registry,
        EffectRunner runner, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("Download");
        string token = ctx.Request.Headers[Protocol.TokenHeader].ToString();
        var ticket = registry.BeginDownload(id, token, out string error);
        if (ticket == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            await ctx.Response.WriteAsync(error);
            return;
        }

        FileStream file;
        try
        {
            file = new FileStream(ticket.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, BufferSize, useAsync: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Download {Id}: file missing", id);
            ctx.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        var minRespRate = ctx.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minRespRate != null)
            minRespRate.MinDataRate = null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, ticket.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (file)
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = ticket.Size;
                long total = 0;
                int n;
                while ((n = await file.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), linked.Token);
                    total += n;
                    var fx = new Effects();
                    registry.ReportDownloadBytes(id, token, total, fx);
                    if (fx.HasWork)
                        await runner.RunAsync(fx);
                }
                await ctx.Response.Body.FlushAsync(linked.Token);
                if (total == ticket.Size)
                    registry.MarkDownloadStreamComplete(id, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away or the transfer was cancelled; the hub/sweeper handles the state.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Download {Id} failed", id);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public static class LevelFile
{
    /// <summary>ZIP ("PK") or RAR ("Rar!") signature — the formats the game can load.</summary>
    public static bool HasKnownSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        ((header[0] == (byte)'P' && header[1] == (byte)'K') ||
         (header[0] == (byte)'R' && header[1] == (byte)'a' && header[2] == (byte)'r' && header[3] == (byte)'!'));
}
