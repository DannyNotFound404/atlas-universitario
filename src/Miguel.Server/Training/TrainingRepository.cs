using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace Miguel.Server.Training;

public enum TrainingSessionDeleteResult
{
    Deleted,
    NotFound,
    CompletedSession
}

public sealed class TrainingRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IOptions<ServerOptions> options)
{
    private readonly TimeZoneInfo operationalTimeZone =
        ResolveTimeZone(options.Value.OperationalTimeZoneId);

    public async Task<AdminTrainingDashboard> ReadDashboardAsync(
        Guid? requestedCycleId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        IReadOnlyList<AdminTrainingCycleView> cycles =
            await ReadCyclesAsync(connection, cancellationToken);
        Guid? selectedCycleId = requestedCycleId is not null &&
                                cycles.Any(item => item.Id == requestedCycleId)
            ? requestedCycleId
            : cycles.FirstOrDefault(item => item.Status == TrainingCycleStatuses.Active)?.Id ??
              cycles.FirstOrDefault()?.Id;

        IReadOnlyList<AdminTrainingSessionView> sessions = selectedCycleId is null
            ? []
            : await ReadSessionsAsync(connection, selectedCycleId.Value, cancellationToken);
        IReadOnlyList<AdminTrainingTopicView> topics =
            await ReadTopicsAsync(connection, cancellationToken);
        IReadOnlyList<AdminTrainingParticipantView> participants =
            await ReadParticipantsAsync(connection, cancellationToken);

        int completed = sessions.Count(item =>
            item.Status == TrainingSessionStatuses.Completed);
        int overdue = sessions.Count(item =>
            item.ScheduledDate < DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(now, operationalTimeZone).DateTime) &&
            item.Status is not TrainingSessionStatuses.Completed and
                not TrainingSessionStatuses.Cancelled);
        int planned = sessions.Count(item =>
            item.Status is TrainingSessionStatuses.Planned or
                TrainingSessionStatuses.Rescheduled or
                TrainingSessionStatuses.Draft);
        int attendancePresent = sessions.Sum(item => item.PresentAttendance);
        int attendanceExpected = sessions.Sum(item =>
            item.PendingAttendance + item.PresentAttendance +
            item.AbsentAttendance + item.ExcusedAttendance);

