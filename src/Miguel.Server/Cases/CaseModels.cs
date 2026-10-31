using System.Text.Json;

namespace Miguel.Server.Cases;

public static class CaseTypes
{
    public const string Incident = "INCIDENT";
    public const string Vulnerability = "VULNERABILITY";
    public const string Exception = "EXCEPTION";
    public const string Restoration = "RESTORATION";
    public const string Access = "ACCESS";
    public const string Supplier = "SUPPLIER";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Incident, Vulnerability, Exception, Restoration, Access, Supplier, Other],
        StringComparer.Ordinal);
}

public static class CaseSeverities
{
    public const string Info = "INFO";
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Info, Low, Medium, High, Critical],
        StringComparer.Ordinal);
}

public static class CasePriorities
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Urgent = "URGENT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Low, Medium, High, Urgent],
        StringComparer.Ordinal);
}

public static class CaseStatuses
{
    public const string New = "NEW";
    public const string Evaluating = "EVALUATING";
    public const string Assigned = "ASSIGNED";
    public const string InTreatment = "IN_TREATMENT";
    public const string PendingValidation = "PENDING_VALIDATION";
    public const string Closed = "CLOSED";
    public const string AcceptedException = "ACCEPTED_EXCEPTION";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [New, Evaluating, Assigned, InTreatment, PendingValidation, Closed,
         AcceptedException],
        StringComparer.Ordinal);

    public static bool IsClosed(string value) =>
        value is Closed or AcceptedException;
}

public sealed record AdminCaseCreateRequest(
    string Type,
    string Title,
    string Description,
    string? Source,
    string Severity,
    string Priority,
    string? AgencyCode,
    IReadOnlyList<Guid>? DeviceIds,
    string? AssignedTo,
    string? InstitutionalOwner,
    DateTimeOffset? DueAtUtc,
    string? SourceKind,
    string? SourceKey,
    string? SourceEntityId,
    JsonElement? SourceSnapshot);

public sealed record AdminCaseUpdateRequest(
    string Type,
    string Title,
    string Description,
    string? Source,
    string Severity,
    string Priority,
    string Status,
    string? AgencyCode,
    IReadOnlyList<Guid>? DeviceIds,
    string? AssignedTo,
    string? InstitutionalOwner,
    DateTimeOffset? DueAtUtc,
    string? ChangeNote);

public sealed record AdminCaseCommentRequest(string Comment);

public sealed record AdminCaseSummary(
    int Total,
    int Open,
    int CriticalOpen,
    int Overdue,
    int Unassigned);

public sealed record AdminCaseListItem(
    Guid Id,
    string CaseNumber,
    string Type,
    string Title,
    string Severity,
    string Priority,
    string Status,
    string? AgencyCode,
    string? AgencyName,
    string? AssignedTo,
    string? InstitutionalOwner,
    DateTimeOffset? DueAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int DeviceCount);

public sealed record AdminCasePage(
    DateTimeOffset GeneratedAtUtc,
    int Page,
    int PageSize,
    int Total,
    AdminCaseSummary Summary,
    IReadOnlyList<AdminCaseListItem> Items);

public sealed record AdminCaseDevice(
    Guid Id,
    string MachineName,
    string AgencyCode,
    string Connectivity);

public sealed record AdminCaseTimelineItem(
    long Id,
    string EventType,
    string? Comment,
    string? FromStatus,
    string? ToStatus,
    string ActorId,
    string ActorRole,
    DateTimeOffset OccurredAtUtc);

public sealed record AdminCaseDetail(
    AdminCaseListItem Case,
    string Description,
    string? Source,
    string? SourceKind,
    string? SourceKey,
    string? SourceEntityId,
    JsonElement SourceSnapshot,
    string CreatedBy,
    string UpdatedBy,
    DateTimeOffset? ClosedAtUtc,
    IReadOnlyList<AdminCaseDevice> Devices,
    IReadOnlyList<AdminCaseTimelineItem> Timeline);

