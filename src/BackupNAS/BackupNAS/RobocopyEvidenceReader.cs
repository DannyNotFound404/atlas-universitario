using System.Globalization;
using System.Text.RegularExpressions;
using Miguel.Contracts;

namespace BackupNAS;

// Observes the existing 5.8 copy engine; never controls its command or lifecycle.
internal sealed class RobocopyEvidenceReader
{
    private readonly object gate = new();
    private static readonly Regex Row = new(@"^\s*(Files|Archivos|Ficheros|Bytes)\s*:\s*([\d,.]+)\s+([\d,.]+)\s+([\d,.]+)\s+([\d,.]+)\s+([\d,.]+)\s+([\d,.]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ErrorRecord = new(@"\bERROR\s+(?<code>\d+)\s+\(0x[0-9a-f]+\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private long? total, copied, skipped, failed, bytes;
    private bool retryObserved;
    private readonly HashSet<int> errorCodes = new();

    internal bool RetryObserved { get { lock (gate) return retryObserved; } }
    internal IReadOnlyList<int> ErrorCodes { get { lock (gate) return errorCodes.Order().ToArray(); } }

    internal void Observe(string line)
    {
        lock (gate)
        {
            // Spanish Robocopy prints ERROR as a column heading even on success.
            // Only an actual numbered error record can mean a recovered retry.
            Match error = ErrorRecord.Match(line);
            if (error.Success)
            {
                retryObserved = true;
                if (errorCodes.Count < 20 && int.TryParse(error.Groups["code"].Value, out int code)) errorCodes.Add(code);
            }
            Match match = Row.Match(line);
            if (!match.Success) return;
            var values = new long[6];
            for (int i = 0; i < values.Length; i++)
                if (!long.TryParse(match.Groups[i + 2].Value.Replace(",", "").Replace(".", ""),
                        NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return;
            if (match.Groups[1].Value.Equals("Bytes", StringComparison.OrdinalIgnoreCase)) bytes = values[1];
            else { total = values[0]; copied = values[1]; skipped = values[2]; failed = values[4]; }
        }
    }

    internal BackupFolderEvidence Finish(string name, int exitCode)
    {
        lock (gate)
        {
            string state = exitCode < 0 ? "UNCONFIRMED" : exitCode >= 8 || failed > 0 ? "FAILED" :
                exitCode <= 3 && total.HasValue && copied.HasValue && skipped.HasValue && bytes.HasValue &&
                failed == 0 && total == copied + skipped ? "COMPLETED" : "UNCONFIRMED";
            return new(name, state, exitCode < 0 ? null : exitCode, total, copied, skipped, failed, bytes);
        }
    }
}