        return new AdminTrainingDashboard(
            now,
            selectedCycleId,
            new AdminTrainingSummary(
                cycles.Count,
                topics.Count,
                participants.Count,
                sessions.Count,
                completed,
                overdue,
                planned,
                attendancePresent,
                attendanceExpected),
            cycles,
            sessions,
            topics,
            participants);
    }

    public async Task<AdminTrainingSessionDetail?> ReadSessionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        AdminTrainingSessionView? session = await ReadSessionAsync(
            connection, id, cancellationToken);
        if (session is null) return null;

        const string attendanceSql = """
            SELECT
                a.participant_id, a.display_name, a.email,
                a.agency_label, a.department, a.responded_at_utc,
                a.source_row_number, a.import_id
            FROM training_session_attendance a
            WHERE a.session_id = $1
            ORDER BY a.responded_at_utc, a.display_name, a.source_row_number;
            """;
        List<AdminTrainingAttendanceView> attendance = [];
        await using (NpgsqlCommand command = new(attendanceSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                attendance.Add(new AdminTrainingAttendanceView(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetInt32(6),
                    reader.GetGuid(7)));
            }
        }

        const string importsSql = """
            SELECT
                id, session_id, original_file_name, media_type, size_bytes,
                sha256, source_row_count, accepted_row_count,
                duplicate_row_count, rejected_row_count,
                header_mapping::text, warnings::text, is_current,
                imported_at_utc, imported_by
            FROM training_attendance_imports
            WHERE session_id = $1
            ORDER BY imported_at_utc DESC, id DESC
            LIMIT 20;
            """;
        List<AdminTrainingAttendanceImportView> imports = [];
        await using (NpgsqlCommand command = new(importsSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                imports.Add(new AdminTrainingAttendanceImportView(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                    reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
                    reader.GetInt32(9),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        reader.GetString(10)) ?? new Dictionary<string, string>(),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [],
                    reader.GetBoolean(12),
                    reader.GetFieldValue<DateTimeOffset>(13), reader.GetString(14)));
            }
        }

        return new AdminTrainingSessionDetail(session, attendance, imports);
    }

    public async Task<AdminTrainingCycleView> CreateCycleAsync(
        AdminTrainingCycleRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (request.Status == TrainingCycleStatuses.Active)
        {
            await CloseOtherActiveCyclesAsync(
                connection, transaction, id, actor, now, cancellationToken);
        }
        const string sql = """
            INSERT INTO training_cycles
                (id, name, start_date, end_date, status, notes,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $7, $8);
            """;
        await using (NpgsqlCommand command = new(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Name);
            command.Parameters.AddWithValue(request.StartDate);
            command.Parameters.AddWithValue(request.EndDate);
            command.Parameters.AddWithValue(request.Status);
            AddNullableText(command, request.Notes);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await ReadCycleAsync(id, cancellationToken))!;
    }

    public async Task<AdminTrainingCycleView?> UpdateCycleAsync(
        Guid id,
        AdminTrainingCycleRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (request.Status == TrainingCycleStatuses.Active)
        {
            await CloseOtherActiveCyclesAsync(
                connection, transaction, id, actor, now, cancellationToken);
        }
        const string sql = """
            UPDATE training_cycles
            SET name = $2, start_date = $3, end_date = $4, status = $5,
                notes = $6, updated_at_utc = $7, updated_by = $8
            WHERE id = $1
              AND NOT EXISTS
              (
                  SELECT 1 FROM training_sessions s
                  WHERE s.cycle_id = $1
                    AND (s.scheduled_date < $3 OR s.scheduled_date > $4)
              );
            """;
        int affected;
        await using (NpgsqlCommand command = new(sql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Name);
            command.Parameters.AddWithValue(request.StartDate);
            command.Parameters.AddWithValue(request.EndDate);
            command.Parameters.AddWithValue(request.Status);
            AddNullableText(command, request.Notes);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return await ReadCycleAsync(id, cancellationToken);
    }

    public async Task<AdminTrainingCycleView?> CloneCycleAsync(
        Guid sourceId,
        AdminTrainingCycleCloneRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        DateOnly sourceStart;
        const string sourceSql =
            "SELECT start_date FROM training_cycles WHERE id = $1 FOR SHARE;";
        await using (NpgsqlCommand command = new(sourceSql, connection, transaction))
        {
            command.Parameters.AddWithValue(sourceId);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not DateOnly date)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            sourceStart = date;
        }

        Guid newCycleId = Guid.NewGuid();
        const string insertCycle = """
            INSERT INTO training_cycles
                (id, name, start_date, end_date, status, notes,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, 'DRAFT', $5, $6, $7, $6, $7);
            """;
        await using (NpgsqlCommand command = new(insertCycle, connection, transaction))
        {
            command.Parameters.AddWithValue(newCycleId);
            command.Parameters.AddWithValue(request.Name);
            command.Parameters.AddWithValue(request.StartDate);
            command.Parameters.AddWithValue(request.EndDate);
            AddNullableText(command, request.Notes);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string readSessions = """
            SELECT topic_id, sequence_number, scheduled_date, start_time,
                   title, security_focus, category, modality, duration_minutes,
                   trainer, material_reference, presentation_url, notes
            FROM training_sessions
            WHERE cycle_id = $1
            ORDER BY scheduled_date, sequence_number, id;
            """;
        List<CloneSessionData> sourceSessions = [];
        await using (NpgsqlCommand command = new(readSessions, connection, transaction))
        {
            command.Parameters.AddWithValue(sourceId);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceSessions.Add(new CloneSessionData(
                    reader.GetGuid(0), reader.GetInt32(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<TimeOnly>(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetInt32(8),
                    GetNullableString(reader, 9), GetNullableString(reader, 10),
                    GetNullableString(reader, 11), GetNullableString(reader, 12)));
            }
        }

        const string insertSession = """
            INSERT INTO training_sessions
                (id, cycle_id, topic_id, sequence_number, scheduled_date,
                 start_time, title, security_focus, category, modality,
                 duration_minutes, status, trainer, material_reference,
                 presentation_url, attendance_form_url, notes,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                 $11, 'PLANNED', $12, $13, $14, NULL, $15,
                 $16, $17, $16, $17);
            """;
        foreach (CloneSessionData source in sourceSessions)
        {
            int dayOffset = source.ScheduledDate.DayNumber - sourceStart.DayNumber;
            DateOnly targetDate = request.StartDate.AddDays(dayOffset);
            if (targetDate > request.EndDate)
            {
                targetDate = request.EndDate;
            }
            await using NpgsqlCommand command = new(
                insertSession, connection, transaction);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(newCycleId);
            command.Parameters.AddWithValue(source.TopicId);
            command.Parameters.AddWithValue(source.SequenceNumber);
            command.Parameters.AddWithValue(targetDate);
            AddNullableTime(command, source.StartTime);
            command.Parameters.AddWithValue(source.Title);
            command.Parameters.AddWithValue(source.SecurityFocus);
            command.Parameters.AddWithValue(source.Category);
            command.Parameters.AddWithValue(source.Modality);
            command.Parameters.AddWithValue(source.DurationMinutes);
            AddNullableText(command, source.Trainer);
            AddNullableText(command, source.MaterialReference);
            AddNullableText(command, source.PresentationUrl);
            AddNullableText(command, source.Notes);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ReadCycleAsync(newCycleId, cancellationToken);
    }

    public async Task<AdminTrainingTopicView> CreateTopicAsync(
        AdminTrainingTopicRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO training_topics
                (id, code, title, security_focus, category,
                 default_duration_minutes, default_modality, active,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10);
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddTopicParameters(command, id, request, actor, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await ReadTopicAsync(id, cancellationToken))!;
    }

    public async Task<AdminTrainingTopicView?> UpdateTopicAsync(
        Guid id,
        AdminTrainingTopicRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE training_topics
            SET code = $2, title = $3, security_focus = $4, category = $5,
                default_duration_minutes = $6, default_modality = $7,
                active = $8, updated_at_utc = $9, updated_by = $10
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddTopicParameters(command, id, request, actor, now);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await ReadTopicAsync(id, cancellationToken);
    }

    public async Task<AdminTrainingParticipantView> CreateParticipantAsync(
        AdminTrainingParticipantRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO training_participants
                (id, account_name, normalized_account_name, display_name,
                 email, department, agency_code, active,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10);
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddParticipantParameters(command, id, request, actor, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await ReadParticipantAsync(id, cancellationToken))!;
    }

    public async Task<AdminTrainingParticipantView?> UpdateParticipantAsync(
        Guid id,
        AdminTrainingParticipantRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE training_participants
            SET account_name = $2, normalized_account_name = $3,
                display_name = $4, email = $5, department = $6,
                agency_code = $7, active = $8,
                updated_at_utc = $9, updated_by = $10
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddParticipantParameters(command, id, request, actor, now);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0
            ? null
            : await ReadParticipantAsync(id, cancellationToken);
    }

    public async Task<AdminTrainingSessionView?> CreateSessionAsync(
        AdminTrainingSessionRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO training_sessions
                (id, cycle_id, topic_id, sequence_number, scheduled_date,
                 start_time, title, security_focus, category, modality,
                 duration_minutes, status, trainer, material_reference,
                 presentation_url, attendance_form_url, notes,
                 created_at_utc, created_by, updated_at_utc, updated_by)
            SELECT
                $1, c.id, t.id, $4, $5, $6, $7, $8, $9, $10,
                $11, $12, $13, $14, $15, $16, $17, $18, $19, $18, $19
            FROM training_cycles c
            CROSS JOIN training_topics t
            WHERE c.id = $2 AND t.id = $3
              AND $5 BETWEEN c.start_date AND c.end_date;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddSessionParameters(command, id, request, actor, now);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await ReadSessionByIdAsync(id, cancellationToken);
    }

    public async Task<AdminTrainingSessionView?> UpdateSessionAsync(
        Guid id,
        AdminTrainingSessionRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE training_sessions s
            SET cycle_id = $2, topic_id = $3, sequence_number = $4,
                scheduled_date = $5, start_time = $6, title = $7,
                security_focus = $8, category = $9, modality = $10,
                duration_minutes = $11, status = $12, trainer = $13,
                material_reference = $14, presentation_url = $15,
                attendance_form_url = $16, notes = $17,
                updated_at_utc = $18, updated_by = $19
            FROM training_cycles c, training_topics t
            WHERE s.id = $1 AND c.id = $2 AND t.id = $3
              AND $5 BETWEEN c.start_date AND c.end_date;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddSessionParameters(command, id, request, actor, now);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await ReadSessionByIdAsync(id, cancellationToken);
    }

    public async Task<TrainingSessionDeleteResult> DeleteSessionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM training_sessions
            WHERE id = $1 AND status <> 'COMPLETED';
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
        {
            return TrainingSessionDeleteResult.Deleted;
        }

        const string existsSql =
            "SELECT status FROM training_sessions WHERE id = $1;";
        await using NpgsqlCommand exists = dataSource.CreateCommand(existsSql);
        exists.Parameters.AddWithValue(id);
        object? status = await exists.ExecuteScalarAsync(cancellationToken);
        return status is null
            ? TrainingSessionDeleteResult.NotFound
            : TrainingSessionDeleteResult.CompletedSession;
    }

    public async Task<AdminTrainingSessionDetail?> ReplaceAttendanceFromImportAsync(
        Guid sessionId,
        Guid importId,
        TrainingAttendanceImportDocument document,
        string storedRelativePath,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        const string lockSql =
            "SELECT 1 FROM training_sessions WHERE id = $1 FOR UPDATE;";
        await using (NpgsqlCommand lockCommand = new(lockSql, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(sessionId);
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (NpgsqlCommand supersede = new(
                         "UPDATE training_attendance_imports SET is_current = FALSE WHERE session_id = $1 AND is_current;",
                         connection, transaction))
        {
            supersede.Parameters.AddWithValue(sessionId);
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertImport = """
            INSERT INTO training_attendance_imports
                (id, session_id, original_file_name, media_type,
                 stored_relative_path, size_bytes, sha256, source_row_count,
                 accepted_row_count, duplicate_row_count, rejected_row_count,
                 header_mapping, warnings, is_current, imported_at_utc, imported_by)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11,
                 $12, $13, TRUE, $14, $15);
            """;
        await using (NpgsqlCommand command = new(
                         insertImport, connection, transaction))
        {
            command.Parameters.AddWithValue(importId);
            command.Parameters.AddWithValue(sessionId);
            command.Parameters.AddWithValue(document.OriginalFileName);
            command.Parameters.AddWithValue(document.MediaType);
            command.Parameters.AddWithValue(storedRelativePath);
            command.Parameters.AddWithValue((long)document.Content.Length);
            command.Parameters.AddWithValue(document.Sha256);
            command.Parameters.AddWithValue(document.SourceRowCount);
            command.Parameters.AddWithValue(document.Rows.Count);
            command.Parameters.AddWithValue(document.DuplicateRowCount);
            command.Parameters.AddWithValue(document.RejectedRowCount);
            command.Parameters.AddWithValue(
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(document.HeaderMapping));
            command.Parameters.AddWithValue(
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(document.Warnings));
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand delete = new(
                         "DELETE FROM training_session_attendance WHERE session_id = $1;",
                         connection, transaction))
        {
            delete.Parameters.AddWithValue(sessionId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string upsertParticipant = """
            INSERT INTO training_participants
                (id, account_name, normalized_account_name, source_identity_key,
                 display_name, email, department, agency_code, agency_label,
                 active, created_at_utc, created_by, updated_at_utc, updated_by)
            VALUES
                ($1, NULL, NULL, $2, $3, $4, $5, NULL, $6,
                 TRUE, $7, $8, $7, $8)
            ON CONFLICT (source_identity_key)
                WHERE source_identity_key IS NOT NULL
            DO UPDATE SET
                display_name = EXCLUDED.display_name,
                email = EXCLUDED.email,
                department = EXCLUDED.department,
                agency_label = EXCLUDED.agency_label,
                active = TRUE,
                updated_at_utc = EXCLUDED.updated_at_utc,
                updated_by = EXCLUDED.updated_by
            RETURNING id;
            """;

        const string insertAttendance = """
            INSERT INTO training_session_attendance
                (session_id, participant_id, import_id, source_row_number,
                 display_name, email, agency_label, department, status,
                 responded_at_utc, checked_in_at_utc, notes,
                 recorded_at_utc, recorded_by)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, 'PRESENT',
                 $9, $9, NULL, $10, $11);
            """;
        foreach (TrainingAttendanceImportRow row in document.Rows)
        {
            Guid proposedParticipantId = Guid.NewGuid();
            Guid participantId;
            await using (NpgsqlCommand participant = new(
                             upsertParticipant, connection, transaction))
            {
                participant.Parameters.AddWithValue(proposedParticipantId);
                participant.Parameters.AddWithValue(row.IdentityKey);
                participant.Parameters.AddWithValue(row.DisplayName);
                AddNullableText(participant, row.Email);
                participant.Parameters.AddWithValue(row.Department);
                participant.Parameters.AddWithValue(row.Agency);
                participant.Parameters.AddWithValue(now);
                participant.Parameters.AddWithValue(actor);
                participantId = (Guid)(await participant.ExecuteScalarAsync(
                    cancellationToken))!;
            }

            await using (NpgsqlCommand attendance = new(
                             insertAttendance, connection, transaction))
            {
                attendance.Parameters.AddWithValue(sessionId);
                attendance.Parameters.AddWithValue(participantId);
                attendance.Parameters.AddWithValue(importId);
                attendance.Parameters.AddWithValue(row.SourceRowNumber);
                attendance.Parameters.AddWithValue(row.DisplayName);
                AddNullableText(attendance, row.Email);
                attendance.Parameters.AddWithValue(row.Agency);
                attendance.Parameters.AddWithValue(row.Department);
                attendance.Parameters.AddWithValue(row.RespondedAtUtc);
                attendance.Parameters.AddWithValue(now);
                attendance.Parameters.AddWithValue(actor);
                await attendance.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (NpgsqlCommand touch = new(
                         "UPDATE training_sessions SET updated_at_utc = $2, updated_by = $3 WHERE id = $1;",
                         connection,
                         transaction))
        {
            touch.Parameters.AddWithValue(sessionId);
            touch.Parameters.AddWithValue(now);
            touch.Parameters.AddWithValue(actor);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ReadSessionAsync(sessionId, cancellationToken);
    }

    private async Task<AdminTrainingCycleView?> ReadCycleAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        IReadOnlyList<AdminTrainingCycleView> items =
            await ReadCyclesAsync(connection, cancellationToken, id);
        return items.FirstOrDefault();
    }

    private async Task<AdminTrainingTopicView?> ReadTopicAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        IReadOnlyList<AdminTrainingTopicView> items =
            await ReadTopicsAsync(connection, cancellationToken, id);
        return items.FirstOrDefault();
    }

    private async Task<AdminTrainingParticipantView?> ReadParticipantAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        IReadOnlyList<AdminTrainingParticipantView> items =
            await ReadParticipantsAsync(connection, cancellationToken, id);
        return items.FirstOrDefault();
    }

    private async Task<AdminTrainingSessionView?> ReadSessionByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSessionAsync(connection, id, cancellationToken);
    }

    private static async Task<IReadOnlyList<AdminTrainingCycleView>> ReadCyclesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        Guid? id = null)
    {
        const string sql = """
            SELECT
                c.id, c.name, c.start_date, c.end_date, c.status, c.notes,
                COUNT(DISTINCT s.id)::int AS session_count,
                COUNT(DISTINCT s.id) FILTER (WHERE s.status = 'COMPLETED')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'PRESENT')::int,
                COUNT(a.participant_id)::int,
                c.updated_at_utc, c.updated_by
            FROM training_cycles c
            LEFT JOIN training_sessions s ON s.cycle_id = c.id
            LEFT JOIN training_session_attendance a ON a.session_id = s.id
            WHERE ($1::uuid IS NULL OR c.id = $1)
            GROUP BY c.id
            ORDER BY
                CASE c.status WHEN 'ACTIVE' THEN 0 WHEN 'DRAFT' THEN 1
                              WHEN 'CLOSED' THEN 2 ELSE 3 END,
                c.start_date DESC, c.name;
            """;
        List<AdminTrainingCycleView> items = [];
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            (object?)id ?? DBNull.Value);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AdminTrainingCycleView(
                reader.GetGuid(0), reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2), reader.GetFieldValue<DateOnly>(3),
                reader.GetString(4), GetNullableString(reader, 5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetString(11)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<AdminTrainingTopicView>> ReadTopicsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        Guid? id = null)
    {
        const string sql = """
            SELECT id, code, title, security_focus, category,
                   default_duration_minutes, default_modality, active,
                   updated_at_utc, updated_by
            FROM training_topics
            WHERE ($1::uuid IS NULL OR id = $1)
            ORDER BY active DESC, title, id;
            """;
        List<AdminTrainingTopicView> items = [];
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            (object?)id ?? DBNull.Value);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AdminTrainingTopicView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetBoolean(7),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<AdminTrainingParticipantView>> ReadParticipantsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        Guid? id = null)
    {
        const string sql = """
            SELECT
                p.id, p.account_name, p.display_name, p.email, p.department,
                p.agency_code, COALESCE(ag.display_name, p.agency_label), p.active,
                COUNT(a.session_id) FILTER (WHERE a.status = 'PRESENT')::int,
                p.updated_at_utc, p.updated_by
            FROM training_participants p
            LEFT JOIN agencies ag ON ag.code = p.agency_code
            LEFT JOIN training_session_attendance a ON a.participant_id = p.id
            WHERE ($1::uuid IS NULL OR p.id = $1)
            GROUP BY p.id, ag.display_name
            ORDER BY p.active DESC, p.display_name, p.id;
            """;
        List<AdminTrainingParticipantView> items = [];
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Uuid,
            (object?)id ?? DBNull.Value);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AdminTrainingParticipantView(
                reader.GetGuid(0), GetNullableString(reader, 1), reader.GetString(2),
                GetNullableString(reader, 3), GetNullableString(reader, 4),
                GetNullableString(reader, 5), GetNullableString(reader, 6),
                reader.GetBoolean(7), reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<AdminTrainingSessionView>> ReadSessionsAsync(
        NpgsqlConnection connection,
        Guid cycleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id, s.cycle_id, s.topic_id, s.sequence_number,
                s.scheduled_date, s.start_time, s.title, s.security_focus,
                s.category, s.modality, s.duration_minutes, s.status,
                s.trainer, s.material_reference, s.presentation_url,
                s.attendance_form_url, s.notes,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'PENDING')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'PRESENT')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'ABSENT')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'EXCUSED')::int,
                s.updated_at_utc, s.updated_by
            FROM training_sessions s
            LEFT JOIN training_session_attendance a ON a.session_id = s.id
            WHERE s.cycle_id = $1
            GROUP BY s.id
            ORDER BY s.scheduled_date, s.sequence_number, s.id;
            """;
        List<AdminTrainingSessionView> items = [];
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(cycleId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSession(reader));
        }
        return items;
    }

    private static async Task<AdminTrainingSessionView?> ReadSessionAsync(
        NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id, s.cycle_id, s.topic_id, s.sequence_number,
                s.scheduled_date, s.start_time, s.title, s.security_focus,
                s.category, s.modality, s.duration_minutes, s.status,
                s.trainer, s.material_reference, s.presentation_url,
                s.attendance_form_url, s.notes,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'PENDING')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'PRESENT')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'ABSENT')::int,
                COUNT(a.participant_id) FILTER (WHERE a.status = 'EXCUSED')::int,
                s.updated_at_utc, s.updated_by
            FROM training_sessions s
            LEFT JOIN training_session_attendance a ON a.session_id = s.id
            WHERE s.id = $1
            GROUP BY s.id;
            """;
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSession(reader)
            : null;
    }

    private static AdminTrainingSessionView ReadSession(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
            reader.GetInt32(3), reader.GetFieldValue<DateOnly>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<TimeOnly>(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetString(9), reader.GetInt32(10), reader.GetString(11),
            GetNullableString(reader, 12), GetNullableString(reader, 13),
            GetNullableString(reader, 14), GetNullableString(reader, 15),
            GetNullableString(reader, 16), reader.GetInt32(17),
            reader.GetInt32(18), reader.GetInt32(19), reader.GetInt32(20),
            reader.GetFieldValue<DateTimeOffset>(21), reader.GetString(22));

    private static async Task CloseOtherActiveCyclesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid exceptId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE training_cycles
            SET status = 'CLOSED', updated_at_utc = $2, updated_by = $3
            WHERE status = 'ACTIVE' AND id <> $1;
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(exceptId);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddTopicParameters(
        NpgsqlCommand command,
        Guid id,
        AdminTrainingTopicRequest request,
        string actor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Code);
        command.Parameters.AddWithValue(request.Title);
        command.Parameters.AddWithValue(request.SecurityFocus);
        command.Parameters.AddWithValue(request.Category);
        command.Parameters.AddWithValue(request.DefaultDurationMinutes);
        command.Parameters.AddWithValue(request.DefaultModality);
        command.Parameters.AddWithValue(request.Active);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
    }

    private static void AddParticipantParameters(
        NpgsqlCommand command,
        Guid id,
        AdminTrainingParticipantRequest request,
        string actor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(id);
        AddNullableText(command, request.AccountName);
        AddNullableText(
            command,
            request.AccountName is null
                ? null
                : TrainingInput.NormalizeAccount(request.AccountName));
        command.Parameters.AddWithValue(request.DisplayName);
        AddNullableText(command, request.Email);
        AddNullableText(command, request.Department);
        AddNullableText(command, request.AgencyCode);
        command.Parameters.AddWithValue(request.Active);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
    }

    private static void AddSessionParameters(
        NpgsqlCommand command,
        Guid id,
        AdminTrainingSessionRequest request,
        string actor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.CycleId);
        command.Parameters.AddWithValue(request.TopicId);
        command.Parameters.AddWithValue(request.SequenceNumber);
        command.Parameters.AddWithValue(request.ScheduledDate);
        AddNullableTime(command, request.StartTime);
        command.Parameters.AddWithValue(request.Title);
        command.Parameters.AddWithValue(request.SecurityFocus);
        command.Parameters.AddWithValue(request.Category);
        command.Parameters.AddWithValue(request.Modality);
        command.Parameters.AddWithValue(request.DurationMinutes);
        command.Parameters.AddWithValue(request.Status);
        AddNullableText(command, request.Trainer);
        AddNullableText(command, request.MaterialReference);
        AddNullableText(command, request.PresentationUrl);
        AddNullableText(command, request.AttendanceFormUrl);
        AddNullableText(command, request.Notes);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
    }

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

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

    private static void AddNullableText(NpgsqlCommand command, string? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            (object?)value ?? DBNull.Value);

    private static void AddNullableTime(NpgsqlCommand command, TimeOnly? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.Time,
            (object?)value ?? DBNull.Value);

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        DateTimeOffset? value) =>
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            (object?)value ?? DBNull.Value);

    private sealed record CloneSessionData(
        Guid TopicId,
        int SequenceNumber,
        DateOnly ScheduledDate,
        TimeOnly? StartTime,
        string Title,
        string SecurityFocus,
        string Category,
        string Modality,
        int DurationMinutes,
        string? Trainer,
        string? MaterialReference,
        string? PresentationUrl,
        string? Notes);
}
