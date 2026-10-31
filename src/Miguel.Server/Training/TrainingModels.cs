namespace Miguel.Server.Training;

public static class TrainingCycleStatuses
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Closed = "CLOSED";
    public const string Archived = "ARCHIVED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Draft, Active, Closed, Archived], StringComparer.Ordinal);
}

public static class TrainingSessionStatuses
{
    public const string Draft = "DRAFT";
    public const string Planned = "PLANNED";
    public const string Completed = "COMPLETED";
    public const string Rescheduled = "RESCHEDULED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Draft, Planned, Completed, Rescheduled, Cancelled],
        StringComparer.Ordinal);
}

public static class TrainingModalities
{
    public const string Virtual = "VIRTUAL";
    public const string InPerson = "IN_PERSON";
    public const string Hybrid = "HYBRID";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Virtual, InPerson, Hybrid], StringComparer.Ordinal);
}

public static class TrainingAttendanceStatuses
{
    public const string Pending = "PENDING";
    public const string Present = "PRESENT";
    public const string Absent = "ABSENT";
    public const string Excused = "EXCUSED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Pending, Present, Absent, Excused], StringComparer.Ordinal);
}

public sealed record AdminTrainingSummary(
    int CycleCount,
    int TopicCount,
    int ParticipantCount,
    int SessionCount,
    int CompletedSessions,
    int OverdueSessions,
    int PlannedSessions,
    int AttendancePresent,
    int AttendanceExpected);

