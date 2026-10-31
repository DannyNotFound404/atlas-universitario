using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed class UpdateAdmissionException() : Exception("download_authorization_changed");

internal static class ResumableDownload
{
    public static async Task<string> FetchAsync(HttpClient http, Func<HttpRequestMessage> requestFactory,
        AtlasReleasePackage package, string cache, int kibPerSecond,
        Func<long, long, Task>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cache);
        string destination = DurableFile.Child(cache, package.Sha256.ToLowerInvariant() + ".package");
        string partial = destination + ".partial";
        if (await ValidAsync(destination, package, ct)) return destination;
        if (File.Exists(destination)) File.Delete(destination);
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > package.SizeBytes) { File.Delete(partial); offset = 0; }
                if (offset == package.SizeBytes)
                {
                    if (await ValidAsync(partial, package, ct)) { File.Move(partial, destination, true); return destination; }
                    File.Delete(partial); offset = 0;
                }
                using var request = requestFactory();
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    throw new UpdateAdmissionException();
                response.EnsureSuccessStatusCode();
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK) offset = 0;
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != package.SizeBytes))
                    throw new InvalidDataException("invalid_download_range");
                if (response.Content.Headers.ContentLength is long length && length != package.SizeBytes - offset)
                    throw new InvalidDataException("download_size_mismatch");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using (var target = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[131072]; long total = offset, transferred = 0;
                    var watch = Stopwatch.StartNew(); TimeSpan lastProgress = TimeSpan.MinValue;
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        int read = await source.ReadAsync(buffer, idle.Token);
                        if (read == 0) break;
                        total += read; transferred += read;
                        if (total > package.SizeBytes) throw new InvalidDataException("download_exceeds_signed_size");
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        if (kibPerSecond > 0)
                        {
                            TimeSpan expected = TimeSpan.FromSeconds(transferred / (kibPerSecond * 1024d));
                            if (expected > watch.Elapsed) await Task.Delay(expected - watch.Elapsed, ct);
                        }
                        if (progress is not null && (lastProgress == TimeSpan.MinValue || watch.Elapsed - lastProgress > TimeSpan.FromSeconds(30)))
                        { await progress(total, package.SizeBytes); lastProgress = watch.Elapsed; }
                    }
                    await target.FlushAsync(ct);
                    target.Flush(flushToDisk: true);
                    if (total != package.SizeBytes) throw new IOException("download_incomplete");
                }
                if (!await ValidAsync(partial, package, ct)) { File.Delete(partial); throw new InvalidDataException("download_hash_mismatch"); }
                File.Move(partial, destination, true);
                if (progress is not null) await progress(package.SizeBytes, package.SizeBytes);
                return destination;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (ex is InvalidDataException) throw;
                last = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble() * 2), ct);
            }
        }
        throw new IOException("download_retries_exhausted", last);
    }

    internal static async Task<bool> ValidAsync(string path, AtlasReleasePackage package, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != package.SizeBytes) return false;
        await using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), package.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
