using System.Diagnostics;
using System.Globalization;
using System.Text;
using Miguel.Contracts;

namespace Atlas.Nas.Provisioner;

public sealed class SystemdMountManager
{
    public const string MountRoot = "/srv/atlas/nas";
    public const string CredentialsRoot = "/etc/atlas/nas-credentials";
    public const string UnitRoot = "/etc/systemd/system";

    public async Task<NasProvisioningResponse> ProvisionAsync(
        NasProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        string mountPath = Path.Combine(MountRoot, request.Code);
        string mountUnit = (await RunAsync(
            "/usr/bin/systemd-escape",
            ["--path", "--suffix=mount", mountPath],
            cancellationToken)).StandardOutput.Trim();
        if (mountUnit.Length == 0 || mountUnit.Contains('/') ||
            !mountUnit.EndsWith(".mount", StringComparison.Ordinal))
        {
            return Failure(request, mountPath, "UNIT_NAME_FAILED", null);
        }
        string automountUnit =
            mountUnit[..^".mount".Length] + ".automount";

        Directory.CreateDirectory(MountRoot);
        Directory.CreateDirectory(CredentialsRoot);
        Directory.CreateDirectory(mountPath);
        SetMode(MountRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
        SetMode(CredentialsRoot, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SetMode(mountPath, UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await RunAsync(
            "/usr/bin/chown",
            ["root:miguel", mountPath],
            cancellationToken);

        await RunAllowFailureAsync(
            "/usr/bin/systemctl",
            ["stop", automountUnit],
            cancellationToken);
        await RunAllowFailureAsync(
            "/usr/bin/systemctl",
            ["stop", mountUnit],
            cancellationToken);

        string? credentialsPath = null;
        string what;
        string type;
        string options;
        if (request.Protocol == NasMountProtocols.Cifs)
        {
            if (!File.Exists("/usr/sbin/mount.cifs") &&
                !File.Exists("/usr/bin/mount.cifs"))
            {
                return Failure(
                    request,
                    mountPath,
                    "CIFS_UTILS_MISSING",
                    "Instale cifs-utils en el servidor.");
            }
            credentialsPath = Path.Combine(
                CredentialsRoot,
                request.Code + ".credentials");
            string credentialText = BuildCredentialFile(request);
            WriteAtomic(
                credentialsPath,
                credentialText,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

            string uid = (await RunAsync(
                "/usr/bin/id",
                ["-u", "miguel"],
                cancellationToken)).StandardOutput.Trim();
            string gid = (await RunAsync(
                "/usr/bin/id",
                ["-g", "miguel"],
                cancellationToken)).StandardOutput.Trim();
            string security = GetSmbSecurityOptions(request.SmbSecurityProfile!);
            string port = request.SmbPort == 445
                ? ""
                : $",port={request.SmbPort.ToString(CultureInfo.InvariantCulture)}";
            what = $"//{request.Host}/{request.ShareName}";
            type = "cifs";
            options = $"credentials={credentialsPath},uid={uid},gid={gid}," +
                $"file_mode=0440,dir_mode=0550,{security}{port},ro,nosuid," +
                "nodev,noexec,serverino,_netdev";
        }
        else
        {
            credentialsPath = Path.Combine(
                CredentialsRoot,
                request.Code + ".credentials");
            if (File.Exists(credentialsPath))
            {
                File.Delete(credentialsPath);
            }
            what = $"{request.Host}:{request.NfsExport}";
            type = "nfs";
            options = "ro,nosuid,nodev,noexec,vers=3,proto=tcp," +
                "timeo=50,retrans=2,soft,_netdev";
        }

        string mountUnitPath = Path.Combine(UnitRoot, mountUnit);
        string automountUnitPath = Path.Combine(UnitRoot, automountUnit);
        WriteAtomic(
            mountUnitPath,
            BuildMountUnit(request.Code, what, mountPath, type, options),
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        WriteAtomic(
            automountUnitPath,
            BuildAutomountUnit(request.Code, mountPath),
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ProcessResult reload = await RunAsync(
            "/usr/bin/systemctl",
            ["daemon-reload"],
            cancellationToken);
        ProcessResult enabled = await RunAsync(
            "/usr/bin/systemctl",
            ["enable", "--now", automountUnit],
            cancellationToken);
        ProcessResult started = await RunAsync(
            "/usr/bin/systemctl",
            ["start", mountUnit],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(45));
        _ = reload;
        _ = enabled;
        _ = started;

        ProcessResult inspection = await RunAsync(
            "/usr/bin/findmnt",
            ["-T", mountPath, "-n", "-o", "FSTYPE,OPTIONS"],
            cancellationToken);
        string mountDescription = inspection.StandardOutput.Trim();
        if (!IsExpectedReadOnlyMount(mountDescription, type))
        {
            return Failure(
                request,
                mountPath,
                "MOUNT_NOT_READ_ONLY",
                LimitDetail(mountDescription));
        }

        ProcessResult readable = await RunAsync(
            "/usr/sbin/runuser",
            ["-u", "miguel", "--", "/usr/bin/ls", "-A", mountPath],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(20));
        _ = readable;
        return new NasProvisioningResponse(
            true,
            request.Code,
            "MOUNTED",
            null,
            null,
            mountPath,
            request.Protocol);
    }

    public static string BuildCredentialFile(NasProvisioningRequest request)
    {
        StringBuilder builder = new();
        builder.Append("username=").Append(request.Username).Append('\n');
        builder.Append("password=").Append(request.Password).Append('\n');
        if (!string.IsNullOrEmpty(request.Domain))
        {
            builder.Append("domain=").Append(request.Domain).Append('\n');
        }
        return builder.ToString();
    }

    public static string BuildMountUnit(
        string code,
        string what,
        string where,
        string type,
        string options) =>
        "[Unit]\n" +
        $"Description=ATLAS NAS {code} (solo lectura)\n" +
        "Wants=network-online.target\n" +
        "After=network-online.target\n\n" +
        "[Mount]\n" +
        $"What={EscapeUnitValue(what)}\n" +
        $"Where={EscapeUnitValue(where)}\n" +
        $"Type={type}\n" +
        $"Options={options}\n" +
        "TimeoutSec=30\n";

    public static string BuildAutomountUnit(string code, string where) =>
        "[Unit]\n" +
        $"Description=ATLAS automount {code}\n\n" +
        "[Automount]\n" +
        $"Where={EscapeUnitValue(where)}\n" +
        "TimeoutIdleSec=300\n\n" +
        "[Install]\n" +
        "WantedBy=multi-user.target\n";

    public static string EscapeUnitValue(string value) => value
        .Replace("\\", "\\x5c", StringComparison.Ordinal)
        .Replace("%", "%%", StringComparison.Ordinal)
        .Replace(" ", "\\x20", StringComparison.Ordinal)
        .Replace("\t", "\\x09", StringComparison.Ordinal);

    public static bool IsExpectedReadOnlyMount(
        string description,
        string expectedType)
    {
        string[] parts = description.Split(
            ' ',
            2,
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
            string.Equals(parts[0], expectedType, StringComparison.Ordinal) &&
            parts[1].Split(',').Contains("ro", StringComparer.Ordinal) &&
            !parts[1].Split(',').Contains("rw", StringComparer.Ordinal);
    }

    private static string GetSmbSecurityOptions(string profile) => profile switch
    {
        SmbSecurityProfiles.Smb311Seal => "vers=3.1.1,seal",
        SmbSecurityProfiles.Smb30Seal => "vers=3.0,seal",
        SmbSecurityProfiles.Smb30Sign => "vers=3.0,sign",
        SmbSecurityProfiles.Smb21Sign => "vers=2.1,sign",
        _ => throw new InvalidDataException("Perfil SMB no admitido.")
    };

    private static void WriteAtomic(
        string path,
        string content,
        UnixFileMode mode)
    {
        string directory = Path.GetDirectoryName(path) ??
            throw new InvalidDataException("Ruta sin directorio.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            SetMode(temporary, mode);
            File.Move(temporary, path, true);
            SetMode(path, mode);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static NasProvisioningResponse Failure(
        NasProvisioningRequest request,
        string? mountPath,
        string errorCode,
        string? detail) =>
        new(
            false,
            request.Code,
            "FAILED",
            errorCode,
            detail,
            mountPath,
            request.Protocol);

    private static async Task RunAllowFailureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(fileName, arguments, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Stopping a unit that is not active is safe and idempotent.
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("No se pudo iniciar el proceso seguro.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("La operacion privilegiada excedio el tiempo permitido.");
        }
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} fallo ({process.ExitCode}): " +
                LimitDetail(error));
        }
        return new ProcessResult(output, error);
    }

    private static string LimitDetail(string? value)
    {
        string text = value?.Trim() ?? "";
        return text.Length <= 512 ? text : text[..512];
    }

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
