using Sayserne.Api.Data;

namespace Sayserne.Api.Media;

public sealed record FileRecord(
    string Id, long OwnerId, string Purpose, string MediaKind, string? OriginalName, string? ContentType, long OriginalBytes, long? StoredBytes, int? Width, int? Height, int? DurationMs,
    string Status, string? FailureCode, string? Provider, string? StorageKey, string? IncomingKey, long? RoomId, bool IsDeleted, DateTime CreatedAt, DateTime? ReadyAt);

public interface IFileRepository
{
    Task<FileRecord> CreateAsync(string id, long ownerId, string purpose, string kind, string? originalName, long originalBytes, long? roomId, string provider, string incomingKey, CancellationToken ct = default);
    Task<FileRecord> MarkReadyAsync(string id, string contentType, long storedBytes, int? width, int? height, int? durationMs, string provider, string storageKey, CancellationToken ct = default);
    Task<FileRecord> MarkFailedAsync(string id, string code, CancellationToken ct = default);
    Task<FileRecord?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>null = no such file; otherwise whether this viewer (null = anonymous) may see it.</summary>
    Task<bool?> ViewerAllowedAsync(string id, long? viewerId, CancellationToken ct = default);
}

public sealed class FileRepository(IStoredProcedureExecutor db) : IFileRepository
{
    private static FileRecord Map(System.Data.Common.DbDataReader r) => new(
        r.Str("id"), r.Long("owner_id"), r.Str("purpose"), r.Str("media_kind"), r.StrOrNull("original_name"), r.StrOrNull("content_type"), r.Long("original_bytes"), r.LongOrNull("stored_bytes"),
        r.IntOrNull("width"), r.IntOrNull("height"), r.IntOrNull("duration_ms"), r.Str("status"), r.StrOrNull("failure_code"), r.StrOrNull("provider"), r.StrOrNull("storage_key"),
        r.StrOrNull("incoming_key"), r.LongOrNull("room_id"), r.Bool("is_deleted"), r.Utc("created_at"), r.UtcOrNull("ready_at"));

    public async Task<FileRecord> CreateAsync(string id, long ownerId, string purpose, string kind, string? originalName, long originalBytes, long? roomId, string provider, string incomingKey, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_files_create", new ProcParams().Add("p_id", id).Add("p_owner_id", ownerId).Add("p_purpose", purpose).Add("p_kind", kind)
            .Add("p_original_name", originalName).Add("p_original_bytes", originalBytes).Add("p_room_id", roomId).Add("p_provider", provider).Add("p_incoming_key", incomingKey), Map, ct))!;

    public async Task<FileRecord> MarkReadyAsync(string id, string contentType, long storedBytes, int? width, int? height, int? durationMs, string provider, string storageKey, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_files_mark_ready", new ProcParams().Add("p_id", id).Add("p_content_type", contentType).Add("p_stored_bytes", storedBytes).Add("p_width", width)
            .Add("p_height", height).Add("p_duration_ms", durationMs).Add("p_provider", provider).Add("p_storage_key", storageKey), Map, ct))!;

    public async Task<FileRecord> MarkFailedAsync(string id, string code, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_files_mark_failed", new ProcParams().Add("p_id", id).Add("p_code", code), Map, ct))!;

    public Task<FileRecord?> GetAsync(string id, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_files_get", new ProcParams().Add("p_id", id), Map, ct);

    public async Task<bool?> ViewerAllowedAsync(string id, long? viewerId, CancellationToken ct = default)
    {
        var rows = await db.QueryAsync("sp_files_viewer_allowed", new ProcParams().Add("p_id", id).Add("p_viewer_id", viewerId), r => r.Bool("allowed"), ct);
        return rows.Count == 0 ? null : rows[0];
    }
}

/// <summary>Takes a freshly uploaded file from "incoming" to "ready": normalise it, store the result, record it, remove the raw upload.</summary>
public sealed class MediaIngestService(IFileRepository files, IStorageRouter storage, IMediaProcessor processor, Platform.ISettingsService settings, TimeProvider clock, Microsoft.Extensions.Options.IOptions<MediaOptions> options)
{
    public MediaLimits Limits() => new(settings.GetInt("Media.MaxImageMB") * 1024L * 1024L, settings.GetInt("Media.MaxVideoMB") * 1024L * 1024L,
        settings.GetInt("Media.ImageMaxDimension"), settings.GetInt("Media.VideoMaxHeight"), settings.GetInt("Media.VideoMaxSeconds"));

    /// <exception cref="MediaException">The file itself is unacceptable (the caller marks it failed).</exception>
    public async Task<FileRecord> ProcessAsync(FileRecord file, CancellationToken ct = default)
    {
        if (file.IncomingKey is null || file.Provider is null) throw new MediaException("UNSUPPORTED_FILE");
        var temp = Path.Combine(options.Value.TempPath ?? Path.GetTempPath(), "sayserne-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var input = Path.Combine(temp, "input" + Path.GetExtension(file.IncomingKey));
            await using (var source = await storage.For(file.Provider).OpenReadAsync(file.IncomingKey, ct))
            await using (var target = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(target, ct);

            var limits = Limits();
            var result = file.MediaKind == "video" ? await processor.ProcessVideoAsync(input, temp, limits, ct) : await processor.ProcessImageAsync(input, temp, limits, ct);
            var writer = storage.ForWrite();
            var key = StorageKeys.Final(file.Id, result.Extension, clock.GetUtcNow().UtcDateTime);
            await using (var data = File.OpenRead(result.Path)) await writer.PutAsync(key, data, result.ContentType, ct);
            var ready = await files.MarkReadyAsync(file.Id, result.ContentType, result.Bytes, result.Width, result.Height, result.DurationMs, writer.Name, key, ct);
            try { await storage.For(file.Provider).DeleteAsync(file.IncomingKey, ct); } catch (StorageException) { /* the raw upload is swept later; it is not served */ }
            return ready;
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* temp cleanup is best-effort */ } }
    }

    public async Task DiscardIncomingAsync(FileRecord file)
    {
        if (file.IncomingKey is null || file.Provider is null) return;
        try { await storage.For(file.Provider).DeleteAsync(file.IncomingKey); } catch (Exception ex) when (ex is StorageException or AppException) { /* best-effort */ }
    }
}
