using System.Text.RegularExpressions;

namespace Miguel.Server.BackupControl;

public static class BackupControlScopeTypes
{
    public const string Endpoint = "ENDPOINT";
    public const string OnPremSystem = "ON_PREM_SYSTEM";
    public const string LocalExport = "LOCAL_EXPORT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Endpoint, OnPremSystem, LocalExport], StringComparer.Ordinal);
}

public static class BackupControlProviders
{
    public const string AtlasBackupNas = "ATLAS_BACKUPNAS";
    public const string Acronis = "ACRONIS";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [AtlasBackupNas, Acronis, Other], StringComparer.Ordinal);
}

public static class BackupControlModes
{
    public const string Automatic = "AUTOMATIC";
    public const string SemiAutomatic = "SEMI_AUTOMATIC";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Automatic, SemiAutomatic, Manual], StringComparer.Ordinal);
}

public static class BackupControlResponsibilities
{
    public const string TechnicalResponsible =
        "Coordinador de Ciberseguridad";
}

public static class BackupControlConfigurationModes
{
    public const string Standard = "STANDARD";
    public const string Customized = "CUSTOMIZED";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Standard, Customized, Manual], StringComparer.Ordinal);
}

public static class BackupControlExceptionTypes
{
    public const string None = "NONE";
    public const string Operational = "OPERATIONAL";
    public const string Normative = "NORMATIVE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [None, Operational, Normative], StringComparer.Ordinal);
}

public static class BackupControlFieldOrigins
{
    public const string Detected = "DETECTED";
    public const string Inherited = "INHERITED";
    public const string Suggested = "SUGGESTED";
    public const string Manual = "MANUAL";
    public const string Override = "OVERRIDE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Detected, Inherited, Suggested, Manual, Override],
        StringComparer.Ordinal);
}

public static class BackupProtectedScopeCategories
{
    public const string StandardBackupNas = "STANDARD_BACKUPNAS";
    public const string AcronisPolicy = "ACRONIS_POLICY";
    public const string Documents = "DOCUMENTS";
    public const string Desktop = "DESKTOP";
    public const string Images = "IMAGES";
    public const string Favorites = "FAVORITES";
    public const string Downloads = "DOWNLOADS";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [StandardBackupNas, AcronisPolicy, Documents, Desktop, Images,
         Favorites, Downloads, Other], StringComparer.Ordinal);
}

public static class BackupObservationSources
{
    public const string Agent = "AGENT";
    public const string AcronisEvidence = "ACRONIS_EVIDENCE";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlySet<string> ManualSources = new HashSet<string>(
        [AcronisEvidence, Manual], StringComparer.Ordinal);
}

public static class BackupControlResults
{
    public const string Skipped = "SKIPPED";
    public const string Running = "RUNNING";
    public const string Partial = "PARTIAL";
    public const string Unconfirmed = "UNCONFIRMED";
    // Observed agent results; manual evidence keeps its existing validated choices.
    public const string Cancelled = "CANCELLED";
    public const string Unknown = "UNKNOWN";
    public const string Success = "SUCCESS";
    public const string Warning = "WARNING";
    public const string Failed = "FAILED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Success, Warning, Failed], StringComparer.Ordinal);
}

public static class BackupRestorationModalities
{
    public const string File = "FILE";
    public const string Application = "APPLICATION";
    public const string System = "SYSTEM";
    public const string HotRecovery = "HOT_RECOVERY";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [File, Application, System, HotRecovery], StringComparer.Ordinal);
}

public static class BackupEvidenceTypes
{
    public const string Report = "REPORT";
    public const string Log = "LOG";
    public const string Screenshot = "SCREENSHOT";
    public const string Act = "ACT";
    public const string Export = "EXPORT";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Report, Log, Screenshot, Act, Export, Other], StringComparer.Ordinal);
}

public static class BackupApprovalStatuses
{
    public const string Active = "ACTIVE";
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Returned = "RETURNED";

    // APPROVED is retained for records created before operational activation.
    public static bool IsActive(string status) => status is Active or Approved;

    public static string ForSave(BackupControlProfileRequest request) =>
        request.Activate && request.Enabled &&
        request.ExceptionType != BackupControlExceptionTypes.Normative
            ? Active
            : Draft;
}

