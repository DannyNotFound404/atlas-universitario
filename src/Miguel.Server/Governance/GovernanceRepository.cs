using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Governance;

public enum GovernanceDocumentUpdateResult
{
    Updated,
    NotFound,
    Locked,
    InvalidRelationship,
    HierarchyTooDeep
}

public enum GovernanceDescriptionUpdateResult
{
    Updated,
    NotFound,
    RequiresApprovedDocument
}

public sealed record GovernanceDescriptionUpdateOutcome(
    GovernanceDescriptionUpdateResult Result,
    AdminGovernanceDescriptionRequest? Previous);

public sealed class GovernanceHierarchyException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public enum GovernanceStatusUpdateResult
{
    Updated,
    NotFound,
    InvalidTransition,
    VersionRequired,
    PdfRequired
}

public enum GovernanceCorrectionResult
{
    Corrected,
    NotFound,
    InvalidTransition,
    VersionRequired
}

public sealed record GovernanceRejectedVersion(
    Guid Id,
    string VersionLabel,
    string OriginalFileName,
    string StoredRelativePath,
    long SizeBytes,
    string Sha256);

public sealed record GovernanceCorrectionOutcome(
    GovernanceCorrectionResult Result,
    IReadOnlyList<GovernanceRejectedVersion> RejectedVersions);

public sealed record GovernanceDocumentFile(
    string StoredRelativePath,
    string OriginalFileName,
    string MediaType);

public sealed class GovernanceRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IOptions<ServerOptions> options)
{
    private readonly TimeZoneInfo operationalTimeZone = ResolveTimeZone(
        options.Value.OperationalTimeZoneId);

