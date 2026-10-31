using System.Diagnostics;
using System.Text;

namespace Atlas.Updater;

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        UpdateLog log,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Windows no pudo iniciar {Path.GetFileName(executable)}.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw new TimeoutException(
                $"{Path.GetFileName(executable)} superó el tiempo permitido.");
        }

        string output = (await standardOutput).Trim();
        string error = (await standardError).Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            log.Write($"Proceso {Path.GetFileName(executable)}: {output}");
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            log.Write($"Proceso {Path.GetFileName(executable)} STDERR: {error}");
        }
        return process.ExitCode;
    }
}
