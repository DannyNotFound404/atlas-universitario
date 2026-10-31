using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Miguel.Contracts;

namespace Miguel.Agent.Inventory;

public sealed class SoftwareInventoryCollector
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int Limit = 10000;
    private readonly List<InstalledSoftware> applications = [];
    private readonly List<SoftwareSource> sources = [];

    public SoftwareInventory Capture()
    {
        applications.Clear(); sources.Clear();
        ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, null);
        ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, null);
        try
        {
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            foreach (string sid in (profiles?.GetSubKeyNames() ?? []).Where(IsUserSid).Take(250))
            {
                using var loaded = users.OpenSubKey(sid);
                if (loaded is null)
                {
                    sources.Add(new(UserSource(sid, RegistryView.Registry64), "USER", "UNAVAILABLE", "profile_not_loaded"));
                    sources.Add(new(UserSource(sid, RegistryView.Registry32), "USER", "UNAVAILABLE", "profile_not_loaded"));
                    continue;
                }
                ReadRegistry(RegistryHive.Users, RegistryView.Registry64, sid);
                ReadRegistry(RegistryHive.Users, RegistryView.Registry32, sid);
            }
            if (profiles is null || profiles.GetSubKeyNames().Count(IsUserSid) > 250)
                sources.Add(new("profile-discovery", "USER", "PARTIAL", "profile_limit_or_unavailable"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { sources.Add(new("profile-discovery", "USER", "UNAVAILABLE", "access_denied")); }
        ReadPackages();
        return new(1, sources.ToArray(), applications.DistinctBy(a => a.InstallationKey).ToArray());
    }

    internal static bool IsUserSid(string sid) => sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal);
    private static string UserSource(string sid, RegistryView view) => "user-" + SoftwareIdentity.Hash(sid)[..24] + "-" + (view == RegistryView.Registry64 ? "64" : "32");
    private void ReadRegistry(RegistryHive hive, RegistryView view, string? sid)
    {
        string source = sid is null ? "machine-" + (view == RegistryView.Registry64 ? "64" : "32") : UserSource(sid, view);
        string status = "COMPLETE", reason = "";
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(sid is null ? Uninstall : sid + "\\" + Uninstall);
            foreach (string keyName in uninstall?.GetSubKeyNames() ?? [])
            {
                if (applications.Count >= Limit) { status = "PARTIAL"; reason = "item_limit"; break; }
                try
                {
                    using var key = uninstall!.OpenSubKey(keyName);
                    if (key is null) { status = "PARTIAL"; reason = "entry_unavailable"; continue; }
                    string name = Value(key, "DisplayName", 512);
                    // Include runtimes and system components that expose a display name; no uninstall command is read.
                    if (name.Length == 0) continue;
                    string date = Value(key, "InstallDate", 16);
                    applications.Add(new(SoftwareIdentity.Hash(source + "\n" + keyName), source, name,
                        Value(key, "Publisher", 512), Value(key, "DisplayVersion", 128),
                        view == RegistryView.Registry64 ? "x64" : "x86", sid is null ? "MACHINE" : "USER",
                        DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var installed) ? installed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                { status = "PARTIAL"; reason = "entry_unavailable"; }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { status = "UNAVAILABLE"; reason = "source_unavailable"; }
        sources.Add(new(source, sid is null ? "MACHINE" : "USER", status, reason.Length == 0 ? null : reason));
    }

    private static string Value(RegistryKey key, string name, int max) => Clean(Convert.ToString(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), CultureInfo.InvariantCulture), max);
    internal static string Clean(string? value, int max) => new((value ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray());

    private void ReadPackages()
    {
        const string source = "appx-all-users";
        try
        {
            // Fixed read-only script, no profile, paths or registry values interpolated into commands.
            const string script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $p=@(Get-AppxPackage -AllUsers -PackageTypeFilter Main | Select-Object Name,Publisher,@{n='Version';e={$_.Version.ToString()}},@{n='Architecture';e={$_.Architecture.ToString()}},PackageFullName); ConvertTo-Json -InputObject $p -Compress -Depth 3";
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("package_query_unavailable");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                var stdout = ReadBoundedAsync(process.StandardOutput, 8_000_000, timeout.Token);
                var stderr = ReadBoundedAsync(process.StandardError, 64_000, timeout.Token);
                Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new IOException("package_query_failed");
                using var json = JsonDocument.Parse(stdout.Result);
                if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                bool limited = false;
                foreach (var p in json.RootElement.EnumerateArray())
                {
                    if (applications.Count >= Limit) { limited = true; break; }
                    string Text(string name, int max = 512) => Clean(p.GetProperty(name).GetString(), max);
                    applications.Add(new(SoftwareIdentity.Hash(source + "\n" + Text("PackageFullName")), source,
                        Text("Name"), Text("Publisher"), Text("Version", 128), Text("Architecture", 32), "PACKAGE"));
                }
                sources.Add(new(source, "PACKAGE", limited ? "PARTIAL" : "COMPLETE", limited ? "item_limit" : null));
            }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { sources.Add(new(source, "PACKAGE", "UNAVAILABLE", "package_query_unavailable")); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var result = new StringBuilder(); char[] buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        { if (result.Length + read > maximum) throw new IOException("package_output_limit"); result.Append(buffer, 0, read); }
        return result.ToString();
    }
}