public sealed record AdminTrainingCycleView(
    Guid Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    string? Notes,
    int SessionCount,
    int CompletedSessions,
    int AttendancePresent,
    int AttendanceExpected,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminTrainingTopicView(
    Guid Id,
    string Code,
    string Title,
    string SecurityFocus,
    string Category,
    int DefaultDurationMinutes,
    string DefaultModality,
    bool Active,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminTrainingSessionView(
    Guid Id,
    Guid CycleId,
    Guid TopicId,
    int SequenceNumber,
    DateOnly ScheduledDate,
    TimeOnly? StartTime,
    string Title,
    string SecurityFocus,
    string Category,
    string Modality,
    int DurationMinutes,
    string Status,
    string? Trainer,
    string? MaterialReference,
    string? PresentationUrl,
    string? AttendanceFormUrl,
    string? Notes,
    int PendingAttendance,
    int PresentAttendance,
    int AbsentAttendance,
    int ExcusedAttendance,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminTrainingParticipantView(
    Guid Id,
    string? AccountName,
    string DisplayName,
    string? Email,
    string? Department,
    string? AgencyCode,
    string? AgencyName,
    bool Active,
    int AttendedSessions,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminTrainingAttendanceView(
    Guid ParticipantId,
    string DisplayName,
    string? Email,
    string Agency,
    string Department,
    DateTimeOffset RespondedAtUtc,
    int SourceRowNumber,
    Guid ImportId);

public sealed record AdminTrainingAttendanceImportView(
    Guid Id,
    Guid SessionId,
    string OriginalFileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    int SourceRowCount,
    int AcceptedRowCount,
    int DuplicateRowCount,
    int RejectedRowCount,
    IReadOnlyDictionary<string, string> HeaderMapping,
    IReadOnlyList<string> Warnings,
    bool IsCurrent,
    DateTimeOffset ImportedAtUtc,
    string ImportedBy);

public sealed record AdminTrainingDashboard(
    DateTimeOffset GeneratedAtUtc,
    Guid? SelectedCycleId,
    AdminTrainingSummary Summary,
    IReadOnlyList<AdminTrainingCycleView> Cycles,
    IReadOnlyList<AdminTrainingSessionView> Sessions,
    IReadOnlyList<AdminTrainingTopicView> Topics,
    IReadOnlyList<AdminTrainingParticipantView> Participants);

public sealed record AdminTrainingSessionDetail(
    AdminTrainingSessionView Session,
    IReadOnlyList<AdminTrainingAttendanceView> Attendance,
    IReadOnlyList<AdminTrainingAttendanceImportView> Imports);

public sealed record AdminTrainingCycleRequest(
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    string? Notes);

public sealed record AdminTrainingCycleCloneRequest(
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Notes);

public sealed record AdminTrainingTopicRequest(
    string Code,
    string Title,
    string SecurityFocus,
    string Category,
    int DefaultDurationMinutes,
    string DefaultModality,
    bool Active);

public sealed record AdminTrainingSessionRequest(
    Guid CycleId,
    Guid TopicId,
    int SequenceNumber,
    DateOnly ScheduledDate,
    TimeOnly? StartTime,
    string Title,
    string SecurityFocus,
    string Category,
    string Modality,
    int DurationMinutes,
    string Status,
    string? Trainer,
    string? MaterialReference,
    string? PresentationUrl,
    string? AttendanceFormUrl,
    string? Notes);

public sealed record AdminTrainingParticipantRequest(
    string? AccountName,
    string DisplayName,
    string? Email,
    string? Department,
    string? AgencyCode,
    bool Active);

public sealed record TrainingAttendanceImportRow(
    int SourceRowNumber,
    string DisplayName,
    string? Email,
    string Agency,
    string Department,
    DateTimeOffset RespondedAtUtc,
    string IdentityKey);

public sealed record TrainingAttendanceImportDocument(
    string OriginalFileName,
    string MediaType,
    string Extension,
    byte[] Content,
    string Sha256,
    int SourceRowCount,
    int DuplicateRowCount,
    int RejectedRowCount,
    IReadOnlyDictionary<string, string> HeaderMapping,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TrainingAttendanceImportRow> Rows);

public static class TrainingInput
{
    public static bool TryNormalize(
        AdminTrainingCycleRequest request,
        out AdminTrainingCycleRequest normalized)
    {
        normalized = request with
        {
            Name = Text(request.Name, 120),
            Status = Code(request.Status),
            Notes = Optional(request.Notes, 4000)
        };
        return normalized.Name.Length > 0 &&
               normalized.EndDate >= normalized.StartDate &&
               (normalized.EndDate.DayNumber - normalized.StartDate.DayNumber) <= 1096 &&
               TrainingCycleStatuses.All.Contains(normalized.Status);
    }

    public static bool TryNormalize(
        AdminTrainingCycleCloneRequest request,
        out AdminTrainingCycleCloneRequest normalized)
    {
        normalized = request with
        {
            Name = Text(request.Name, 120),
            Notes = Optional(request.Notes, 4000)
        };
        return normalized.Name.Length > 0 &&
               normalized.EndDate >= normalized.StartDate &&
               (normalized.EndDate.DayNumber - normalized.StartDate.DayNumber) <= 1096;
    }

    public static bool TryNormalize(
        AdminTrainingTopicRequest request,
        out AdminTrainingTopicRequest normalized)
    {
        normalized = request with
        {
            Code = Code(request.Code),
            Title = Text(request.Title, 180),
            SecurityFocus = Text(request.SecurityFocus, 4000),
            Category = Text(request.Category, 64),
            DefaultModality = Code(request.DefaultModality)
        };
        return normalized.Code.Length is > 0 and <= 64 &&
               normalized.Code.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character == '_') &&
               normalized.Title.Length > 0 &&
               normalized.SecurityFocus.Length > 0 &&
               normalized.Category.Length > 0 &&
               normalized.DefaultDurationMinutes is >= 5 and <= 480 &&
               TrainingModalities.All.Contains(normalized.DefaultModality);
    }

    public static bool TryNormalize(
        AdminTrainingSessionRequest request,
        out AdminTrainingSessionRequest normalized)
    {
        normalized = request with
        {
            Title = Text(request.Title, 180),
            SecurityFocus = Text(request.SecurityFocus, 4000),
            Category = Text(request.Category, 64),
            Modality = Code(request.Modality),
            Status = Code(request.Status),
            Trainer = Optional(request.Trainer, 160),
            MaterialReference = Optional(request.MaterialReference, 500),
            PresentationUrl = Optional(request.PresentationUrl, 1000),
            AttendanceFormUrl = Optional(request.AttendanceFormUrl, 1000),
            Notes = Optional(request.Notes, 4000)
        };
        return normalized.CycleId != Guid.Empty &&
               normalized.TopicId != Guid.Empty &&
               normalized.SequenceNumber is >= 1 and <= 999 &&
               normalized.Title.Length > 0 &&
               normalized.SecurityFocus.Length > 0 &&
               normalized.Category.Length > 0 &&
               normalized.DurationMinutes is >= 5 and <= 480 &&
               TrainingModalities.All.Contains(normalized.Modality) &&
               TrainingSessionStatuses.All.Contains(normalized.Status) &&
               ValidOptionalWebUrl(normalized.PresentationUrl) &&
               ValidOptionalWebUrl(normalized.AttendanceFormUrl);
    }

    public static bool TryNormalize(
        AdminTrainingParticipantRequest request,
        out AdminTrainingParticipantRequest normalized)
    {
        normalized = request with
        {
            AccountName = Optional(request.AccountName, 160),
            DisplayName = Text(request.DisplayName, 180),
            Email = Optional(request.Email, 254),
            Department = Optional(request.Department, 160),
            AgencyCode = OptionalCode(request.AgencyCode)
        };
        return normalized.DisplayName.Length > 0 &&
               (normalized.Email is null ||
                (normalized.Email.Contains('@') &&
                 !normalized.Email.Contains(' ')));
    }

    public static string NormalizeAccount(string value) =>
        value.Trim().ToUpperInvariant();

    private static bool ValidOptionalWebUrl(string? value) =>
        value is null ||
        (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
         (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));

    private static string Code(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? OptionalCode(string? value)
    {
        string normalized = Code(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Text(string? value, int maxLength)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static string? Optional(string? value, int maxLength)
    {
        string normalized = Text(value, maxLength);
        return normalized.Length == 0 ? null : normalized;
    }
}
