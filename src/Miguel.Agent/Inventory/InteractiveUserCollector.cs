using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Miguel.Contracts;

namespace Miguel.Agent.Inventory;

public sealed class InteractiveUserCollector(
    TimeProvider timeProvider,
    ILogger<InteractiveUserCollector> logger)
{
    private const int NetApiSuccess = 0;
    private const int UserInfoLevel = 10;
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const string LogonUiSessionDataPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\SessionData";
    private static readonly IntPtr CurrentServerHandle = IntPtr.Zero;
    private static readonly TimeSpan SuccessfulResolutionLifetime =
        TimeSpan.FromHours(6);
    private static readonly TimeSpan FailedResolutionLifetime =
        TimeSpan.FromMinutes(15);
    private readonly object cacheLock = new();
    private string? cachedAccountName;
    private InteractiveUserObservation? cachedObservation;
    private DateTimeOffset cacheExpiresAtUtc = DateTimeOffset.MinValue;

    public InteractiveUserObservation? Capture()
    {
        InteractiveSession? session = ReadInteractiveSession();
        if (session is null || string.IsNullOrWhiteSpace(session.AccountName))
        {
            return null;
        }

        string accountName = session.AccountName.Trim();
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (cacheLock)
        {
            if (string.Equals(
                    cachedAccountName,
                    accountName,
                    StringComparison.OrdinalIgnoreCase) &&
                cachedObservation is not null &&
                cacheExpiresAtUtc > now)
            {
                return cachedObservation;
            }
        }

        (string domainName, string userName) = SplitAccountName(accountName);
        string? securityIdentifier = ResolveSid(accountName);
        string? displayName = NormalizeDisplayName(session.DisplayName) ??
            ResolveDisplayName(domainName, userName);

        InteractiveUserObservation observation = new(
            accountName,
            displayName,
            securityIdentifier,
            session.SessionType,
            string.IsNullOrWhiteSpace(displayName)
                ? "WINDOWS_ACCOUNT"
                : IsLocalDomain(domainName)
                    ? "LOCAL_ACCOUNT"
                    : "ACTIVE_DIRECTORY");

        lock (cacheLock)
        {
            cachedAccountName = accountName;
            cachedObservation = observation;
            cacheExpiresAtUtc = now.Add(
                string.IsNullOrWhiteSpace(displayName)
                    ? FailedResolutionLifetime
                    : SuccessfulResolutionLifetime);
        }

        return observation;
    }

    internal static string ComposeAccountName(
        string? domainName,
        string userName)
    {
        string normalizedUser = userName.Trim();
        string normalizedDomain = domainName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalizedDomain)
            ? normalizedUser
            : $"{normalizedDomain}\\{normalizedUser}";
    }

    internal static (string DomainName, string UserName) SplitAccountName(
        string accountName)
    {
        int separator = accountName.IndexOf('\\');
        if (separator <= 0 || separator == accountName.Length - 1)
        {
            return (string.Empty, accountName.Trim());
        }

        return (
            accountName[..separator].Trim(),
            accountName[(separator + 1)..].Trim());
    }

    private InteractiveSession? ReadInteractiveSession()
    {
        uint activeConsoleSessionId = WTSGetActiveConsoleSessionId();
        InteractiveSession? logonUiSession = ReadLogonUiSession(
            activeConsoleSessionId);
        if (logonUiSession is not null)
        {
            return logonUiSession;
        }

        InteractiveSession? consoleSession = ReadSession(
            activeConsoleSessionId);
        if (consoleSession is not null)
        {
            return consoleSession;
        }

        InteractiveSession? activeSession = ReadFirstActiveSession();
        if (activeSession is not null)
        {
            return activeSession;
        }

        string? wmiAccountName = ReadWmiAccountName();
        return string.IsNullOrWhiteSpace(wmiAccountName)
            ? null
            : new InteractiveSession(
                wmiAccountName.Trim(),
                "CONSOLE",
                null,
                null);
    }

    private InteractiveSession? ReadLogonUiSession(
        uint preferredSessionId)
    {
        try
        {
            using RegistryKey? sessionData = Registry.LocalMachine.OpenSubKey(
                LogonUiSessionDataPath,
                writable: false);
            if (sessionData is null)
            {
                return null;
            }

            List<int> sessionIds = [];
            if (preferredSessionId != InvalidSessionId &&
                preferredSessionId <= int.MaxValue)
            {
                sessionIds.Add(unchecked((int)preferredSessionId));
            }

            sessionIds.AddRange(
                sessionData.GetSubKeyNames()
                    .Select(name => int.TryParse(
                        name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int sessionId)
                        ? sessionId
                        : -1)
                    .Where(sessionId => sessionId >= 0)
                    .OrderByDescending(sessionId => sessionId)
                    .Where(sessionId => !sessionIds.Contains(sessionId)));

            foreach (int sessionId in sessionIds)
            {
                using RegistryKey? session = sessionData.OpenSubKey(
                    sessionId.ToString(CultureInfo.InvariantCulture),
                    writable: false);
                if (session is null)
                {
                    continue;
                }

                string? accountName = NormalizeDisplayName(
                    Convert.ToString(
                        session.GetValue("LoggedOnSAMUser") ??
                            session.GetValue("LoggedOnUser"),
                        CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(accountName))
                {
                    continue;
                }

                string? displayName = NormalizeDisplayName(
                    Convert.ToString(
                        session.GetValue("LoggedOnDisplayName"),
                        CultureInfo.InvariantCulture));
                return new InteractiveSession(
                    accountName,
                    sessionId == unchecked((int)preferredSessionId)
                        ? "CONSOLE"
                        : "REMOTE_INTERACTIVE",
                    sessionId,
                    displayName);
            }
        }
        catch (Exception ex) when (
            ex is SystemException or UnauthorizedAccessException)
        {
            logger.LogDebug(
                ex,
                "No se pudo consultar el registro de sesiones de LogonUI.");
        }

        return null;
    }

    private InteractiveSession? ReadFirstActiveSession()
    {
        IntPtr sessionsBuffer = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessions(
                    CurrentServerHandle,
                    0,
                    1,
                    out sessionsBuffer,
                    out int sessionCount) ||
                sessionsBuffer == IntPtr.Zero)
            {
                return null;
            }

            int itemSize = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < sessionCount; index++)
            {
                IntPtr itemPointer = IntPtr.Add(
                    sessionsBuffer,
                    index * itemSize);
                WtsSessionInfo item = Marshal.PtrToStructure<WtsSessionInfo>(
                    itemPointer);
                if (item.State != WtsConnectState.Active)
                {
                    continue;
                }

                InteractiveSession? session = ReadSession(
                    unchecked((uint)item.SessionId));
                if (session is not null)
                {
                    return session;
                }
            }
        }
        catch (Exception ex) when (
            ex is SystemException or ArgumentException)
        {
            logger.LogDebug(
                ex,
                "No se pudieron enumerar las sesiones interactivas de Windows.");
        }
        finally
        {
            if (sessionsBuffer != IntPtr.Zero)
            {
                WTSFreeMemory(sessionsBuffer);
            }
        }

        return null;
    }

    private InteractiveSession? ReadSession(uint sessionId)
    {
        if (sessionId == InvalidSessionId || sessionId > int.MaxValue)
        {
            return null;
        }

        int normalizedSessionId = unchecked((int)sessionId);
        string? userName = QuerySessionString(
            normalizedSessionId,
            WtsInfoClass.UserName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        string? domainName = QuerySessionString(
            normalizedSessionId,
            WtsInfoClass.DomainName);
        ushort? protocol = QuerySessionUInt16(
            normalizedSessionId,
            WtsInfoClass.ClientProtocolType);
        return new InteractiveSession(
            ComposeAccountName(domainName, userName),
            protocol == 2 ? "REMOTE_INTERACTIVE" : "CONSOLE",
            normalizedSessionId,
            null);
    }

    private static string? QuerySessionString(
        int sessionId,
        WtsInfoClass infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(
                    CurrentServerHandle,
                    sessionId,
                    infoClass,
                    out buffer,
                    out int bytesReturned) ||
                buffer == IntPtr.Zero ||
                bytesReturned <= sizeof(char))
            {
                return null;
            }

            string? value = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private static ushort? QuerySessionUInt16(
        int sessionId,
        WtsInfoClass infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(
                    CurrentServerHandle,
                    sessionId,
                    infoClass,
                    out buffer,
                    out int bytesReturned) ||
                buffer == IntPtr.Zero ||
                bytesReturned < sizeof(ushort))
            {
                return null;
            }

            return unchecked((ushort)Marshal.ReadInt16(buffer));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private string? ReadWmiAccountName()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                @"root\cimv2",
                "SELECT UserName FROM Win32_ComputerSystem");
            using ManagementObjectCollection results = searcher.Get();
            ManagementBaseObject? computer =
                results.Cast<ManagementBaseObject>().FirstOrDefault();
            return Convert.ToString(
                computer?["UserName"],
                CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "No se pudo identificar al usuario de la consola de Windows.");
            return null;
        }
    }

    private string? ResolveSid(string accountName)
    {
        try
        {
            NTAccount account = new(accountName);
            return ((SecurityIdentifier)account.Translate(
                typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException ex)
        {
            logger.LogDebug(
                ex,
                "No se pudo resolver el SID de {AccountName}.",
                accountName);
            return null;
        }
    }

    private string? ResolveDisplayName(string domainName, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        IntPtr domainControllerBuffer = IntPtr.Zero;
        IntPtr userInfoBuffer = IntPtr.Zero;

        try
        {
            string? serverName = null;
            if (!string.IsNullOrWhiteSpace(domainName) &&
                !IsLocalDomain(domainName))
            {
                int dcResult = NetGetDCName(
                    null,
                    domainName,
                    out domainControllerBuffer);
                if (dcResult != NetApiSuccess ||
                    domainControllerBuffer == IntPtr.Zero)
                {
                    return null;
                }

                serverName = Marshal.PtrToStringUni(domainControllerBuffer);
            }

            int result = NetUserGetInfo(
                serverName,
                userName,
                UserInfoLevel,
                out userInfoBuffer);
            if (result != NetApiSuccess || userInfoBuffer == IntPtr.Zero)
            {
                return null;
            }

            UserInfo10 info = Marshal.PtrToStructure<UserInfo10>(
                userInfoBuffer);
            return NormalizeDisplayName(info.FullName);
        }
        catch (Exception ex) when (
            ex is SystemException or ArgumentException)
        {
            logger.LogDebug(
                ex,
                "No se pudo resolver el nombre visible de {Domain}\\{User}.",
                domainName,
                userName);
            return null;
        }
        finally
        {
            if (userInfoBuffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(userInfoBuffer);
            }

            if (domainControllerBuffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(domainControllerBuffer);
            }
        }
    }

    private static bool IsLocalDomain(string domainName) =>
        string.IsNullOrWhiteSpace(domainName) ||
        string.Equals(
            domainName,
            Environment.MachineName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(domainName, ".", StringComparison.Ordinal);

    private static string? NormalizeDisplayName(string? displayName)
    {
        string? normalized = displayName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo10
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserComment;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? FullName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? WindowStationName;

        public WtsConnectState State;
    }

    private sealed record InteractiveSession(
        string AccountName,
        string SessionType,
        int? SessionId,
        string? DisplayName);

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
        ClientProtocolType = 16
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport(
        "Wtsapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport(
        "Wtsapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetDCName(
        string? serverName,
        string domainName,
        out IntPtr buffer);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(
        string? serverName,
        string userName,
        int level,
        out IntPtr buffer);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
