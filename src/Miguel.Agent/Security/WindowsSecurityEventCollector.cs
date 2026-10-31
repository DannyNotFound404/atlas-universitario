using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Miguel.Contracts;

namespace Miguel.Agent.Security;

public sealed record SecurityEventCapture(
    SecurityEventBatch? Batch,
    IReadOnlyDictionary<string, long> Cursors);

public sealed class WindowsSecurityEventCollector(
    TimeProvider timeProvider,
    ILogger<WindowsSecurityEventCollector> logger)
{
    private static readonly ChannelDefinition[] Channels =
    [
        new(
            "Security:Critical",
            "Security",
            "(EventID=1102 or EventID=4616 or EventID=4697 or " +
            "EventID=4719 or EventID=4720 or EventID=4722 or EventID=4725 or " +
            "EventID=4726 or EventID=4732 or EventID=4733 or EventID=4946 or " +
            "EventID=4947 or EventID=4948 or EventID=4950)",
            100),
        new(
            "Defender:Operational",
            "Microsoft-Windows-Windows Defender/Operational",
            "(EventID=1116 or EventID=1117 or EventID=1121 or EventID=5001 or " +
            "EventID=5007 or EventID=5008 or EventID=5010 or EventID=5012 or " +
            "EventID=5013)",
            100),
        new(
            "System:Security",
            "System",
            "(EventID=41 or EventID=6005 or EventID=6006 or EventID=6008 or EventID=7045)",
            100),
        new(
            "Security:Authentication",
            "Security",
            "(EventID=4624 or EventID=4625 or EventID=4634 or EventID=4647 or EventID=4648)",
            150),
        new(
            "TerminalServices:Authentication",
            "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational",
            "(EventID=1149)",
            100),
        new(
            "TerminalServices:Sessions",
            "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
            "(EventID=21 or EventID=22 or EventID=23 or EventID=24 or EventID=25)",
            100),
        new(
            "Security:Privilege",
            "Security",
            "(EventID=4672)",
            50)
    ];

    public SecurityEventCapture Capture(
        IReadOnlyDictionary<string, long> storedCursors,
        TimeSpan initialLookback,
        int maximumEvents)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset earliest = now.Subtract(initialLookback);
        List<WindowsSecurityEvent> events = [];
        Dictionary<string, long> cursors = new(
            storedCursors,
            StringComparer.OrdinalIgnoreCase);

        foreach (ChannelDefinition channel in Channels)
        {
            if (events.Count >= maximumEvents)
            {
                break;
            }

            try
            {
                CaptureChannel(
                    channel,
                    storedCursors.GetValueOrDefault(channel.CursorKey),
                    earliest,
                    Math.Min(channel.MaximumEvents, maximumEvents - events.Count),
                    events,
                    cursors);
            }
            catch (EventLogNotFoundException ex)
            {
                logger.LogInformation(
                    ex,
                    "El canal {Channel} no esta disponible en este equipo.",
                    channel.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(
                    ex,
                    "El agente no tiene acceso al canal {Channel}.",
                    channel.Name);
            }
            catch (EventLogException ex)
            {
                logger.LogWarning(
                    ex,
                    "No se pudo leer el canal {Channel}.",
                    channel.Name);
            }
        }

        WindowsSecurityEvent[] ordered = events
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Channel, StringComparer.Ordinal)
            .ThenBy(item => item.RecordId)
            .ToArray();
        SecurityEventBatch? batch = ordered.Length == 0
            ? null
            : new SecurityEventBatch(
                ApiContract.SchemaVersion,
                Guid.NewGuid(),
                now,
                ordered);
        return new SecurityEventCapture(batch, cursors);
    }

    private static void CaptureChannel(
        ChannelDefinition channel,
        long storedCursor,
        DateTimeOffset earliest,
        int maximumEvents,
        ICollection<WindowsSecurityEvent> destination,
        IDictionary<string, long> nextCursors)
    {
        string xpath = storedCursor > 0
            ? $"*[System[{channel.EventPredicate} and (EventRecordID > {storedCursor.ToString(CultureInfo.InvariantCulture)})]]"
            : $"*[System[{channel.EventPredicate}]]";
        EventLogQuery query = new(channel.Name, PathType.LogName, xpath)
        {
            ReverseDirection = storedCursor == 0,
            TolerateQueryErrors = true
        };
        using EventLogReader reader = new(query);
        List<WindowsSecurityEvent> channelEvents = [];
        long highestRecordId = storedCursor;

        while (channelEvents.Count < maximumEvents && reader.ReadEvent() is { } record)
        {
            using (record)
            {
                long recordId = record.RecordId ?? 0;
                DateTimeOffset occurredAt = record.TimeCreated.HasValue
                    ? new DateTimeOffset(record.TimeCreated.Value).ToUniversalTime()
                    : DateTimeOffset.MinValue;
                if (storedCursor == 0 && occurredAt < earliest)
                {
                    break;
                }

                highestRecordId = Math.Max(highestRecordId, recordId);
                WindowsSecurityEvent? parsed = Parse(record, channel.Name, occurredAt);
                if (parsed is not null)
                {
                    channelEvents.Add(parsed);
                }
            }
        }

        IEnumerable<WindowsSecurityEvent> ordered = storedCursor == 0
            ? channelEvents.AsEnumerable().Reverse()
            : channelEvents;
        foreach (WindowsSecurityEvent item in ordered)
        {
            destination.Add(item);
        }
        if (highestRecordId > storedCursor)
        {
            nextCursors[channel.CursorKey] = highestRecordId;
        }
    }

    internal static WindowsSecurityEvent? ParseXml(
        string xml,
        string channel,
        long fallbackRecordId = 0,
        DateTimeOffset? fallbackTime = null)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        XElement? system = document.Root?.Elements().FirstOrDefault(
            item => item.Name.LocalName == "System");
        int eventCode = ParseInt(
            system?.Elements().FirstOrDefault(item => item.Name.LocalName == "EventID")?.Value) ?? 0;
        long recordId = ParseLong(
            system?.Elements().FirstOrDefault(item => item.Name.LocalName == "EventRecordID")?.Value) ??
            fallbackRecordId;
        string? timeText = system?.Elements()
            .FirstOrDefault(item => item.Name.LocalName == "TimeCreated")?
            .Attribute("SystemTime")?.Value;
        DateTimeOffset occurredAt = DateTimeOffset.TryParse(
            timeText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsedTime)
            ? parsedTime.ToUniversalTime()
            : fallbackTime ?? DateTimeOffset.UtcNow;
        Dictionary<string, string?> values = ReadValues(document);
        values["_ProviderName"] = system?.Elements()
            .FirstOrDefault(item => item.Name.LocalName == "Provider")?
            .Attribute("Name")?.Value;
        return CreateEvent(channel, recordId, eventCode, occurredAt, values);
    }

    private static WindowsSecurityEvent? Parse(
        EventRecord record,
        string channel,
        DateTimeOffset occurredAt)
    {
        try
        {
            return ParseXml(
                record.ToXml(),
                channel,
                record.RecordId ?? 0,
                occurredAt);
        }
        catch (Exception ex) when (
            ex is EventLogException or InvalidOperationException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static WindowsSecurityEvent? CreateEvent(
        string channel,
        long recordId,
        int eventCode,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string?> values)
    {
        int? logonType = ReadInt(values, "LogonType");
        string? sourceAddress = NormalizeAddress(
            First(values, "IpAddress", "SourceNetworkAddress", "Param3", "Address"));
        string? sessionName = First(values, "SessionName");
        string? sessionId = First(values, "TargetLogonId", "SubjectLogonId", "SessionID");
        bool remoteSession = IsRemoteSession(sessionName, sourceAddress);
        Classification? classification = Classify(eventCode, logonType, remoteSession);
        if (classification is null || recordId <= 0)
        {
            return null;
        }

        string? userName = First(values, "TargetUserName", "Param1", "User");
        string? userDomain = First(values, "TargetDomainName", "Param2", "Domain");
        string? subjectUserName = First(values, "SubjectUserName");
        string? subjectUserDomain = First(values, "SubjectDomainName");
        string? targetName = ResolveTargetName(eventCode, values);
        string? processName = First(values, "ProcessName", "CallerProcessName", "ImagePath", "ServiceFileName");
        string? details = ResolveDetails(eventCode, values);

        return new WindowsSecurityEvent(
            Limit(channel, 192)!,
            recordId,
            eventCode,
            occurredAt,
            classification.Category,
            classification.Outcome,
            logonType,
            Limit(userName, 256),
            Limit(userDomain, 256),
            Limit(First(values, "TargetUserSid"), 192),
            Limit(sourceAddress, 64),
            ReadInt(values, "IpPort", "SourcePort"),
            Limit(First(values, "WorkstationName"), 256),
            Limit(First(values, "AuthenticationPackageName"), 128),
            Limit(sessionName, 128),
            Limit(First(values, "FailureReason", "Error Description"), 256),
            Limit(First(values, "Status", "Error Code"), 32),
            Limit(First(values, "SubStatus"), 32),
            classification.Severity,
            Limit(First(values, "_ProviderName"), 192),
            Limit(subjectUserName, 256),
            Limit(subjectUserDomain, 256),
            Limit(First(values, "SubjectUserSid"), 192),
            Limit(targetName, 512),
            Limit(processName, 512),
            Limit(sessionId, 128),
            Limit(details, 1024),
            Limit(sessionId, 128));
    }

    private static Classification? Classify(
        int eventCode,
        int? logonType,
        bool remoteSession) => eventCode switch
    {
        4624 when logonType is 10 or 12 =>
            New(SecurityEventCategories.RemoteInteractiveLogon, SecurityEventOutcomes.Success),
        4624 when logonType == 3 =>
            New(SecurityEventCategories.NetworkLogon, SecurityEventOutcomes.Success),
        4624 when logonType is 2 or 7 or 11 or 13 =>
            New(SecurityEventCategories.InteractiveLogon, SecurityEventOutcomes.Success),
        4624 when logonType == 4 =>
            New(SecurityEventCategories.BatchLogon, SecurityEventOutcomes.Success),
        4624 when logonType == 5 =>
            New(SecurityEventCategories.ServiceLogon, SecurityEventOutcomes.Success),
        4624 when logonType == 8 =>
            New(SecurityEventCategories.NetworkCleartextLogon, SecurityEventOutcomes.Success, SecurityEventSeverities.Medium),
        4624 when logonType == 9 =>
            New(SecurityEventCategories.NewCredentialsLogon, SecurityEventOutcomes.Success, SecurityEventSeverities.Low),
        4625 when logonType is 10 or 12 =>
            New(SecurityEventCategories.RemoteInteractiveLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.High),
        4625 when logonType == 3 =>
            New(SecurityEventCategories.NetworkLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.Medium),
        4625 when logonType is 2 or 7 or 11 or 13 =>
            New(SecurityEventCategories.InteractiveLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.Medium),
        4625 when logonType == 4 =>
            New(SecurityEventCategories.BatchLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.Medium),
        4625 when logonType == 5 =>
            New(SecurityEventCategories.ServiceLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.Medium),
        4625 when logonType == 8 =>
            New(SecurityEventCategories.NetworkCleartextLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.High),
        4625 when logonType == 9 =>
            New(SecurityEventCategories.NewCredentialsLogon, SecurityEventOutcomes.Failure, SecurityEventSeverities.Medium),
        4634 => New(SecurityEventCategories.SessionLogoff, SecurityEventOutcomes.Information),
        4647 => New(SecurityEventCategories.UserInitiatedLogoff, SecurityEventOutcomes.Information),
        4648 => New(SecurityEventCategories.ExplicitCredentialLogon, SecurityEventOutcomes.Information),
        1149 => New(SecurityEventCategories.RemoteAuthentication, SecurityEventOutcomes.Success),
        21 when remoteSession => New(SecurityEventCategories.RemoteSessionLogon, SecurityEventOutcomes.Success),
        21 => New(SecurityEventCategories.InteractiveSessionLogon, SecurityEventOutcomes.Success),
        22 => New(SecurityEventCategories.SessionShellStarted, SecurityEventOutcomes.Information),
        23 when remoteSession => New(SecurityEventCategories.RemoteSessionLogoff, SecurityEventOutcomes.Information),
        23 => New(SecurityEventCategories.InteractiveSessionLogoff, SecurityEventOutcomes.Information),
        24 when remoteSession => New(SecurityEventCategories.RemoteSessionDisconnected, SecurityEventOutcomes.Information),
        24 => New(SecurityEventCategories.SessionDisconnected, SecurityEventOutcomes.Information),
        25 when remoteSession => New(SecurityEventCategories.RemoteSessionReconnected, SecurityEventOutcomes.Success),
        25 => New(SecurityEventCategories.SessionReconnected, SecurityEventOutcomes.Success),
        1102 => New(SecurityEventCategories.AuditLogCleared, SecurityEventOutcomes.Changed, SecurityEventSeverities.Critical),
        4616 => New(SecurityEventCategories.SystemTimeChanged, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        4672 => New(SecurityEventCategories.PrivilegedLogon, SecurityEventOutcomes.Success, SecurityEventSeverities.Low),
        4697 or 7045 => New(SecurityEventCategories.ServiceInstalled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4719 => New(SecurityEventCategories.AuditPolicyChanged, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4720 => New(SecurityEventCategories.LocalAccountCreated, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4722 => New(SecurityEventCategories.LocalAccountEnabled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4725 => New(SecurityEventCategories.LocalAccountDisabled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4726 => New(SecurityEventCategories.LocalAccountDeleted, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4732 => New(SecurityEventCategories.LocalGroupMemberAdded, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4733 => New(SecurityEventCategories.LocalGroupMemberRemoved, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        4946 => New(SecurityEventCategories.FirewallRuleAdded, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        4947 => New(SecurityEventCategories.FirewallRuleChanged, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        4948 => New(SecurityEventCategories.FirewallRuleRemoved, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        4950 => New(SecurityEventCategories.FirewallSettingChanged, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        41 => New(SecurityEventCategories.UnexpectedShutdown, SecurityEventOutcomes.Failure, SecurityEventSeverities.Critical),
        6005 => New(SecurityEventCategories.SystemStartup, SecurityEventOutcomes.Information),
        6006 => New(SecurityEventCategories.SystemShutdown, SecurityEventOutcomes.Information),
        6008 => New(SecurityEventCategories.UnexpectedShutdown, SecurityEventOutcomes.Failure, SecurityEventSeverities.High),
        1116 => New(SecurityEventCategories.MalwareDetected, SecurityEventOutcomes.Detected, SecurityEventSeverities.Critical),
        1117 => New(SecurityEventCategories.MalwareActionTaken, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        1121 => New(SecurityEventCategories.AttackSurfaceReductionBlocked, SecurityEventOutcomes.Blocked, SecurityEventSeverities.High),
        5001 => New(SecurityEventCategories.RealtimeProtectionDisabled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        5007 => New(SecurityEventCategories.AntimalwareConfigurationChanged, SecurityEventOutcomes.Changed, SecurityEventSeverities.Medium),
        5008 => New(SecurityEventCategories.AntimalwareEngineFailure, SecurityEventOutcomes.Failure, SecurityEventSeverities.High),
        5010 => New(SecurityEventCategories.AntispywareDisabled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        5012 => New(SecurityEventCategories.AntivirusDisabled, SecurityEventOutcomes.Changed, SecurityEventSeverities.High),
        5013 => New(SecurityEventCategories.TamperProtectionBlocked, SecurityEventOutcomes.Blocked, SecurityEventSeverities.High),
        _ => null
    };

    private static Classification New(
        string category,
        string outcome,
        string severity = SecurityEventSeverities.Information) =>
        new(category, outcome, severity);

    private static bool IsRemoteSession(string? sessionName, string? sourceAddress) =>
        (!string.IsNullOrWhiteSpace(sessionName) &&
         sessionName.Contains("RDP", StringComparison.OrdinalIgnoreCase)) ||
        !string.IsNullOrWhiteSpace(sourceAddress);

    private static string? ResolveTargetName(
        int eventCode,
        IReadOnlyDictionary<string, string?> values) => eventCode switch
    {
        4697 or 7045 => First(values, "ServiceName", "Service Name"),
        4719 => First(values, "SubcategoryName", "CategoryName"),
        4720 or 4722 or 4725 or 4726 => First(values, "TargetUserName"),
        4732 or 4733 => First(values, "MemberName", "TargetUserName"),
        4946 or 4947 or 4948 => First(values, "RuleName", "ProfileChanged"),
        1116 or 1117 => First(values, "Threat Name", "ThreatName", "Name"),
        1121 => First(values, "Path", "Process Name", "ProcessName"),
        _ => First(values, "TargetServerName", "TargetInfo")
    };

    private static string? ResolveDetails(
        int eventCode,
        IReadOnlyDictionary<string, string?> values) => eventCode switch
    {
        4616 => JoinDetails(
            First(values, "PreviousTime"),
            First(values, "NewTime")),
        4672 => First(values, "PrivilegeList"),
        4697 or 7045 => JoinDetails(
            First(values, "ServiceName", "Service Name"),
            First(values, "ServiceFileName", "ImagePath"),
            First(values, "StartType", "ServiceStartType")),
        4719 => JoinDetails(
            First(values, "CategoryName"),
            First(values, "SubcategoryName"),
            First(values, "AuditPolicyChanges")),
        4732 or 4733 => JoinDetails(
            First(values, "MemberName", "MemberSid"),
            First(values, "TargetUserName")),
        4946 or 4947 or 4948 or 4950 => JoinDetails(
            First(values, "RuleName", "ProfileChanged"),
            First(values, "SettingType")),
        1116 or 1117 => JoinDetails(
            First(values, "Threat Name", "ThreatName", "Name"),
            First(values, "Path"),
            First(values, "Action Name", "ActionName")),
        1121 => JoinDetails(
            First(values, "Path"),
            First(values, "Process Name", "ProcessName")),
        5007 => JoinDetails(
            First(values, "Old Value", "OldValue"),
            First(values, "New Value", "NewValue")),
        _ => null
    };

    private static string? JoinDetails(params string?[] values)
    {
        string[] present = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return present.Length == 0 ? null : string.Join(" | ", present);
    }

    private static Dictionary<string, string?> ReadValues(XDocument document)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        XElement? eventData = document.Descendants()
            .FirstOrDefault(item => item.Name.LocalName == "EventData");
        if (eventData is not null)
        {
            int unnamed = 0;
            foreach (XElement data in eventData.Elements()
                         .Where(item => item.Name.LocalName == "Data"))
            {
                string key = data.Attribute("Name")?.Value ?? $"Data{unnamed++}";
                values[key] = data.Value;
            }
        }

        XElement? userData = document.Descendants()
            .FirstOrDefault(item => item.Name.LocalName == "UserData");
        if (userData is not null)
        {
            foreach (XElement leaf in userData.Descendants().Where(item => !item.HasElements))
            {
                values.TryAdd(leaf.Name.LocalName, leaf.Value);
            }
        }
        return values;
    }

    private static string? First(
        IReadOnlyDictionary<string, string?> values,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (values.TryGetValue(name, out string? value) &&
                !string.IsNullOrWhiteSpace(value) &&
                value is not "-" and not "::1" and not "127.0.0.1")
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "-" or "::1" or "127.0.0.1" ||
            value.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return value.Trim();
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, string?> values,
        params string[] names) =>
        int.TryParse(First(values, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

    private static string? Limit(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, maximumLength)];
    }

    private sealed record ChannelDefinition(
        string CursorKey,
        string Name,
        string EventPredicate,
        int MaximumEvents);

    private sealed record Classification(
        string Category,
        string Outcome,
        string Severity);
}
