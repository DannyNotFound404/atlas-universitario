using System.Text.Json;
using System.Net;
using System.Security.Cryptography;
using Miguel.Contracts;

namespace Miguel.Agent.Outbox;

public sealed class OutboxStore(AgentPaths paths)
{
    private const long MaximumEventBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task EnqueueAsync<T>(
        string eventType,
        T payload,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        CleanupStaleTemporaryFiles();
        Guid eventId = Guid.NewGuid();
        OutboxEnvelope envelope = new(
            ApiContract.SchemaVersion,
            eventId,
            eventType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, JsonOptions));

        string fileName = $"{envelope.CreatedAtUtc.UtcTicks:D19}-{eventId:N}.json";
        string destination = Path.Combine(paths.OutboxDirectory, fileName);
        string temporary = destination + ".tmp";

        await using (FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, destination);
    }

    public int Count()
    {
        paths.EnsureDirectories();
        return Directory.EnumerateFiles(paths.OutboxDirectory, "*.json").Count();
    }

    public int QuarantinedCount() => Directory.Exists(RejectedDirectory)
        ? Directory.EnumerateFiles(RejectedDirectory, "*.event.json").Count() : 0;

    private string RejectedDirectory => Path.Combine(paths.OutboxDirectory, "Rejected");

    public AgentDeliveryHealth DeliveryHealth()
    {
        int count = QuarantinedCount();
        DateTimeOffset? latest = null;
        string? code = null;
        if (Directory.Exists(RejectedDirectory))
        foreach (string receipt in Directory.EnumerateFiles(RejectedDirectory, "*.receipt.json"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(receipt));
                DateTimeOffset at = document.RootElement.GetProperty("quarantinedAtUtc").GetDateTimeOffset();
                string? reason = document.RootElement.GetProperty("reason").GetString();
                bool knownReason = reason is "invalid_event_size" or "invalid_json" or "invalid_event_contract" or
                    "payload_rejected_http_400" or "payload_rejected_http_413" or "payload_rejected_http_415" or "payload_rejected_http_422";
                if (knownReason && (latest is null || at > latest)) { latest = at; code = reason; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            { /* The original event still contributes to the count if its receipt is unreadable. */ }
        }
        return new AgentDeliveryHealth(count, latest, code);
    }

    public async Task FlushAsync(
        Func<OutboxEnvelope, CancellationToken, Task> sender,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        try
        {
        foreach (string file in Directory
                     .EnumerateFiles(paths.OutboxDirectory, "*.json")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            OutboxEnvelope? envelope;
            try
            {
                if (new FileInfo(file).Length is <= 0 or > MaximumEventBytes)
                { Quarantine(file, "invalid_event_size"); continue; }
                await using FileStream stream = File.OpenRead(file);
                envelope = await JsonSerializer.DeserializeAsync<OutboxEnvelope>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException)
            { Quarantine(file, "invalid_json"); continue; }

            if (envelope is null || envelope.SchemaVersion != ApiContract.SchemaVersion ||
                envelope.EventId == Guid.Empty || envelope.CreatedAtUtc == default ||
                envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                envelope.EventType is not (OutboxEventTypes.Inventory or OutboxEventTypes.BackupRun or
                    OutboxEventTypes.SecurityPosture or OutboxEventTypes.SecurityEvents))
            {
                Quarantine(file, "invalid_event_contract");
                continue;
            }

            try { await sender(envelope, cancellationToken); }
            catch (HttpRequestException error) when (IsPermanentPayloadRejection(error.StatusCode))
            { Quarantine(file, "payload_rejected_http_" + (int)error.StatusCode!.Value); continue; }
            File.Delete(file);
        }
        }
        finally { WriteHealth(); }
    }

    private static bool IsPermanentPayloadRejection(HttpStatusCode? code) =>
        code is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity;

    private void Quarantine(string file, string reason)
    {
        // The original event is retained, never treated as acknowledged or deleted.
        Directory.CreateDirectory(RejectedDirectory);
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(RejectedDirectory, id + ".event.json");
        string hash;
        using (var source = File.OpenRead(file)) hash = Convert.ToHexStringLower(SHA256.HashData(source));
        File.Move(file, destination);
        WriteDurable(Path.Combine(RejectedDirectory, id + ".receipt.json"), new
        {
            schemaVersion = 1, quarantinedAtUtc = DateTimeOffset.UtcNow, reason,
            originalFileName = Path.GetFileName(file), payloadFileName = Path.GetFileName(destination), sha256 = hash
        });
    }

    private void WriteHealth()
    {
        try
        {
            WriteDurable(Path.Combine(paths.RootDirectory, "Health", "outbox-health.json"), new
            {
                schemaVersion = 1, checkedAtUtc = DateTimeOffset.UtcNow,
                queuedEvents = Count(), quarantinedEvents = QuarantinedCount(),
                status = QuarantinedCount() > 0 ? "TECHNICAL_REVIEW_REQUIRED" : "NO_QUARANTINED_EVENTS"
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine("ATLAS: diagnóstico de cola no disponible (" + error.GetType().Name + ")."); }
    }

    private static void WriteDurable<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".next";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, JsonOptions); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private void CleanupStaleTemporaryFiles()
    {
        DateTime staleBeforeUtc = DateTime.UtcNow.AddMinutes(-15);
        foreach (string temporaryFile in Directory.EnumerateFiles(
                     paths.OutboxDirectory,
                     "*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(temporaryFile) < staleBeforeUtc)
                {
                    File.Delete(temporaryFile);
                }
            }
            catch (IOException)
            {
                // Otro proceso puede estar terminando una escritura legítima.
            }
            catch (UnauthorizedAccessException)
            {
                // La cola principal sigue funcionando y se reintentará después.
            }
        }
    }
}
