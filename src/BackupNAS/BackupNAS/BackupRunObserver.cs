using System.Diagnostics;
using Miguel.Contracts;

namespace BackupNAS;

internal sealed class BackupRunObserver
{
    private readonly object gate = new();
    private readonly Dictionary<string, BackupFolderEvidence> folders;
    private readonly Dictionary<string, RobocopyEvidenceReader> readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> warnings = new();
    private readonly Guid eventId = Guid.NewGuid();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly string version, host, share, destination, snapshotMode;
    private readonly string? snapshotWarning;
    private readonly bool hidden, temporary;
    private DateTimeOffset lastSignal;
    private long sequence;
    private bool finished;
    private string? logPath;

    internal BackupRunObserver(IEnumerable<string> names, string version, string host, string share, string destination, bool hidden, bool temporary, string snapshotMode = "live", string? snapshotWarning = null)
    {
        folders = names.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, n => new BackupFolderEvidence(n, "PENDING"), StringComparer.OrdinalIgnoreCase);
        (this.version, this.host, this.share, this.destination, this.hidden, this.temporary) = (version, host, share, destination, hidden, temporary);
        (this.snapshotMode, this.snapshotWarning) = (snapshotMode, snapshotWarning);
    }

    internal BackupRunReport? FinalReport { get; private set; }
    internal bool DeliveredToAgent { get; private set; }

    internal void Start() { lock (gate) Emit(BackupRunStatus.Running, "STARTED"); }
    internal void SetLog(string path) { lock (gate) logPath = path; }
    internal void FolderStarted(string name)
    {
        lock (gate) { readers[name] = new(); folders[name] = new(name, "RUNNING"); }
    }
    internal void Observe(string name, string line)
    {
        lock (gate) { if (readers.TryGetValue(name, out var reader)) reader.Observe(line); }
    }
    internal void FolderFinished(string name, int exitCode)
    {
        lock (gate)
        {
            if (!readers.TryGetValue(name, out var reader)) { folders[name] = new(name, "UNCONFIRMED"); return; }
            folders[name] = reader.Finish(name, exitCode);
            if (reader.RetryObserved && folders[name].State == "COMPLETED")
                warnings.Add(name + ": se recuperó un error o reintento; el resumen final confirma cero archivos fallidos.");
            else if (folders[name].State == "FAILED")
                foreach (int code in reader.ErrorCodes.Take(3))
                {
                    string detail = code switch
                    {
                        2 => "no se encontró un archivo durante la ejecución",
                        3 => "no se encontró una ruta durante la ejecución",
                        5 => "Windows denegó el acceso",
                        32 or 33 => "Windows informó que otro proceso tenía un archivo en uso o bloqueado",
                        53 or 64 or 67 => "Windows informó un problema de acceso a la ruta de red",
                        112 => "Windows informó falta de espacio en el destino",
                        121 => "Windows informó que se agotó el tiempo de espera",
                        206 => "Windows informó que una ruta o un nombre era demasiado largo",
                        _ => "consultar el registro para conocer el detalle de Windows"
                    };
                    string reason = name + ": error de Windows " + code + " · " + detail + ".";
                    warnings.Add(reason.Length <= 512 ? reason : reason[..509] + "...");
                }
        }
    }
    internal void FolderMissing(string name) { lock (gate) folders[name] = new(name, "MISSING"); }
    internal void CleanupWarning(string name, int count)
    {
        lock (gate) warnings.Add(name + ": no se pudieron limpiar " + count + " temporales excluidos; los archivos protegidos se copiaron por separado.");
    }
    internal void Heartbeat()
    {
        lock (gate)
            if (!finished && DateTimeOffset.UtcNow - lastSignal >= TimeSpan.FromSeconds(30)) Emit(BackupRunStatus.Running, "PROCESS_ACTIVE");
    }

    internal BackupRunReport Finish(bool cancelled = false, Exception? error = null, bool skipped = false)
    {
        lock (gate)
        {
            if (FinalReport is not null) return FinalReport;
            var evidence = Evidence("FINISHED", "PROCESS_FINISHED");
            BackupRunStatus status = cancelled ? BackupRunStatus.Cancelled : error is not null ? BackupRunStatus.Failed :
                skipped ? BackupRunStatus.Skipped : BackupTelemetry.IsComplete(evidence) ?
                (warnings.Count == 0 ? BackupRunStatus.Successful : BackupRunStatus.Warning) :
                folders.Values.Any(f => f.State == "COMPLETED") ? BackupRunStatus.Partial :
                folders.Values.Any(f => f.State is "FAILED" or "MISSING") ? BackupRunStatus.Failed : BackupRunStatus.Unconfirmed;
            string reason = cancelled ? "USER_CANCELLED_PROCESS_STOPPED" : skipped ? "USER_CLOSED_WITHOUT_STARTING" :
                error is not null ? "EXECUTION_ERROR_" + error.GetType().Name : "PROCESS_FINISHED";
            FinalReport = Emit(status, reason);
            finished = true;
            return FinalReport;
        }
    }

    internal string UserSummary()
    {
        lock (gate)
        {
            if (FinalReport is not { } report) return "La copia sigue en proceso.";
            if (report.Status == BackupRunStatus.Skipped)
                return "Copia no iniciada. Se cerró la ventana antes de comenzar.";

            var observed = report.Evidence?.Folders ?? Array.Empty<BackupFolderEvidence>();
            var counted = observed.Where(f => f.FilesTotal.HasValue && f.FilesCopied.HasValue &&
                f.FilesUnchanged.HasValue && f.FilesFailed.HasValue && f.BytesCopied.HasValue).ToArray();
            bool acceptable = report.Status is BackupRunStatus.Successful or BackupRunStatus.Warning;
            bool acceptableWithPending = BackupTelemetry.IsAcceptableWithPendingFiles(report);
            long total = counted.Sum(f => f.FilesTotal!.Value);
            string result;
            if (acceptableWithPending)
            {
                long pending = counted.Sum(f => f.FilesFailed!.Value);
                result = "Copia aceptable · " + pending + (pending == 1 ? " archivo pendiente." : " archivos pendientes.") +
                    "\nLa ejecución terminó. Los archivos pendientes se volverán a intentar cuando hagas la siguiente copia.";
                foreach (var folder in counted.Where(f => f.FilesFailed > 0))
                    result += "\n• " + folder.Name + ": " + folder.FilesFailed + (folder.FilesFailed == 1 ? " archivo pendiente." : " archivos pendientes.");
                if (report.Evidence?.Warnings.Any(w => w.Contains(": error de Windows ", StringComparison.Ordinal)) != true)
                    result += "\nEl resumen confirma la cantidad pendiente, pero no especifica el motivo por archivo. El registro conserva el detalle disponible.";
            }
            else if (acceptable)
            {
                result = report.FilesCopied == 0 ? (total == 0 ?
                    "Copia finalizada. No se encontraron archivos para copiar dentro del alcance configurado." :
                    "Copia comprobada. No había cambios que transferir.") : "Copia de seguridad completada.";
                if (report.Status == BackupRunStatus.Warning)
                    result += "\nLa copia es aceptable: los archivos incluidos en el alcance terminaron sin fallos. Hay avisos informativos.";
            }
            else
            {
                result = report.Status switch
                {
                    BackupRunStatus.Cancelled => "Copia cancelada. La ejecución se detuvo antes de terminar; este intento no se registró como copia completada.",
                    BackupRunStatus.Unconfirmed => "Resultado pendiente de confirmación. El proceso terminó sin un resumen suficiente para confirmar la copia.",
                    _ => counted.Any(f => f.FilesFailed > 0) ?
                        "Proceso finalizado con archivos que no se pudieron copiar." :
                        "Proceso finalizado con carpetas pendientes de copia o de confirmación."
                };

                AddFolders("Carpetas finalizadas sin fallos", observed.Where(f => f.State == "COMPLETED").Select(f => f.Name));
                AddFolders("Carpetas con incidencias", observed.Where(f => f.State == "FAILED").Select(f => f.Name));
                AddFolders("Carpetas no encontradas", observed.Where(f => f.State == "MISSING").Select(f => f.Name));
                AddFolders("Carpetas sin resultado confirmado", observed.Where(f => f.State is "PENDING" or "RUNNING" or "UNCONFIRMED").Select(f => f.Name));
            }

            if (counted.Length == 0)
                result += "\n\nNo hay un resumen de archivos que permita mostrar un recuento confirmado.";
            else
            {
                result += counted.Length == observed.Count ? "\n\nRecuento de archivos:" :
                    "\n\nRecuento disponible de " + counted.Length + " de " + observed.Count + " carpetas; no representa las carpetas sin resumen:";
                result += "\nArchivos evaluados por Robocopy: " + total +
                    "\nArchivos copiados: " + counted.Sum(f => f.FilesCopied!.Value) +
                    "\nSin cambios o excluidos por configuración: " + counted.Sum(f => f.FilesUnchanged!.Value) +
                    "\nArchivos pendientes de copiar por error: " + counted.Sum(f => f.FilesFailed!.Value) +
                    "\nDatos transferidos: " + (counted.Sum(f => f.BytesCopied!.Value) / 1048576d).ToString("N2") + " MB";
                if (acceptable && report.FilesSkipped > 0)
                    result += "\nLos archivos sin cambios o excluidos por configuración no indican un fallo de la copia.";
            }
            if (report.Evidence?.Warnings.Count > 0)
            {
                result += (acceptableWithPending ? "\n\nMotivos observados durante la ejecución:\n• " : "\n\nAvisos:\n• ") + string.Join("\n• ", report.Evidence.Warnings.Take(3));
                if (report.Evidence.Warnings.Count > 3)
                    result += "\nOtros avisos: " + (report.Evidence.Warnings.Count - 3) + ".";
            }
            return result + "\n\nDuración: " +
                (report.FinishedAtUtc - report.StartedAtUtc).TotalSeconds.ToString("N1") + " segundos.";

            void AddFolders(string label, IEnumerable<string> names)
            {
                string[] values = names.ToArray();
                if (values.Length > 0) result += "\n\n" + label + ": " + string.Join(", ", values);
            }
        }
    }

    private BackupExecutionEvidence Evidence(string phase, string reason) => new(2, sequence + 1, phase, reason,
        folders.Values.ToArray(), warnings.Take(100).ToArray(), hidden, temporary);

    private BackupRunReport Emit(BackupRunStatus status, string reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = Evidence(status == BackupRunStatus.Running ? "RUNNING" : "FINISHED", reason);
        sequence++; lastSignal = now;
        var report = new BackupRunReport(1, eventId, started, now, status,
            evidence.Folders.Sum(f => f.FilesCopied ?? 0), evidence.Folders.Sum(f => f.FilesUnchanged ?? 0),
            evidence.Folders.Sum(f => f.BytesCopied ?? 0), evidence.Folders.Where(f => f.State == "MISSING").Select(f => f.Name).ToArray(),
            evidence.Folders.Where(f => f.State == "FAILED").Select(f => f.Name).ToArray(), version,
            logPath is null ? null : Path.GetFileName(logPath), host, share, destination, snapshotMode, snapshotWarning, evidence);
        try { DeliveredToAgent = BackupReportDelivery.Submit(report); }
        catch (Exception ex)
        {
            DeliveredToAgent = false;
            Trace.WriteLine("ATLAS: no se pudo conservar el informe (" + ex.GetType().Name + ").");
            try { if (logPath is not null) File.AppendAllText(logPath, "\n[ATLAS] Informe pendiente: " + ex.GetType().Name + "\n"); } catch { }
        }
        return report;
    }
}