    public async Task<AdminGovernanceDashboard> ReadDashboardAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly reviewBoundary = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, operationalTimeZone).Date.AddDays(30));
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        IReadOnlyList<AdminGovernanceDocumentView> documents =
            await ReadDocumentsAsync(connection, reviewBoundary, cancellationToken);
        return new AdminGovernanceDashboard(
            now,
            new AdminGovernanceSummary(
                documents.Count,
                documents.Count(item => item.Status == GovernanceDocumentStatuses.Approved),
                documents.Count(item => item.ReviewDue),
                documents.Count(item => item.Status == GovernanceDocumentStatuses.Draft),
                documents.Count(item => item.Status == GovernanceDocumentStatuses.InReview),
                documents.Count(item => item.Status == GovernanceDocumentStatuses.Obsolete),
                documents.Count(item => item.DocumentType is
                    GovernanceDocumentTypes.MasterPolicy or GovernanceDocumentTypes.Policy),
                documents.Count(item => item.DocumentType == GovernanceDocumentTypes.Procedure),
                documents.Sum(item => item.VersionCount)),
            documents);
    }

    public async Task<AdminGovernanceDocumentDetail?> ReadDocumentAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        DateOnly reviewBoundary = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(
                timeProvider.GetUtcNow(), operationalTimeZone).Date.AddDays(30));
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        AdminGovernanceDocumentView? document = await ReadDocumentAsync(
            connection, id, reviewBoundary, cancellationToken);
        if (document is null)
        {
            return null;
        }

        const string versionsSql = """
            SELECT id, document_id, version_label, original_file_name,
                   media_type, size_bytes, sha256, change_summary, is_current,
                   published_at_utc, published_by, created_at_utc, created_by
            FROM governance_document_versions
            WHERE document_id = $1
            ORDER BY created_at_utc DESC, id DESC;
            """;
        List<AdminGovernanceVersionView> versions = [];
        await using (NpgsqlCommand command = new(versionsSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                versions.Add(new AdminGovernanceVersionView(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                    reader.GetString(6), reader.GetString(7), reader.GetBoolean(8),
                    GetNullableDateTimeOffset(reader, 9), GetNullableString(reader, 10),
                    reader.GetFieldValue<DateTimeOffset>(11), reader.GetString(12)));
            }
        }

        const string ancestorsSql = """
            WITH RECURSIVE ancestors AS
            (
                SELECT p.id, p.code, p.title, p.document_type, p.status,
                       p.parent_document_id, 1 AS depth
                FROM governance_documents d
                JOIN governance_documents p ON p.id = d.parent_document_id
                WHERE d.id = $1
                UNION ALL
                SELECT p.id, p.code, p.title, p.document_type, p.status,
                       p.parent_document_id, child.depth + 1
                FROM governance_documents p
                JOIN ancestors child ON p.id = child.parent_document_id
                WHERE child.depth < 2
            )
            SELECT id, code, title, document_type, status, depth
            FROM ancestors
            ORDER BY depth DESC;
            """;
        List<AdminGovernanceAncestorView> ancestors = [];
        await using (NpgsqlCommand command = new(ancestorsSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ancestors.Add(new AdminGovernanceAncestorView(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
            }
        }

        const string relatedSql = """
            SELECT d.id, d.code, d.title, d.document_type, d.status, l.relationship
            FROM governance_document_links l
            JOIN governance_documents d ON d.id = l.target_document_id
            WHERE l.source_document_id = $1
            UNION
            SELECT d.id, d.code, d.title, d.document_type, d.status, l.relationship
            FROM governance_document_links l
            JOIN governance_documents d ON d.id = l.source_document_id
            WHERE l.target_document_id = $1
            ORDER BY 2, 3;
            """;
        List<AdminGovernanceRelatedDocumentView> related = [];
        await using (NpgsqlCommand command = new(relatedSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                related.Add(new AdminGovernanceRelatedDocumentView(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
        }
        return new AdminGovernanceDocumentDetail(
            document, versions, ancestors, related);
    }

    public async Task<AdminGovernanceDocumentDetail> CreateDocumentAsync(
        AdminGovernanceDocumentRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (request.ParentDocumentId is Guid parentId)
        {
            GovernanceParentValidation parentValidation =
                await ValidateParentAsync(
                    connection, transaction, id, parentId, cancellationToken);
            if (parentValidation != GovernanceParentValidation.Valid)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new GovernanceHierarchyException(
                    parentValidation == GovernanceParentValidation.TooDeep
                        ? "governance_document_hierarchy_too_deep"
                        : "invalid_governance_document_relationship");
            }
        }
        const string sql = """
            INSERT INTO governance_documents
                (id, code, title, document_type, summary, objective, scope,
                 owner_name, status, parent_document_id,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'DRAFT', $9,
                    $10, $11, $10, $11);
            """;
        await using (NpgsqlCommand command = new(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Code);
            command.Parameters.AddWithValue(request.Title);
            command.Parameters.AddWithValue(request.DocumentType);
            command.Parameters.AddWithValue(request.Summary);
            AddNullableText(command, request.Objective);
            AddNullableText(command, request.Scope);
            command.Parameters.AddWithValue(request.OwnerName);
            AddNullableGuid(command, request.ParentDocumentId);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplaceLinksAsync(
            connection, transaction, id, request.RelatedDocumentIds ?? [],
            actor, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadDocumentAsync(id, cancellationToken))!;
    }

    public async Task<GovernanceDocumentUpdateResult> UpdateDocumentAsync(
        Guid id,
        AdminGovernanceDocumentRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.ParentDocumentId == id ||
            (request.RelatedDocumentIds ?? []).Contains(id))
        {
            return GovernanceDocumentUpdateResult.InvalidRelationship;
        }
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        string? status;
        await using (NpgsqlCommand command = new(
            "SELECT status FROM governance_documents WHERE id = $1 FOR UPDATE;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            status = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }
        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return GovernanceDocumentUpdateResult.NotFound;
        }
        if (status is GovernanceDocumentStatuses.Approved or
            GovernanceDocumentStatuses.Obsolete)
        {
            await transaction.RollbackAsync(cancellationToken);
            return GovernanceDocumentUpdateResult.Locked;
        }
        if (request.ParentDocumentId is Guid parentId)
        {
            GovernanceParentValidation parentValidation =
                await ValidateParentAsync(
                    connection, transaction, id, parentId, cancellationToken);
            if (parentValidation != GovernanceParentValidation.Valid)
            {
                await transaction.RollbackAsync(cancellationToken);
                return parentValidation == GovernanceParentValidation.TooDeep
                    ? GovernanceDocumentUpdateResult.HierarchyTooDeep
                    : GovernanceDocumentUpdateResult.InvalidRelationship;
            }
        }

        const string sql = """
            UPDATE governance_documents
            SET code = $2, title = $3, document_type = $4, summary = $5,
                objective = $6, scope = $7, owner_name = $8,
                parent_document_id = $9, updated_at_utc = $10, updated_by = $11
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command = new(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Code);
            command.Parameters.AddWithValue(request.Title);
            command.Parameters.AddWithValue(request.DocumentType);
            command.Parameters.AddWithValue(request.Summary);
            AddNullableText(command, request.Objective);
            AddNullableText(command, request.Scope);
            command.Parameters.AddWithValue(request.OwnerName);
            AddNullableGuid(command, request.ParentDocumentId);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplaceLinksAsync(
            connection, transaction, id, request.RelatedDocumentIds ?? [],
            actor, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return GovernanceDocumentUpdateResult.Updated;
    }

    public async Task<GovernanceDescriptionUpdateOutcome> UpdateDescriptionAsync(
        Guid id,
        AdminGovernanceDescriptionRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        string? status;
        AdminGovernanceDescriptionRequest? previous;
        const string stateSql = """
            SELECT status, summary, objective, scope
            FROM governance_documents
            WHERE id = $1
            FOR UPDATE;
            """;
        await using (NpgsqlCommand command = new(stateSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(
                    GovernanceDescriptionUpdateResult.NotFound,
                    null);
            }
            status = reader.GetString(0);
            previous = new(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        if (status != GovernanceDocumentStatuses.Approved)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                GovernanceDescriptionUpdateResult.RequiresApprovedDocument,
                previous);
        }

        const string updateSql = """
            UPDATE governance_documents
            SET summary = $2, objective = $3, scope = $4,
                updated_at_utc = $5, updated_by = $6
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command = new(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Summary);
            AddNullableText(command, request.Objective);
            AddNullableText(command, request.Scope);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(GovernanceDescriptionUpdateResult.Updated, previous);
    }

    public async Task<GovernanceStatusUpdateResult> UpdateStatusAsync(
        Guid id,
        AdminGovernanceStatusRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        string? currentStatus;
        bool hasVersion;
        bool hasCurrentPdf;
        const string stateSql = """
            SELECT d.status,
                   EXISTS(SELECT 1 FROM governance_document_versions v
                          WHERE v.document_id = d.id AND v.is_current),
                   EXISTS(SELECT 1 FROM governance_document_versions v
                          WHERE v.document_id = d.id AND v.is_current
                            AND v.media_type = 'application/pdf'
                            AND lower(v.original_file_name) LIKE '%.pdf')
            FROM governance_documents d
            WHERE d.id = $1
            FOR UPDATE;
            """;
        await using (NpgsqlCommand command = new(stateSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return GovernanceStatusUpdateResult.NotFound;
            }
            currentStatus = reader.GetString(0);
            hasVersion = reader.GetBoolean(1);
            hasCurrentPdf = reader.GetBoolean(2);
        }
        if (!IsAllowedTransition(currentStatus!, request.Status))
        {
            await transaction.RollbackAsync(cancellationToken);
            return GovernanceStatusUpdateResult.InvalidTransition;
        }
        if ((request.Status is GovernanceDocumentStatuses.InReview or
                GovernanceDocumentStatuses.Approved) &&
            !hasVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return GovernanceStatusUpdateResult.VersionRequired;
        }
        if (request.Status == GovernanceDocumentStatuses.Approved &&
            !hasCurrentPdf)
        {
            await transaction.RollbackAsync(cancellationToken);
            return GovernanceStatusUpdateResult.PdfRequired;
        }

        const string updateSql = """
            UPDATE governance_documents
            SET status = $2,
                approver_name = CASE WHEN $2 = 'APPROVED' THEN $3
                                     ELSE approver_name END,
                effective_date = CASE WHEN $2 = 'APPROVED' THEN $4
                                      ELSE effective_date END,
                next_review_date = CASE WHEN $2 = 'APPROVED' THEN $5
                                        ELSE next_review_date END,
                updated_at_utc = $6, updated_by = $7
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command = new(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Status);
            AddNullableText(command, request.ApproverName);
            AddNullableDate(command, request.EffectiveDate);
            AddNullableDate(command, request.NextReviewDate);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (request.Status == GovernanceDocumentStatuses.Approved)
        {
            const string publishSql = """
                UPDATE governance_document_versions
                SET published_at_utc = COALESCE(published_at_utc, $2),
                    published_by = COALESCE(published_by, $3)
                WHERE document_id = $1 AND is_current;
                """;
            await using NpgsqlCommand command =
                new(publishSql, connection, transaction);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return GovernanceStatusUpdateResult.Updated;
    }

    public async Task<bool> AddVersionAsync(
        Guid documentId,
        Guid versionId,
        GovernanceVersionUpload upload,
        string storedRelativePath,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        bool exists;
        await using (NpgsqlCommand command = new(
            "SELECT 1 FROM governance_documents WHERE id = $1 FOR UPDATE;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            exists = await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using (NpgsqlCommand command = new(
            "UPDATE governance_document_versions SET is_current = FALSE WHERE document_id = $1 AND is_current;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string insertSql = """
            INSERT INTO governance_document_versions
                (id, document_id, version_label, original_file_name, media_type,
                 stored_relative_path, size_bytes, sha256, change_summary,
                 is_current, created_at_utc, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, TRUE, $10, $11);
            """;
        await using (NpgsqlCommand command = new(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue(versionId);
            command.Parameters.AddWithValue(documentId);
            command.Parameters.AddWithValue(upload.VersionLabel);
            command.Parameters.AddWithValue(upload.FileName);
            command.Parameters.AddWithValue(upload.MediaType);
            command.Parameters.AddWithValue(storedRelativePath);
            command.Parameters.AddWithValue((long)upload.Content.Length);
            command.Parameters.AddWithValue(upload.Sha256);
            command.Parameters.AddWithValue(upload.ChangeSummary);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string documentSql = """
            UPDATE governance_documents
            SET status = 'DRAFT', approver_name = NULL, effective_date = NULL,
                next_review_date = NULL, updated_at_utc = $2, updated_by = $3
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command = new(documentSql, connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<GovernanceCorrectionOutcome> RequestCorrectionAsync(
        Guid documentId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        string? status;
        await using (NpgsqlCommand command = new(
            "SELECT status FROM governance_documents WHERE id = $1 FOR UPDATE;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            status = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }
        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(GovernanceCorrectionResult.NotFound, []);
        }
        if (status != GovernanceDocumentStatuses.InReview)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(GovernanceCorrectionResult.InvalidTransition, []);
        }

        const string versionsSql = """
            SELECT id, version_label, original_file_name, stored_relative_path,
                   size_bytes, sha256
            FROM governance_document_versions
            WHERE document_id = $1 AND published_at_utc IS NULL
            ORDER BY created_at_utc, id
            FOR UPDATE;
            """;
        List<GovernanceRejectedVersion> rejected = [];
        await using (NpgsqlCommand command =
            new(versionsSql, connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rejected.Add(new GovernanceRejectedVersion(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
            }
        }
        if (rejected.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(GovernanceCorrectionResult.VersionRequired, []);
        }

        await using (NpgsqlCommand command = new(
            "DELETE FROM governance_document_versions WHERE document_id = $1 AND published_at_utc IS NULL;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string documentSql = """
            UPDATE governance_documents
            SET status = 'DRAFT', approver_name = NULL, effective_date = NULL,
                next_review_date = NULL, updated_at_utc = $2, updated_by = $3
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command =
            new(documentSql, connection, transaction))
        {
            command.Parameters.AddWithValue(documentId);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(GovernanceCorrectionResult.Corrected, rejected);
    }

    public async Task<GovernanceDocumentFile?> ReadVersionFileAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT stored_relative_path, original_file_name, media_type
            FROM governance_document_versions
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(versionId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GovernanceDocumentFile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task<IReadOnlyList<AdminGovernanceDocumentView>> ReadDocumentsAsync(
        NpgsqlConnection connection,
        DateOnly reviewBoundary,
        CancellationToken cancellationToken)
    {
        string sql = DocumentSelectSql + "\n" + """
            ORDER BY
                CASE d.status WHEN 'APPROVED' THEN 0 WHEN 'IN_REVIEW' THEN 1
                              WHEN 'DRAFT' THEN 2 ELSE 3 END,
                CASE d.document_type WHEN 'MASTER_POLICY' THEN 0 WHEN 'POLICY' THEN 1
                                     WHEN 'PROCEDURE' THEN 2 ELSE 3 END,
                d.code, d.title;
            """;
        List<AdminGovernanceDocumentView> result = [];
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(reviewBoundary);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDocument(reader));
        }
        return result;
    }

    private static async Task<AdminGovernanceDocumentView?> ReadDocumentAsync(
        NpgsqlConnection connection,
        Guid id,
        DateOnly reviewBoundary,
        CancellationToken cancellationToken)
    {
        string sql = DocumentSelectSql + " WHERE d.id = $2;";
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(reviewBoundary);
        command.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    private const string DocumentSelectSql = """
        SELECT d.id, d.code, d.title, d.document_type, d.summary,
               d.objective, d.scope, d.owner_name, d.approver_name, d.status,
               d.parent_document_id, p.code, p.title,
               d.effective_date, d.next_review_date,
               (SELECT count(*) FROM governance_documents c
                WHERE c.parent_document_id = d.id)::integer,
               ((SELECT count(*) FROM governance_document_links l
                 WHERE l.source_document_id = d.id OR l.target_document_id = d.id))::integer,
               (SELECT count(*) FROM governance_document_versions av
                WHERE av.document_id = d.id)::integer,
               v.id, v.version_label, v.original_file_name, v.sha256,
               v.size_bytes, v.published_at_utc,
               (d.status = 'APPROVED' AND d.next_review_date IS NOT NULL AND
                d.next_review_date <= $1),
               d.updated_at_utc, d.updated_by
        FROM governance_documents d
        LEFT JOIN governance_documents p ON p.id = d.parent_document_id
        LEFT JOIN LATERAL
        (
            SELECT id, version_label, original_file_name, sha256,
                   size_bytes, published_at_utc
            FROM governance_document_versions
            WHERE document_id = d.id AND is_current
            LIMIT 1
        ) v ON TRUE
        """;

    private static AdminGovernanceDocumentView ReadDocument(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), GetNullableString(reader, 5),
            GetNullableString(reader, 6), reader.GetString(7),
            GetNullableString(reader, 8), reader.GetString(9),
            GetNullableGuid(reader, 10), GetNullableString(reader, 11),
            GetNullableString(reader, 12), GetNullableDateOnly(reader, 13),
            GetNullableDateOnly(reader, 14), reader.GetInt32(15),
            reader.GetInt32(16), reader.GetInt32(17), GetNullableGuid(reader, 18),
            GetNullableString(reader, 19), GetNullableString(reader, 20),
            GetNullableString(reader, 21), GetNullableInt64(reader, 22),
            GetNullableDateTimeOffset(reader, 23), reader.GetBoolean(24),
            reader.GetFieldValue<DateTimeOffset>(25), reader.GetString(26));

    private static async Task ReplaceLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        IReadOnlyList<Guid> relatedIds,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand delete = new(
            "DELETE FROM governance_document_links WHERE source_document_id = $1;",
            connection, transaction))
        {
            delete.Parameters.AddWithValue(documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        const string sql = """
            INSERT INTO governance_document_links
                (source_document_id, target_document_id, relationship,
                 created_at_utc, created_by)
            SELECT $1, $2, 'RELATED', $3, $4
            WHERE NOT EXISTS
            (
                SELECT 1 FROM governance_document_links
                WHERE source_document_id = $2 AND target_document_id = $1
            );
            """;
        foreach (Guid relatedId in relatedIds.Distinct())
        {
            await using NpgsqlCommand insert = new(sql, connection, transaction);
            insert.Parameters.AddWithValue(documentId);
            insert.Parameters.AddWithValue(relatedId);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(actor);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<GovernanceParentValidation> ValidateParentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        Guid proposedParentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH RECURSIVE ancestors AS
            (
                SELECT id, parent_document_id, 1 AS depth, ARRAY[id] AS path
                FROM governance_documents
                WHERE id = $1
                UNION ALL
                SELECT parent.id, parent.parent_document_id,
                       child.depth + 1, child.path || parent.id
                FROM governance_documents parent
                JOIN ancestors child ON parent.id = child.parent_document_id
                WHERE child.depth < 4
                  AND NOT parent.id = ANY(child.path)
            )
            SELECT count(*)::integer,
                   COALESCE(max(depth), 0)::integer,
                   COALESCE(bool_or(id = $2), FALSE)
            FROM ancestors;
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(proposedParentId);
        command.Parameters.AddWithValue(documentId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        int count = reader.GetInt32(0);
        int depth = reader.GetInt32(1);
        bool createsCycle = reader.GetBoolean(2);
        if (count == 0 || createsCycle)
        {
            return GovernanceParentValidation.Invalid;
        }
        return depth > 2
            ? GovernanceParentValidation.TooDeep
            : GovernanceParentValidation.Valid;
    }

    private enum GovernanceParentValidation
    {
        Valid,
        Invalid,
        TooDeep
    }

    private static bool IsAllowedTransition(string current, string target) =>
        (current, target) switch
        {
            (GovernanceDocumentStatuses.Draft, GovernanceDocumentStatuses.InReview) => true,
            (GovernanceDocumentStatuses.InReview, GovernanceDocumentStatuses.Draft) => true,
            (GovernanceDocumentStatuses.InReview, GovernanceDocumentStatuses.Approved) => true,
            (GovernanceDocumentStatuses.Approved, GovernanceDocumentStatuses.Obsolete) => true,
            _ => false
        };

    private static string? GetNullableString(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static Guid? GetNullableGuid(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetGuid(index);

    private static long? GetNullableInt64(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static DateOnly? GetNullableDateOnly(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetFieldValue<DateOnly>(index);

    private static DateTimeOffset? GetNullableDateTimeOffset(
        NpgsqlDataReader reader,
        int index) =>
        reader.IsDBNull(index)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(index);

    private static void AddNullableText(NpgsqlCommand command, string? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text, (object?)value ?? DBNull.Value);

    private static void AddNullableGuid(NpgsqlCommand command, Guid? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid, (object?)value ?? DBNull.Value);

    private static void AddNullableDate(NpgsqlCommand command, DateOnly? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.Date, (object?)value ?? DBNull.Value);

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