public static class CaseInput
{
    public static bool TryNormalize(
        AdminCaseCreateRequest request,
        out AdminCaseCreateRequest normalized)
    {
        string type = NormalizeCode(request.Type);
        string severity = NormalizeCode(request.Severity);
        string priority = NormalizeCode(request.Priority);
        string? sourceKind = NormalizeOptionalCode(request.SourceKind);
        string? sourceKey = NormalizeOptional(request.SourceKey, 255);
        normalized = request with
        {
            Type = type,
            Title = NormalizeText(request.Title, 180),
            Description = NormalizeText(request.Description, 8000),
            Source = NormalizeOptional(request.Source, 120),
            Severity = severity,
            Priority = priority,
            AgencyCode = NormalizeOptionalCode(request.AgencyCode),
            DeviceIds = NormalizeDeviceIds(request.DeviceIds),
            AssignedTo = NormalizeOptional(request.AssignedTo, 160),
            InstitutionalOwner = NormalizeOptional(request.InstitutionalOwner, 160),
            SourceKind = sourceKind,
            SourceKey = sourceKey,
            SourceEntityId = NormalizeOptional(request.SourceEntityId, 128)
        };
        bool validSource = (sourceKind is null && sourceKey is null) ||
                           (sourceKind is not null && sourceKey is not null &&
                            sourceKind.Length <= 32);
        bool validSnapshot = request.SourceSnapshot is null ||
            ((request.SourceSnapshot.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null) &&
             request.SourceSnapshot.Value.GetRawText().Length <= 16000);
        return CaseTypes.All.Contains(type) &&
               CaseSeverities.All.Contains(severity) &&
               CasePriorities.All.Contains(priority) &&
               normalized.Title.Length is > 0 and <= 180 &&
               normalized.Description.Length is > 0 and <= 8000 &&
               (normalized.AgencyCode is null || normalized.AgencyCode.Length <= 64) &&
               normalized.DeviceIds?.Count <= 100 &&
               validSource && validSnapshot;
    }

    public static bool TryNormalize(
        AdminCaseUpdateRequest request,
        out AdminCaseUpdateRequest normalized)
    {
        string type = NormalizeCode(request.Type);
        string severity = NormalizeCode(request.Severity);
        string priority = NormalizeCode(request.Priority);
        string status = NormalizeCode(request.Status);
        normalized = request with
        {
            Type = type,
            Title = NormalizeText(request.Title, 180),
            Description = NormalizeText(request.Description, 8000),
            Source = NormalizeOptional(request.Source, 120),
            Severity = severity,
            Priority = priority,
            Status = status,
            AgencyCode = NormalizeOptionalCode(request.AgencyCode),
            DeviceIds = NormalizeDeviceIds(request.DeviceIds),
            AssignedTo = NormalizeOptional(request.AssignedTo, 160),
            InstitutionalOwner = NormalizeOptional(request.InstitutionalOwner, 160),
            ChangeNote = NormalizeOptional(request.ChangeNote, 2000)
        };
        return CaseTypes.All.Contains(type) &&
               CaseSeverities.All.Contains(severity) &&
               CasePriorities.All.Contains(priority) &&
               CaseStatuses.All.Contains(status) &&
               normalized.Title.Length is > 0 and <= 180 &&
               normalized.Description.Length is > 0 and <= 8000 &&
               (normalized.AgencyCode is null || normalized.AgencyCode.Length <= 64) &&
               normalized.DeviceIds?.Count <= 100;
    }

    public static string? NormalizeComment(string? value) =>
        NormalizeOptional(value, 4000);

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NormalizeOptionalCode(string? value)
    {
        string normalized = NormalizeCode(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeText(string? value, int maxLength)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        string normalized = NormalizeText(value, maxLength);
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyList<Guid> NormalizeDeviceIds(
        IReadOnlyList<Guid>? values) =>
        (values ?? []).Where(value => value != Guid.Empty).Distinct().Take(101).ToArray();
}
