using System.Buffers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace FunkyMakerTransferServer;

/// <summary>
/// HTTP streaming of level files:
/// <list type="bullet">
/// <item><c>PUT /api/transfers/{id}/file</c> — uploader sends the raw file (header X-Transfer-Token).</item>
/// <item><c>GET /api/transfers/{id}/file</c> — downloader receives it (header X-Transfer-Token).</item>
/// </list>
/// Both report byte progress to the registry, which relays it to both players.
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

        string path = store.NewUploadPath(id);
        if (ctx.Request.ContentLength is long announced && announced != ticket.Size)
        {
            await Fail("The upload size does not match the level.");
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
                            throw new InvalidDataException("The file is not a level archive (.fnmm/.zip/.rar).");
                    }

                    total += n;
                    if (total > ticket.Size)
                        throw new InvalidDataException("The upload is larger than the level.");

                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), linked.Token);

                    var fx = new Effects();
                    registry.ReportBytes(id, Protocol.PhaseUpload, total, fx);
                    await runner.RunAsync(fx);
                }
            }

            if (headerLen < header.Length)
                throw new InvalidDataException("The file is too small to be a level.");

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
                    registry.ReportBytes(id, Protocol.PhaseDownload, total, fx);
                    await runner.RunAsync(fx);
                }
                await ctx.Response.Body.FlushAsync(linked.Token);
            }

            var sent = new Effects();
            registry.DownloadSent(id, sent);
            await runner.RunAsync(sent);
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