public static class BackupControlCompliance
{
    public const string Scheduled = "SCHEDULED";
    public const string Skipped = "SKIPPED";
    public const string Running = "RUNNING";
    public const string Partial = "PARTIAL";
    public const string Cancelled = "CANCELLED";
    public const string Compliant = "COMPLIANT";
    public const string Warning = "WARNING";
    public const string Failed = "FAILED";
    public const string Overdue = "OVERDUE";
    public const string NotConfirmed = "NOT_CONFIRMED";
    public const string DestinationMismatch = "DESTINATION_MISMATCH";
    public const string ConfigurationRequired = "CONFIGURATION_REQUIRED";
    public const string ExceptionExpired = "EXCEPTION_EXPIRED";
    public const string RestoreTestDue = "RESTORE_TEST_DUE";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Inactive = "INACTIVE";
    public const string Paused = "PAUSED";
    public const string PauseReviewDue = "PAUSE_REVIEW_DUE";
}

public sealed record BackupControlProfileRequest(
    string Code,
    string ScopeType,
    Guid? DeviceId,
    string AgencyCode,
    string SystemName,
    string ProtectedScope,
    string Provider,
    string OperationMode,
    string Technology,
    string? TaskName,
    Guid? NasApplianceId,
    string? ExpectedDestination,
    int FrequencyHours,
    int GraceMinutes,
    int? RetentionDays,
    int? RpoHours,
    int? RtoMinutes,
    int? RestoreTestIntervalDays,
    string TechnicalResponsible,
    string? ProcessOwner,
    Guid? StandardId,
    string ConfigurationMode,
    string? ConfigurationReason,
    IReadOnlyList<string>? ProtectedScopeCategories,
    string? ProtectedScopeAdditional,
    string? ProtectedScopeExclusions,
    IReadOnlyDictionary<string, string>? FieldOrigins,
    string ExceptionType,
    string? ExceptionReason,
    DateTimeOffset? ExceptionExpiresAtUtc,
    bool Enabled,
    bool Activate = false,
    IReadOnlyList<string>? ProtectedFolders = null);

public sealed record BackupControlObservationRequest(
    string Source,
    string Result,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? ExternalReference,
    string? Detail);

public sealed record BackupRestorationRequest(
    string Modality,
    string Result,
    Guid? OperationalCaseId,
    DateTimeOffset? BackupUsedAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string TestedScope,
    bool IntegrityVerified,
    bool FunctionalityVerified,
    int? RtoAchievedMinutes,
    string ExecutedBy,
    string? ValidatedBy,
    DateTimeOffset? ValidatedAtUtc,
    string? Notes,
    bool Historical,
    DateTimeOffset? NextTestDueUtc);

public sealed record BackupControlActivationRequest(DateTimeOffset ExpectedUpdatedAtUtc);

public sealed record BackupEvidenceUploadMetadata(
    string FileName,
    string EvidenceType,
    Guid? ObservationId,
    Guid? RestorationTestId,
    Guid? OperationalCaseId,
    DateTimeOffset EvidenceAtUtc,
    string? Description,
    string? Source);

public sealed record BackupControlSummary(
    int Total,
    int Compliant,
    int Attention,
    int Failed,
    int Overdue,
    int RestoreTestsDue,
    int PendingApproval,
    int CoveragePercentage, int Scheduled = 0);

public sealed record BackupControlDeviceOption(
    Guid Id,
    string MachineName,
    string AgencyCode,
    string BackupProvider,
    Guid? NasApplianceId,
    string? NasDisplayName,
    string? NasHost,
    string? NasShareName,
    string? LastReportedDestinationHost,
    string? LastReportedDestinationShare,
    string? InteractiveUserAccount = null,
    string? InteractiveUserDisplayName = null,
    DateTimeOffset? InteractiveUserCheckedAtUtc = null,
    string? WorkstationBackupPolicy = null);

public sealed record BackupControlStandardView(
    Guid Id,
    string Code,
    string Version,
    string Name,
    string Description,
    string? ScopeType,
    string? Provider,
    string? OperationMode,
    string? Technology,
    string? TaskName,
    string? ProtectedScope,
    IReadOnlyList<string> ProtectedScopeCategories,
    int? FrequencyHours,
    int? GraceMinutes,
    int? RetentionDays,
    int? RpoHours,
    int? RtoMinutes,
    int? RestoreTestIntervalDays,
    string TechnicalResponsible,
    bool IsSystem);

