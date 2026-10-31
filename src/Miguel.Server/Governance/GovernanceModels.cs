namespace Miguel.Server.Governance;

public static class GovernanceDocumentTypes
{
    public const string MasterPolicy = "MASTER_POLICY";
    public const string Policy = "POLICY";
    public const string Procedure = "PROCEDURE";
    public const string Standard = "STANDARD";
    public const string Guide = "GUIDE";
    public const string Form = "FORM";
    public const string Annex = "ANNEX";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [MasterPolicy, Policy, Procedure, Standard, Guide, Form, Annex],
        StringComparer.Ordinal);
}

public static class GovernanceDocumentStatuses
{
    public const string Draft = "DRAFT";
    public const string InReview = "IN_REVIEW";
    public const string Approved = "APPROVED";
    public const string Obsolete = "OBSOLETE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Draft, InReview, Approved, Obsolete], StringComparer.Ordinal);
}

public sealed record AdminGovernanceSummary(
    int Total,
    int Approved,
    int ReviewDue,
    int Draft,
    int InReview,
    int Obsolete,
    int Policies,
    int Procedures,
    int VersionCount);

public sealed record AdminGovernanceDocumentView(
    Guid Id,
    string Code,
    string Title,
    string DocumentType,
    string Summary,
    string? Objective,
    string? Scope,
    string OwnerName,
    string? ApproverName,
    string Status,
    Guid? ParentDocumentId,
    string? ParentCode,
    string? ParentTitle,
    DateOnly? EffectiveDate,
    DateOnly? NextReviewDate,
    int ChildCount,
    int RelatedCount,
    int VersionCount,
    Guid? CurrentVersionId,
    string? CurrentVersionLabel,
    string? CurrentFileName,
    string? CurrentSha256,
    long? CurrentSizeBytes,
    DateTimeOffset? CurrentPublishedAtUtc,
    bool ReviewDue,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminGovernanceVersionView(
    Guid Id,
    Guid DocumentId,
    string VersionLabel,
    string OriginalFileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string ChangeSummary,
    bool IsCurrent,
    DateTimeOffset? PublishedAtUtc,
    string? PublishedBy,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

public sealed record AdminGovernanceRelatedDocumentView(
    Guid Id,
    string Code,
    string Title,
    string DocumentType,
    string Status,
    string Relationship);

public sealed record AdminGovernanceAncestorView(
    Guid Id,
    string Code,
    string Title,
    string DocumentType,
    string Status,
    int DepthFromDocument);

public sealed record AdminGovernanceDashboard(
    DateTimeOffset GeneratedAtUtc,
    AdminGovernanceSummary Summary,
    IReadOnlyList<AdminGovernanceDocumentView> Documents);

public sealed record AdminGovernanceDocumentDetail(
    AdminGovernanceDocumentView Document,
    IReadOnlyList<AdminGovernanceVersionView> Versions,
    IReadOnlyList<AdminGovernanceAncestorView> Ancestors,
    IReadOnlyList<AdminGovernanceRelatedDocumentView> RelatedDocuments);

public sealed record AdminGovernanceDocumentRequest(
    string Code,
    string Title,
    string DocumentType,
    string Summary,
    string? Objective,
    string? Scope,
    string OwnerName,
    Guid? ParentDocumentId,
    IReadOnlyList<Guid>? RelatedDocumentIds);

public sealed record AdminGovernanceDescriptionRequest(
    string Summary,
    string? Objective,
    string? Scope);

public sealed record AdminGovernanceStatusRequest(
    string Status,
    string? ApproverName,
    DateOnly? EffectiveDate,
    DateOnly? NextReviewDate);

public sealed record AdminGovernanceApprovalRequest(
    DateOnly EffectiveDate,
    DateOnly NextReviewDate,
    string Attestation);

public sealed record AdminGovernanceCorrectionRequest(string Reason);

public sealed record GovernanceVersionUpload(
    string FileName,
    string MediaType,
    string Extension,
    string VersionLabel,
    string ChangeSummary,
    byte[] Content,
    string Sha256);

public static class GovernanceInput
{
    public const string ApprovalAttestation =
        "He leído y apruebo este procedimiento como oficial.";

    private static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(
            [".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".txt", ".csv"],
            StringComparer.OrdinalIgnoreCase);

    public static bool TryNormalize(
        AdminGovernanceDocumentRequest request,
        out AdminGovernanceDocumentRequest normalized)
    {
        IReadOnlyList<Guid> related = (request.RelatedDocumentIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(100)
            .ToArray();
        normalized = request with
        {
            Code = Code(request.Code),
            Title = Text(request.Title, 220),
            DocumentType = Code(request.DocumentType),
            Summary = Text(request.Summary, 4000),
            Objective = Optional(request.Objective, 8000),
            Scope = Optional(request.Scope, 8000),
            OwnerName = Text(request.OwnerName, 160),
            ParentDocumentId = request.ParentDocumentId == Guid.Empty
                ? null
                : request.ParentDocumentId,
            RelatedDocumentIds = related
        };

        return normalized.Code.Length is > 0 and <= 64 &&
               normalized.Code.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '_' or '.') &&
               normalized.Title.Length > 0 &&
               normalized.Summary.Length > 0 &&
               normalized.OwnerName.Length > 0 &&
               (normalized.DocumentType != GovernanceDocumentTypes.MasterPolicy ||
                normalized.ParentDocumentId is null) &&
               GovernanceDocumentTypes.All.Contains(normalized.DocumentType);
    }

    public static bool TryNormalize(
        AdminGovernanceDescriptionRequest request,
        out AdminGovernanceDescriptionRequest normalized)
    {
        normalized = request with
        {
            Summary = Text(request.Summary, 4000),
            Objective = Optional(request.Objective, 8000),
            Scope = Optional(request.Scope, 8000)
        };
        return normalized.Summary.Length > 0;
    }

    public static bool TryNormalize(
        AdminGovernanceStatusRequest request,
        out AdminGovernanceStatusRequest normalized)
    {
        normalized = request with
        {
            Status = Code(request.Status),
            ApproverName = Optional(request.ApproverName, 160)
        };
        if (!GovernanceDocumentStatuses.All.Contains(normalized.Status))
        {
            return false;
        }
        if (normalized.Status == GovernanceDocumentStatuses.Approved)
        {
            return normalized.ApproverName is not null &&
                   normalized.EffectiveDate is not null &&
                   normalized.NextReviewDate is not null &&
                   normalized.NextReviewDate >= normalized.EffectiveDate;
        }
        return true;
    }

    public static bool TryNormalize(
        AdminGovernanceApprovalRequest request,
        out AdminGovernanceApprovalRequest normalized)
    {
        normalized = request with
        {
            Attestation = (request.Attestation ?? string.Empty).Trim()
        };
        return normalized.EffectiveDate != default &&
               normalized.NextReviewDate >= normalized.EffectiveDate &&
               string.Equals(
                   normalized.Attestation,
                   ApprovalAttestation,
                   StringComparison.Ordinal);
    }

    public static bool TryNormalize(
        AdminGovernanceCorrectionRequest request,
        out AdminGovernanceCorrectionRequest normalized)
    {
        normalized = request with { Reason = Text(request.Reason, 2000) };
        return normalized.Reason.Length is >= 5 and <= 2000;
    }

    public static bool TryNormalizeVersion(
        string? fileName,
        string? mediaType,
        string? versionLabel,
        string? changeSummary,
        byte[] content,
        string sha256,
        out GovernanceVersionUpload normalized)
    {
        string safeFileName = Path.GetFileName((fileName ?? string.Empty).Trim());
        string extension = Path.GetExtension(safeFileName).ToLowerInvariant();
        string version = Text(versionLabel, 32);
        string changes = Text(changeSummary, 2000);
        normalized = new GovernanceVersionUpload(
            safeFileName,
            Text(mediaType, 120),
            extension,
            version,
            changes,
            content,
            sha256.ToLowerInvariant());
        return safeFileName.Length is > 0 and <= 255 &&
               AllowedExtensions.Contains(extension) &&
               version.Length > 0 &&
               version.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '_' or '.') &&
               changes.Length > 0 &&
               content.Length > 0 &&
               normalized.Sha256.Length == 64 &&
               normalized.Sha256.All(char.IsAsciiHexDigit);
    }

    private static string Code(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string Text(string? value, int maxLength)
    {
        string result = (value ?? string.Empty).Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static string? Optional(string? value, int maxLength)
    {
        string result = Text(value, maxLength);
        return result.Length == 0 ? null : result;
    }
}
