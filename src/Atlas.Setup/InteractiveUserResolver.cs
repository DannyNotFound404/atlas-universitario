using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Atlas.Setup;

internal sealed record InteractiveUserIdentity(
    string AccountName,
    string Sid);

internal static class InteractiveUserResolver
{
    private const uint TokenQuery = 0x0008;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    public static InteractiveUserIdentity Resolve()
    {
        int setupSessionId = Process.GetCurrentProcess().SessionId;
        string accountName = ReadSessionAccountName(setupSessionId);

        using (WindowsIdentity setupIdentity = WindowsIdentity.GetCurrent())
        {
            string? setupSid = setupIdentity.User?.Value;
            if (string.Equals(
                    setupIdentity.Name,
                    accountName,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(setupSid) &&
                !setupIdentity.IsSystem)
            {
                return new InteractiveUserIdentity(accountName, setupSid);
            }
        }

        Process[] explorers = Process.GetProcessesByName("explorer");

        try
        {
            foreach (Process explorer in explorers.OrderBy(process => process.Id))
            {
                try
                {
                    if (explorer.SessionId != setupSessionId ||
                        !OpenProcessToken(
                            explorer.Handle,
                            TokenQuery,
                            out SafeAccessTokenHandle token))
                    {
                        continue;
                    }

                    using (token)
                    using (WindowsIdentity identity = new(
                               token.DangerousGetHandle()))
                    {
                        string? sid = identity.User?.Value;
                        if (!string.IsNullOrWhiteSpace(sid) &&
                            !identity.IsSystem)
                        {
                            return new InteractiveUserIdentity(
                                accountName,
                                sid);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (SystemException)
                {
                }
            }
        }
        finally
        {
            foreach (Process explorer in explorers)
            {
                explorer.Dispose();
            }
        }

        string? profileSid = ResolveProfileSid(accountName);
        if (!string.IsNullOrWhiteSpace(profileSid))
        {
            return new InteractiveUserIdentity(accountName, profileSid);
        }

        throw new InvalidOperationException(
            "No fue posible identificar de forma segura al usuario que tiene " +
            "la sesión de Windows abierta. Inicie el instalador desde el " +
            "escritorio de ese usuario; no lo ejecute desde una sesión " +
            "administrativa diferente.");
    }

    private static string ReadSessionAccountName(int sessionId)
    {
        string userName = QuerySessionText(sessionId, WtsUserName);
        string domainName = QuerySessionText(sessionId, WtsDomainName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException(
                "La sesión actual de Windows no reportó un usuario " +
                "interactivo válido.");
        }

        return string.IsNullOrWhiteSpace(domainName)
            ? userName
            : $"{domainName}\\{userName}";
    }

    private static string QuerySessionText(int sessionId, int informationClass)
    {
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                informationClass,
                out IntPtr buffer,
                out int byteCount) ||
            buffer == IntPtr.Zero ||
            byteCount <= 2)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer)?.Trim() ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? ResolveProfileSid(string accountName)
    {
        string userName = accountName.Contains('\\')
            ? accountName[(accountName.LastIndexOf('\\') + 1)..]
            : accountName;
        List<(string Sid, bool ExactPath)> candidates = [];

        using RegistryKey? profiles = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList",
            writable: false);
        if (profiles is null)
        {
            return null;
        }

        foreach (string sidText in profiles.GetSubKeyNames())
        {
            if (!sidText.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            {
                continue;
            }

            using RegistryKey? profile = profiles.OpenSubKey(sidText);
            string? rawPath = profile?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string profileName = Path.GetFileName(
                Environment.ExpandEnvironmentVariables(rawPath)
                    .TrimEnd(Path.DirectorySeparatorChar));
            bool exactPath = string.Equals(
                profileName,
                userName,
                StringComparison.OrdinalIgnoreCase);
            bool qualifiedPath = profileName.StartsWith(
                userName + ".",
                StringComparison.OrdinalIgnoreCase);
            if (!exactPath && !qualifiedPath)
            {
                continue;
            }

            try
            {
                SecurityIdentifier sid = new(sidText);
                string resolvedAccount = sid
                    .Translate(typeof(NTAccount))
                    .Value;
                if (string.Equals(
                        resolvedAccount,
                        accountName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return sidText;
                }
            }
            catch (IdentityNotMappedException)
            {
                // Un perfil de dominio almacenado en caché puede no traducirse
                // cuando el controlador de dominio no está disponible.
            }
            catch (SystemException)
            {
            }

            candidates.Add((sidText, exactPath));
        }

        (string Sid, bool ExactPath)[] exactCandidates = candidates
            .Where(candidate => candidate.ExactPath)
            .ToArray();
        if (exactCandidates.Length == 1)
        {
            return exactCandidates[0].Sid;
        }
        return candidates.Count == 1 ? candidates[0].Sid : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        int informationClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
