using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Sayserne.Api.Auth;
using Sayserne.Api.Jobs;
using Sayserne.Api.Media;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>Uploads and downloads of images and videos. The uploaded bytes are never served: only the normalised result is.</summary>
public static class FileEndpoints
{
    /// <summary>What each purpose accepts. Ad images are uploaded by staff through the back office, not here.</summary>
    private sealed record Purpose(bool Image, bool Video, bool NeedsRoom, bool Public, bool StaffOnly = false);
    private static readonly Dictionary<string, Purpose> Purposes = new(StringComparer.Ordinal)
    {
        ["profile_photo"] = new(Image: true, Video: false, NeedsRoom: false, Public: true),
        ["banner_photo"] = new(Image: true, Video: false, NeedsRoom: false, Public: true),
        ["chat_media"] = new(Image: true, Video: true, NeedsRoom: true, Public: false),
        ["ad_image"] = new(Image: true, Video: false, NeedsRoom: false, Public: true, StaffOnly: true),   // uploaded through the back office only
    };

    private static string Url(string id) => $"/api/files/{id}";
    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/files");
        g.MapPost("", Upload).RequireAuthorization().RequireRateLimiting(MarketplaceEndpoints.WriteLimiter);
        g.MapGet("/{id}", Download);
        g.MapGet("/{id}/status", Status).RequireAuthorization();
    }

    private static Task<IResult> Upload(HttpContext ctx, string? purpose, long? roomId, IFileRepository files, IStorageRouter storage, MediaIngestService ingest, IJobQueue jobs,
        ISettingsService settings, TimeProvider clock) => UploadCore(ctx, purpose, roomId, files, storage, ingest, jobs, settings, clock, allowStaffOnly: false);

    /// <summary>The upload pipeline shared by the public endpoint and the back office. Staff-only purposes are refused unless the caller is the back office.</summary>
    internal static async Task<IResult> UploadCore(HttpContext ctx, string? purpose, long? roomId, IFileRepository files, IStorageRouter storage, MediaIngestService ingest, IJobQueue jobs,
        ISettingsService settings, TimeProvider clock, bool allowStaffOnly)
    {
        if (purpose is null || !Purposes.TryGetValue(purpose, out var rule) || (rule.StaffOnly && !allowStaffOnly)) throw Invalid("purpose", "Unknown purpose.");
        if (rule.NeedsRoom && roomId is null) throw Invalid("roomId", "A conversation is required.");
        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var mediaType) || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value))
            throw Invalid("file", "Send the file as multipart/form-data.");

        var maxUpload = settings.GetInt("Media.MaxUploadMB") * 1024L * 1024L;
        // Allow up to twice the limit on the wire: an oversized upload is read and discarded (not stored) so the browser receives a clean "too large"
        // answer instead of a dropped connection. Anything far beyond that is cut off by the server.
        var hardCeiling = maxUpload * 2 + 2 * 1024 * 1024;
        if (ctx.Request.ContentLength > hardCeiling) throw new AppException("FILE_TOO_LARGE", 413);
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = hardCeiling;

        var tempDir = Path.Combine(Path.GetTempPath(), "sayserne-uploads");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".upload");
        string? originalName = null;
        long size = 0;
        var tooLarge = false;
        byte[] head = [];
        try
        {
            var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!, ctx.Request.Body) { BodyLengthLimit = hardCeiling };
            MultipartSection? section;
            do { section = await reader.ReadNextSectionAsync(ctx.RequestAborted); }
            while (section is not null && !(ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) && cd.Name.Value == "file" && cd.FileName.HasValue));
            if (section is null) throw Invalid("file", "No file was sent.");
            originalName = Path.GetFileName(ContentDispositionHeaderValue.Parse(section.ContentDisposition).FileName.Value?.Trim('"'));

            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    if (size == 0) head = buffer.AsSpan(0, Math.Min(read, MediaSniffer.HeaderBytes)).ToArray();
                    size += read;
                    if (size > maxUpload) { tooLarge = true; continue; }   // keep draining, store nothing more
                    await output.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                }
            }
            if (tooLarge) throw new AppException("FILE_TOO_LARGE", 413);
            if (size == 0) throw Invalid("file", "The file is empty.");

            var sniffed = MediaSniffer.Detect(head) ?? throw new AppException("UNSUPPORTED_FILE", 400);
            if (sniffed.Kind == MediaKind.Image && !rule.Image || sniffed.Kind == MediaKind.Video && !rule.Video) throw new AppException("UNSUPPORTED_FILE", 400);
            if (sniffed.Kind == MediaKind.Video && !settings.GetBool("Media.VideoEnabled")) throw new AppException("VIDEO_DISABLED", 400);

            var id = Guid.NewGuid().ToString("N");
            var writer = storage.ForWrite();
            var incoming = StorageKeys.Incoming(id, sniffed.Extension);
            await using (var data = File.OpenRead(temp)) await writer.PutAsync(incoming, data, "application/octet-stream", ctx.RequestAborted);
            FileRecord file;
            try { file = await files.CreateAsync(id, ctx.UserId(), purpose, sniffed.Kind == MediaKind.Video ? "video" : "image", originalName, size, rule.NeedsRoom ? roomId : null, writer.Name, incoming, ctx.RequestAborted); }
            catch { try { await writer.DeleteAsync(incoming); } catch (StorageException) { } throw; }

            if (sniffed.Kind == MediaKind.Video)
            {
                await jobs.EnqueueAsync(MediaProcessJobHandler.JobType, new { fileId = id }, dedupeKey: "media:" + id, ct: ctx.RequestAborted);
                return Results.Json(new { id, status = "processing", kind = "video", url = Url(id) }, statusCode: StatusCodes.Status202Accepted);
            }
            try
            {
                var ready = await ingest.ProcessAsync(file, ctx.RequestAborted);
                return Results.Json(new { id, status = "ready", kind = "image", url = Url(id), width = ready.Width, height = ready.Height, bytes = ready.StoredBytes }, statusCode: StatusCodes.Status201Created);
            }
            catch (MediaException ex)
            {
                await files.MarkFailedAsync(id, ex.Code);
                await ingest.DiscardIncomingAsync(file);
                throw new AppException(ex.Code, 400);
            }
        }
        catch (BadHttpRequestException) { throw new AppException("FILE_TOO_LARGE", 413); }
        catch (InvalidDataException) { throw new AppException("FILE_TOO_LARGE", 413); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* swept with the temp folder */ } }
    }

    private static async Task<IResult> Download(HttpContext ctx, string id, IFileRepository files, IStorageRouter storage)
    {
        if (id.Length != 32) throw new AppException("FILE_NOT_FOUND", 404);
        var viewer = AuthSession.CurrentUserId(ctx.User);
        // not allowed and not found look the same, so ids cannot be probed
        if (await files.ViewerAllowedAsync(id, viewer, ctx.RequestAborted) is not true) throw new AppException("FILE_NOT_FOUND", 404);
        var file = await files.GetAsync(id, ctx.RequestAborted);
        if (file is null || file.Status != "ready" || file.Provider is null || file.StorageKey is null || file.ContentType is null) throw new AppException("FILE_NOT_FOUND", 404);

        Stream stream;
        try { stream = await storage.For(file.Provider).OpenReadAsync(file.StorageKey, ctx.RequestAborted); }
        catch (StorageException) { throw new AppException("FILE_NOT_FOUND", 404); }

        var isPublic = Purposes.TryGetValue(file.Purpose, out var rule) && rule.Public;
        ctx.Response.Headers.CacheControl = isPublic ? "public, max-age=86400, immutable" : "private, max-age=3600";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        ctx.Response.Headers.ContentDisposition = "inline";
        return Results.Stream(stream, file.ContentType, lastModified: file.ReadyAt is { } t ? new DateTimeOffset(t, TimeSpan.Zero) : null,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Id}\""), enableRangeProcessing: stream.CanSeek);
    }

    private static async Task<IResult> Status(HttpContext ctx, string id, IFileRepository files)
    {
        var file = id.Length == 32 ? await files.GetAsync(id, ctx.RequestAborted) : null;
        if (file is null || file.OwnerId != ctx.UserId()) throw new AppException("FILE_NOT_FOUND", 404);
        return Results.Ok(new { id = file.Id, status = file.Status, kind = file.MediaKind, failureCode = file.FailureCode, url = file.Status == "ready" ? Url(file.Id) : null, width = file.Width, height = file.Height });
    }
}