public sealed record BackupControlNasOption(
    Guid Id,
    string DisplayName,
    string AgencyCode,
    string Host,
    string ShareName);

public sealed record BackupControlProfileView(
    Guid Id,
    string Code,
    string ScopeType,
    Guid? DeviceId,
    string? DeviceName,
    string AgencyCode,
    string AgencyName,
    string SystemName,
    string ProtectedScope,
    string Provider,
    string OperationMode,
    string Technology,
    string? TaskName,
    Guid? NasApplianceId,
    string? NasDisplayName,
    string? NasHost,
    string? NasShareName,
    string? ExpectedDestination,
    int FrequencyHours,
    int GraceMinutes,
    int? RetentionDays,
    int? RpoHours,
    int? RtoMinutes,
    int? RestoreTestIntervalDays,
    string TechnicalResponsible,
    string? ProcessOwner,
    string ApprovalStatus,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    string? ApprovalComment,
    bool Enabled,
    Guid? LastObservationId,
    string? LastObservationSource,
    string? LastObservationResult,
    DateTimeOffset? LastObservationAtUtc,
    string? LastObservationDetail,
    string? LastExternalReference,
    Guid? LastRestorationId,
    string? LastRestorationResult,
    DateTimeOffset? LastRestorationAtUtc,
    DateTimeOffset? NextRestoreTestDueUtc,
    bool RestoreTestDue,
    int EvidenceCount,
    string Compliance,
    string ComplianceDetail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    Guid StandardId,
    string StandardCode,
    string StandardVersion,
    string StandardName,
    string ConfigurationMode,
    string? ConfigurationReason,
    IReadOnlyList<string> ProtectedScopeCategories,
    string? ProtectedScopeAdditional,
    string? ProtectedScopeExclusions,
    IReadOnlyDictionary<string, string> FieldOrigins,
    string ExceptionType,
    string? ExceptionReason,
    DateTimeOffset? ExceptionExpiresAtUtc,
    Guid? PlanId = null, int? PlanRevision = null,
    IReadOnlyList<string>? ProtectedFolders = null, bool ScopeOverride = false,
    bool TrackingPaused = false, string? PauseReason = null, DateTimeOffset? PauseReviewDueUtc = null,
    DateTimeOffset? LastAcceptableAtUtc = null, string? LastAcceptableResult = null,
    BackupAutomaticReportView? LatestExecution = null,
    bool DeviceRetired = false, DateTimeOffset? DeviceRetiredAtUtc = null,
    BackupExecutionAssessment? LatestExecutionAssessment = null,
    BackupExecutionAssessment? LastAcceptableAssessment = null,
    string? ObservationContextStatus = null, string? ObservationContextDetail = null,
    BackupScheduleStatus? ScheduleStatus = null);

public sealed record BackupExecutionAssessment(
    string Code,
    string Detail,
    bool AcceptableForControl,
    IReadOnlyList<string> MissingExpectedFolders,
    long? PendingFiles = null,
    bool PendingFilesCountComplete = false,
    IReadOnlyList<string>? PendingReasons = null);

public sealed record BackupControlObservationView(
    Guid Id,
    string Source,
    string Result,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? ExternalReference,
    string? Detail,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

public sealed record BackupRestorationView(
    Guid Id,
    Guid? OperationalCaseId,
    string Modality,
    string Result,
    DateTimeOffset? BackupUsedAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string TestedScope,
    bool IntegrityVerified,
    bool FunctionalityVerified,
    int? RtoAchievedMinutes,
    string ExecutedBy,
    string? ValidatedBy,
    DateTimeOffset? ValidatedAtUtc,
    string? Notes,
    bool Historical,
    DateTimeOffset? NextTestDueUtc,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

public sealed record BackupEvidenceView(
    Guid Id,
    Guid? ObservationId,
    Guid? RestorationTestId,
    Guid? OperationalCaseId,
    string EvidenceType,
    string OriginalFileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset EvidenceAtUtc,
    string? Description,
    string? Source,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

public sealed record BackupControlDashboardView(
    DateTimeOffset GeneratedAtUtc,
    BackupControlSummary Summary,
    IReadOnlyList<BackupControlProfileView> Profiles,
    IReadOnlyList<BackupControlStandardView> Standards,
    IReadOnlyList<BackupControlDeviceOption> Devices,
    IReadOnlyList<BackupControlNasOption> Nas,
    IReadOnlyList<BackupPlanDemonstrationSummary>? PlanDemonstrations = null);

public sealed record BackupControlProfileDetail(
    BackupControlProfileView Profile,
    IReadOnlyList<BackupControlObservationView> Observations,
    IReadOnlyList<BackupRestorationView> Restorations,
    IReadOnlyList<BackupEvidenceView> Evidence,
    IReadOnlyList<BackupAutomaticReportView>? AutomaticReports = null);

public sealed record BackupAutomaticReportView(string Reference, DateTimeOffset FinishedAtUtc,
    string Result, long FilesCopied, long FilesSkipped, string? Destination,
    IReadOnlyList<string> MissingFolders, IReadOnlyList<string> FailedFolders,
    DateTimeOffset? StartedAtUtc = null, DateTimeOffset? ReceivedAtUtc = null,
    bool Acceptable = false, Miguel.Contracts.BackupExecutionEvidence? Evidence = null,
    long BytesCopied = 0, BackupExecutionAssessment? ControlAssessment = null);

public enum BackupProfileDecisionResult
{
    Updated,
    NotFound,
    InvalidTransition,
    NormativeException
}

public static partial class BackupControlInput
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static bool TryNormalize(
        BackupControlProfileRequest request,
        out BackupControlProfileRequest normalized)
    {
        string code = NormalizeCode(request.Code);
        string scopeType = NormalizeCode(request.ScopeType);
        string agencyCode = NormalizeCode(request.AgencyCode);
        string provider = NormalizeCode(request.Provider);
        string operationMode = NormalizeCode(request.OperationMode);
        string configurationMode = NormalizeCode(request.ConfigurationMode);
        string exceptionType = NormalizeCode(request.ExceptionType);
        string[] categories = (request.ProtectedScopeCategories ?? [])
            .Select(NormalizeCode)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> origins = NormalizeOrigins(
            request.FieldOrigins);
        string[]? structuredFolders = null;
        if (request.ProtectedFolders is not null)
        {
            try { structuredFolders = request.ProtectedFolders.Count == 0 ? [] : BackupPlanInput.NormalizeFolders(request.ProtectedFolders); }
            catch (BackupWorkspaceException) { normalized = request; return false; }
        }
        normalized = request with
        {
            Code = code,
            ScopeType = scopeType,
            DeviceId = NormalizeGuid(request.DeviceId),
            AgencyCode = agencyCode,
            SystemName = NormalizeText(request.SystemName, 160),
            ProtectedScope = NormalizeText(request.ProtectedScope, 8000),
            Provider = provider,
            OperationMode = operationMode,
            Technology = NormalizeText(request.Technology, 160),
            TaskName = NormalizeOptional(request.TaskName, 160),
            NasApplianceId = NormalizeGuid(request.NasApplianceId),
            ExpectedDestination = NormalizeOptional(request.ExpectedDestination, 512),
            TechnicalResponsible =
                BackupControlResponsibilities.TechnicalResponsible,
            ProcessOwner = NormalizeOptional(request.ProcessOwner, 160),
            StandardId = NormalizeGuid(request.StandardId),
            ConfigurationMode = configurationMode,
            ConfigurationReason = configurationMode ==
                BackupControlConfigurationModes.Standard
                    ? null
                    : NormalizeOptional(request.ConfigurationReason, 1000),
            ProtectedScopeCategories = categories,
            ProtectedScopeAdditional = NormalizeOptional(
                request.ProtectedScopeAdditional, 4000),
            ProtectedScopeExclusions = NormalizeOptional(
                request.ProtectedScopeExclusions, 4000),
            FieldOrigins = origins,
            ProtectedFolders = structuredFolders,
            ExceptionType = exceptionType,
            ExceptionReason = exceptionType == BackupControlExceptionTypes.None
                ? null
                : NormalizeOptional(request.ExceptionReason, 2000),
            ExceptionExpiresAtUtc = exceptionType ==
                BackupControlExceptionTypes.None
                    ? null
                    : request.ExceptionExpiresAtUtc
        };

        return CodePattern().IsMatch(code) &&
               CodePattern().IsMatch(agencyCode) &&
               BackupControlScopeTypes.All.Contains(scopeType) &&
               BackupControlProviders.All.Contains(provider) &&
               BackupControlModes.All.Contains(operationMode) &&
               normalized.SystemName.Length is > 0 and <= 160 &&
               normalized.ProtectedScope.Length is > 0 and <= 8000 &&
               normalized.Technology.Length is > 0 and <= 160 &&
               normalized.TechnicalResponsible ==
                   BackupControlResponsibilities.TechnicalResponsible &&
               normalized.StandardId.HasValue &&
               BackupControlConfigurationModes.All.Contains(configurationMode) &&
               (configurationMode == BackupControlConfigurationModes.Standard ||
                normalized.ConfigurationReason is not null) &&
               categories.All(BackupProtectedScopeCategories.All.Contains) &&
               origins.Values.All(BackupControlFieldOrigins.All.Contains) &&
               BackupControlExceptionTypes.All.Contains(exceptionType) &&
               IsValidException(normalized) &&
               (!request.Activate ||
                (request.Enabled && exceptionType != BackupControlExceptionTypes.Normative &&
                 (provider != BackupControlProviders.AtlasBackupNas ||
                  normalized.NasApplianceId.HasValue))) &&
               request.FrequencyHours is >= 1 and <= 8760 &&
               request.GraceMinutes is >= 0 and <= 10080 &&
               request.RetentionDays.HasValue &&
               InRange(request.RetentionDays, 1, 3650) &&
               (request.RetentionDays == 180 ||
                exceptionType == BackupControlExceptionTypes.Normative) &&
               request.RpoHours.HasValue &&
               InRange(request.RpoHours, 1, 8760) &&
               request.RtoMinutes.HasValue &&
               InRange(request.RtoMinutes, 1, 10080) &&
               request.RestoreTestIntervalDays.HasValue &&
               InRange(request.RestoreTestIntervalDays, 1, 730) &&
               normalized.TaskName is not null &&
               normalized.ProcessOwner is not null &&
               normalized.ExpectedDestination is not null &&
               (provider != BackupControlProviders.AtlasBackupNas ||
                normalized.DeviceId.HasValue);
    }

    private static Dictionary<string, string> NormalizeOrigins(
        IReadOnlyDictionary<string, string>? values)
    {
        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach ((string key, string value) in values ??
                 new Dictionary<string, string>())
        {
            string normalizedKey = NormalizeText(key, 64);
            string normalizedValue = NormalizeCode(value);
            if (CodePattern().IsMatch(normalizedKey.ToUpperInvariant()) &&
                BackupControlFieldOrigins.All.Contains(normalizedValue))
            {
                normalized[normalizedKey] = normalizedValue;
            }
        }
        return normalized;
    }

    private static bool IsValidException(BackupControlProfileRequest request) =>
        request.ExceptionType switch
        {
            BackupControlExceptionTypes.None =>
                request.ExceptionReason is null &&
                request.ExceptionExpiresAtUtc is null,
            BackupControlExceptionTypes.Operational =>
                request.ExceptionReason is not null &&
                request.ExceptionExpiresAtUtc.HasValue,
            BackupControlExceptionTypes.Normative =>
                request.ExceptionReason is not null,
            _ => false
        };

    public static bool TryNormalize(
        BackupControlObservationRequest request,
        out BackupControlObservationRequest normalized)
    {
        string source = NormalizeCode(request.Source);
        string result = NormalizeCode(request.Result);
        normalized = request with
        {
            Source = source,
            Result = result,
            ExternalReference = NormalizeOptional(request.ExternalReference, 255),
            Detail = NormalizeOptional(request.Detail, 2000)
        };
        return BackupObservationSources.ManualSources.Contains(source) &&
               BackupControlResults.All.Contains(result) &&
               request.PeriodEndUtc >= request.PeriodStartUtc &&
               request.PeriodEndUtc - request.PeriodStartUtc <= TimeSpan.FromDays(366) &&
               (!request.StartedAtUtc.HasValue || !request.FinishedAtUtc.HasValue ||
                request.FinishedAtUtc >= request.StartedAtUtc);
    }

    public static bool TryNormalize(
        BackupRestorationRequest request,
        out BackupRestorationRequest normalized)
    {
        string modality = NormalizeCode(request.Modality);
        string result = NormalizeCode(request.Result);
        normalized = request with
        {
            Modality = modality,
            Result = result,
            OperationalCaseId = NormalizeGuid(request.OperationalCaseId),
            TestedScope = NormalizeText(request.TestedScope, 8000),
            ExecutedBy = NormalizeText(request.ExecutedBy, 160),
            ValidatedBy = NormalizeOptional(request.ValidatedBy, 160),
            Notes = NormalizeOptional(request.Notes, 4000)
        };
        return BackupRestorationModalities.All.Contains(modality) &&
               BackupControlResults.All.Contains(result) &&
               request.FinishedAtUtc >= request.StartedAtUtc &&
               normalized.TestedScope.Length is > 0 and <= 8000 &&
               normalized.ExecutedBy.Length is > 0 and <= 160 &&
               InRange(request.RtoAchievedMinutes, 0, 10080) &&
               (result != BackupControlResults.Success ||
                request.IntegrityVerified && request.FunctionalityVerified) &&
               (!request.ValidatedAtUtc.HasValue || normalized.ValidatedBy is not null) &&
               (!request.NextTestDueUtc.HasValue ||
                request.NextTestDueUtc > request.FinishedAtUtc);
    }

    public static bool TryNormalizeEvidence(
        BackupEvidenceUploadMetadata metadata,
        out BackupEvidenceUploadMetadata normalized)
    {
        string fileName = Path.GetFileName((metadata.FileName ?? string.Empty).Trim());
        string evidenceType = NormalizeCode(metadata.EvidenceType);
        normalized = metadata with
        {
            FileName = fileName,
            EvidenceType = evidenceType,
            ObservationId = NormalizeGuid(metadata.ObservationId),
            RestorationTestId = NormalizeGuid(metadata.RestorationTestId),
            OperationalCaseId = NormalizeGuid(metadata.OperationalCaseId),
            Description = NormalizeOptional(metadata.Description, 1000),
            Source = NormalizeOptional(metadata.Source, 160)
        };
        return fileName.Length is > 0 and <= 255 &&
               BackupEvidenceTypes.All.Contains(evidenceType) &&
               metadata.EvidenceAtUtc != default;
    }

    public static string? NormalizeDecisionComment(string? value) =>
        NormalizeOptional(value, 1000);

    private static Guid? NormalizeGuid(Guid? value) =>
        value is null || value == Guid.Empty ? null : value;

    private static bool InRange(int? value, int minimum, int maximum) =>
        value is null || value >= minimum && value <= maximum;

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string NormalizeText(string? value, int maximum)
    {
        string result = (value ?? string.Empty).Trim();
        return result.Length <= maximum ? result : result[..maximum];
    }

    private static string? NormalizeOptional(string? value, int maximum)
    {
        string result = NormalizeText(value, maximum);
        return result.Length == 0 ? null : result;
    }
}

public sealed record BackupControlEvaluationResult(
    string Compliance,
    string Detail,
    bool RestoreTestDue);

public static class BackupControlEvaluator
{
    public static BackupControlEvaluationResult Evaluate(
        bool enabled,
        string approvalStatus,
        string provider,
        Guid? deviceId,
        Guid? nasApplianceId,
        int frequencyHours,
        int graceMinutes,
        DateTimeOffset? lastObservationAtUtc,
        string? lastObservationResult,
        bool destinationMismatch,
        int? restoreTestIntervalDays,
        DateTimeOffset? lastRestorationAtUtc,
        DateTimeOffset? explicitNextRestoreTestDueUtc,
        string exceptionType,
        DateTimeOffset? exceptionExpiresAtUtc,
        DateTimeOffset now, string? lastRestorationResult = null)
    {
        if (!enabled)
        {
            return new(BackupControlCompliance.Inactive, "Control desactivado.", false);
        }
        if (!BackupApprovalStatuses.IsActive(approvalStatus))
        {
            return new(
                BackupControlCompliance.PendingApproval,
                "El seguimiento está pendiente de activación.",
                false);
        }
        if (provider == BackupControlProviders.AtlasBackupNas &&
            (!deviceId.HasValue || !nasApplianceId.HasValue))
        {
            return new(
                BackupControlCompliance.ConfigurationRequired,
                "El control de Atlas Backup requiere un equipo y un NAS autorizados.",
                false);
        }
        if (exceptionType == BackupControlExceptionTypes.Operational &&
            exceptionExpiresAtUtc.HasValue &&
            exceptionExpiresAtUtc.Value < now)
        {
            return new(
                BackupControlCompliance.ExceptionExpired,
                "La excepción operativa alcanzó su fecha de revisión y debe renovarse, cerrarse o formalizarse.",
                false);
        }

        bool restoreDue = (lastRestorationResult is not null && lastRestorationResult != BackupControlResults.Success) || IsRestoreDue(
            restoreTestIntervalDays,
            lastRestorationAtUtc,
            explicitNextRestoreTestDueUtc,
            now);
        if (!lastObservationAtUtc.HasValue)
        {
            return new(
                BackupControlCompliance.NotConfirmed,
                "No existe una ejecución o comprobación vigente para este control.",
                restoreDue);
        }
        if (destinationMismatch)
        {
            return new(
                BackupControlCompliance.DestinationMismatch,
                "La última ejecución reportó un destino distinto del autorizado.",
                restoreDue);
        }
        if (lastObservationResult == BackupControlResults.Cancelled)
        {
            return new(BackupControlCompliance.Cancelled,
                "La última ejecución fue cancelada; no confirma una copia completa. Revise el motivo y la última copia satisfactoria.", restoreDue);
        }
        if (lastObservationResult is BackupControlResults.Running or BackupControlResults.Skipped or BackupControlResults.Partial)
            return new(lastObservationResult, lastObservationResult switch
            {
                BackupControlResults.Running => "Copia en curso; todavía no existe un resultado final.",
                BackupControlResults.Skipped => "El usuario cerró la copia sin iniciarla. No constituye una copia realizada.",
                _ => "Copia parcial: algunas carpetas o archivos no quedaron completos."
            }, restoreDue);
        if (lastObservationResult is null or BackupControlResults.Unknown or BackupControlResults.Unconfirmed)
        {
            return new(BackupControlCompliance.NotConfirmed,
                "El resultado recibido no permite confirmar una copia completa.", restoreDue);
        }
        if (lastObservationResult == BackupControlResults.Failed)
        {
            return new(
                BackupControlCompliance.Failed,
                "La última ejecución o comprobación terminó con fallo.",
                restoreDue);
        }
        DateTimeOffset deadline = lastObservationAtUtc.Value
            .AddHours(frequencyHours)
            .AddMinutes(graceMinutes);
        if (deadline < now)
        {
            return new(
                BackupControlCompliance.Overdue,
                "La última evidencia excedió la frecuencia y el margen configurados.",
                restoreDue);
        }
        if (lastObservationResult == BackupControlResults.Warning)
        {
            return new(
                BackupControlCompliance.Warning,
                "La última ejecución o comprobación contiene advertencias.",
                restoreDue);
        }
        if (restoreDue)
        {
            return new(
                BackupControlCompliance.RestoreTestDue,
                lastRestorationResult is not null && lastRestorationResult != BackupControlResults.Success
                    ? "La copia está vigente, pero la última prueba de restauración no fue satisfactoria."
                    : "La copia está vigente, pero falta una prueba de restauración vigente.",
                true);
        }
        return new(
            BackupControlCompliance.Compliant,
            "Evidencia vigente dentro de la frecuencia aprobada.",
            false);
    }

    private static bool IsRestoreDue(
        int? intervalDays,
        DateTimeOffset? lastRestoration,
        DateTimeOffset? explicitDue,
        DateTimeOffset now)
    {
        if (explicitDue.HasValue)
        {
            return explicitDue.Value < now;
        }
        return intervalDays.HasValue &&
               (!lastRestoration.HasValue ||
                lastRestoration.Value.AddDays(intervalDays.Value) < now);
    }
}
