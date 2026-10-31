using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using Miguel.Contracts;

namespace Miguel.Server.BackupControl;

public sealed record BackupEvidenceFileRecord(
    string StoredRelativePath,
    string OriginalFileName,
    string MediaType);

public sealed class BackupControlRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider)
{
    // V4 pp. 9-10 / reference 7.2: preserve observed execution evidence separately
    // from its operational acceptance. This is the SQL equivalent of the shared
    // BackupTelemetry.IsAcceptableWithPendingFiles predicate, not a rewrite of
    // backup_runs.acceptable, status, timestamps or original payloads.
    private const string PendingExecutionSql = """
        (b.status IN ('Partial','Failed')
          AND b.payload#>>'{evidence,version}'='2'
          AND b.payload#>>'{evidence,phase}'='FINISHED'
          AND b.payload#>>'{evidence,reason}'='PROCESS_FINISHED'
          AND COALESCE(jsonb_array_length(b.payload->'missingFolders'),0)=0
          AND COALESCE(jsonb_array_length(b.payload#>'{evidence,folders}'),0)>0
          AND NOT EXISTS (
            SELECT 1 FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f
            WHERE COALESCE((f->>'filesTotal')::bigint,-1)<0
               OR (f->>'filesTotal')::bigint>1000000000000000
               OR COALESCE((f->>'filesCopied')::bigint,-1)<0
               OR (f->>'filesCopied')::bigint>1000000000000000
               OR COALESCE((f->>'filesUnchanged')::bigint,-1)<0
               OR (f->>'filesUnchanged')::bigint>1000000000000000
               OR COALESCE((f->>'filesFailed')::bigint,-1)<0
               OR (f->>'filesFailed')::bigint>1000000000000000
               OR COALESCE((f->>'bytesCopied')::bigint,-1)<0
               OR (f->>'bytesCopied')::bigint>1000000000000000
               OR (f->>'filesTotal')::bigint<>(f->>'filesCopied')::bigint+(f->>'filesUnchanged')::bigint+(f->>'filesFailed')::bigint
               OR NOT COALESCE((f->>'state'='COMPLETED' AND (f->>'exitCode')::integer BETWEEN 0 AND 3 AND (f->>'filesFailed')::bigint=0)
                    OR (f->>'state'='FAILED' AND (f->>'exitCode')::integer BETWEEN 8 AND 11 AND (f->>'filesFailed')::bigint>0),false)
          )
          AND NOT EXISTS (
            SELECT 1 FROM jsonb_array_elements_text(COALESCE(b.payload->'failedFolders','[]'::jsonb)) failed(folder)
            WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f
              WHERE lower(f->>'name')=lower(failed.folder) AND f->>'state'='FAILED' AND (f->>'filesFailed')::bigint>0)
          )
          AND (SELECT SUM((f->>'filesFailed')::bigint) FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f)>0
          AND (SELECT SUM((f->>'filesCopied')::bigint+(f->>'filesUnchanged')::bigint) FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f)>0)
        """;

    public async Task<BackupControlDashboardView> ReadDashboardAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<BackupControlProfileView> profiles = [];
        string sql = $$"""
            SELECT p.id, p.code, p.scope_type, p.device_id, d.machine_name,
                   p.agency_code, a.display_name, p.system_name,
                   p.protected_scope, p.provider, p.operation_mode,
                   p.technology, p.task_name, p.nas_appliance_id,
                   n.display_name, n.host, n.share_name,
                   p.expected_destination, p.frequency_hours, p.grace_minutes,
                   p.retention_days, p.rpo_hours, p.rto_minutes,
                   p.restore_test_interval_days, p.technical_responsible,
                   p.process_owner, p.approval_status, p.approved_by,
                   p.approved_at_utc, p.approval_comment, p.enabled,
                   observed.id, observed.source, observed.result,
                   observed.period_end_utc, observed.detail,
                   observed.external_reference,
                   run.id, run.finished_at_utc, run.status,
                   run.payload ->> 'destinationHost',
                   run.payload ->> 'destinationShare',
                   restoration.id, restoration.result,
                   restoration.finished_at_utc,
                   restoration.next_test_due_utc,
                   COALESCE(evidence.total, 0),
                   p.created_at_utc, p.updated_at_utc, p.updated_by,
                   p.standard_id, standard.code, standard.version,
                   standard.name, p.configuration_mode,
                   p.configuration_reason, p.protected_scope_categories,
                   p.protected_scope_additional,
                   p.protected_scope_exclusions,
                   p.field_origins::text, p.exception_type,
                   p.exception_reason, p.exception_expires_at_utc,
                   p.plan_id, p.plan_revision, p.protected_folders, p.scope_override,
                   COALESCE(custody.backups_paused,false),custody.pause_reason,custody.review_due_utc,
                   run.payload::text, run.received_at_utc, run.acceptable,
                   accepted.finished_at_utc, accepted.status,
                   atlas_backup_scope_matches(p.protected_folders, run.payload),
                   p.device_id IS NOT NULL AND NOT COALESCE(d.enabled,false),d.retired_at_utc,
                   CASE WHEN run.payload->'evidence' IS NULL OR run.payload->'evidence'='null'::jsonb
                     THEN ARRAY[]::text[]
                     ELSE ARRAY(
                       SELECT expected.folder FROM unnest(p.protected_folders) WITH ORDINALITY expected(folder, position)
                       WHERE NOT EXISTS (
                         SELECT 1 FROM jsonb_array_elements(COALESCE(run.payload#>'{evidence,folders}','[]'::jsonb)) actual
                         WHERE atlas_backup_folder_key(expected.folder)=atlas_backup_folder_key(actual->>'name')
                           AND actual->>'state' IN ('COMPLETED','FAILED')
                       ) ORDER BY expected.position
                     ) END,
                   manual_accepted.period_end_utc,
                   accepted.id, accepted.payload::text, accepted.received_at_utc, accepted.acceptable,
                   CASE WHEN observed.id IS NULL THEN NULL
                     WHEN atlas_backup_evidence_context_matches(p,'OBSERVATION',observed.id,observed.context_key) THEN 'CURRENT'
                     WHEN observed.context_key IS NULL AND NOT EXISTS(SELECT 1 FROM backup_evidence_context_validations v WHERE v.record_kind='OBSERVATION' AND v.record_id=observed.id)
                       THEN 'HISTORICAL_UNVERIFIED' ELSE 'CONTEXT_CHANGED' END
            FROM backup_control_profiles p
            JOIN agencies a ON a.code = p.agency_code
            JOIN backup_control_standards standard ON standard.id = p.standard_id
            LEFT JOIN devices d ON d.id = p.device_id
            LEFT JOIN asset_custody custody ON custody.current_device_id = p.device_id
            LEFT JOIN nas_appliances n ON n.id = p.nas_appliance_id
            LEFT JOIN LATERAL
            (
                SELECT o.id, o.source, o.result, o.period_end_utc,
                       o.detail, o.external_reference,o.context_key
                FROM backup_control_observations o
                WHERE o.profile_id = p.id
                  AND (custody.tracking_since_utc IS NULL OR o.period_end_utc >= custody.tracking_since_utc)
                ORDER BY o.period_end_utc DESC, o.created_at_utc DESC
                LIMIT 1
            ) observed ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT o.period_end_utc FROM backup_control_observations o
                WHERE o.profile_id=p.id AND o.result='SUCCESS'
                  AND atlas_backup_evidence_context_matches(p,'OBSERVATION',o.id,o.context_key)
                  AND (custody.tracking_since_utc IS NULL OR o.period_end_utc>=custody.tracking_since_utc)
                ORDER BY o.period_end_utc DESC,o.created_at_utc DESC LIMIT 1
            ) manual_accepted ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT b.id, b.finished_at_utc, b.status, b.payload, b.received_at_utc, b.acceptable
                FROM backup_runs b
                WHERE b.device_id = p.device_id
                  AND (custody.tracking_since_utc IS NULL OR b.finished_at_utc >= custody.tracking_since_utc)
                -- A different NAS job on the same device cannot replace this
                -- control's own latest attempt. Keep a foreign run only as a
                -- diagnostic fallback when no matching destination exists.
                ORDER BY (COALESCE(lower(b.payload->>'destinationHost')=lower(n.host)
                          AND lower(b.payload->>'destinationShare')=lower(n.share_name),false)
                          AND (b.payload->'evidence' IS NULL OR b.payload->'evidence'='null'::jsonb
                            OR COALESCE(cardinality(p.protected_folders),0)=0
                            OR NOT EXISTS (
                              SELECT 1 FROM unnest(p.protected_folders) expected(folder)
                              WHERE NOT EXISTS (
                                SELECT 1 FROM jsonb_array_elements(COALESCE(b.payload#>'{evidence,folders}','[]'::jsonb)) actual
                                WHERE atlas_backup_folder_key(expected.folder)=atlas_backup_folder_key(actual->>'name')
                              )
                            ))) DESC,
                         (COALESCE(lower(b.payload->>'destinationHost')=lower(n.host)
                          AND lower(b.payload->>'destinationShare')=lower(n.share_name),false)) DESC,
                         b.finished_at_utc DESC, b.id DESC
                LIMIT 1
            ) run ON p.provider = 'ATLAS_BACKUPNAS'
            LEFT JOIN LATERAL
            (
                SELECT b.id, b.finished_at_utc, b.status, b.payload, b.received_at_utc, b.acceptable
                FROM backup_runs b
                WHERE b.device_id=p.device_id
                  AND ((b.acceptable AND atlas_backup_scope_matches(p.protected_folders,b.payload))
                    OR ({{PendingExecutionSql}} AND cardinality(p.protected_folders)>0
                      AND NOT EXISTS (
                        SELECT 1 FROM unnest(p.protected_folders) expected(folder)
                        WHERE NOT EXISTS (
                          SELECT 1 FROM jsonb_array_elements(b.payload#>'{evidence,folders}') actual
                          WHERE atlas_backup_folder_key(expected.folder)=atlas_backup_folder_key(actual->>'name')
                            AND actual->>'state' IN ('COMPLETED','FAILED')
                        )
                      )))
                  AND lower(b.payload->>'destinationHost')=lower(n.host)
                  AND lower(b.payload->>'destinationShare')=lower(n.share_name)
                  AND (custody.tracking_since_utc IS NULL OR b.finished_at_utc>=custody.tracking_since_utc)
                ORDER BY b.finished_at_utc DESC,b.id DESC LIMIT 1
            ) accepted ON p.provider='ATLAS_BACKUPNAS'
            LEFT JOIN LATERAL
            (
                SELECT r.id, r.result, r.finished_at_utc,
                       r.next_test_due_utc
                FROM backup_restoration_tests r
                WHERE r.profile_id = p.id
                  AND atlas_backup_evidence_context_matches(p,'RESTORATION',r.id,r.context_key)
                  AND (custody.tracking_since_utc IS NULL OR r.finished_at_utc >= custody.tracking_since_utc)
                ORDER BY r.finished_at_utc DESC, r.created_at_utc DESC
                LIMIT 1
            ) restoration ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT COUNT(*)::INTEGER AS total
                FROM backup_control_evidence e
                WHERE e.profile_id = p.id AND e.status = 'ACTIVE'
            ) evidence ON TRUE
            ORDER BY p.enabled DESC, a.display_name, p.system_name, p.code;
            """;

        await using (NpgsqlCommand command = dataSource.CreateCommand(sql))
        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                // Derive lifecycle from the asset, including retirements made before this release.
                // Keep the original profile, assignment and evidence unchanged for historical review.
                bool deviceRetired = reader.GetBoolean(76);
                bool trackingEnabled = reader.GetBoolean(30) && !deviceRetired;
                string provider = reader.GetString(9);
                DateTimeOffset? manualAt = NullableDate(reader, 34);
                DateTimeOffset? runAt = NullableDate(reader, 38);
                bool useAgent = provider == BackupControlProviders.AtlasBackupNas &&
                    runAt.HasValue && (!manualAt.HasValue || runAt >= manualAt);
                Guid? observationId = useAgent
                    ? null
                    : NullableGuid(reader, 31);
                string? observationSource = useAgent
                    ? BackupObservationSources.Agent
                    : NullableString(reader, 32);
                string? observationResult = useAgent
                    ? NormalizeRunResult(NullableString(reader, 39))
                    : NullableString(reader, 33);
                BackupAutomaticReportView? latestExecution = reader.IsDBNull(70) ? null :
                    ReadAutomaticReport(reader.GetInt64(37), reader.GetString(70), NullableDate(reader,71), reader.GetBoolean(72), now, reader.GetString(39),runAt!.Value);
                if (useAgent && latestExecution is not null) observationResult = latestExecution.Result;
                bool scopeUnconfirmed = useAgent && latestExecution?.Evidence is not null && !reader.GetBoolean(75);
                if (useAgent && observationResult == BackupControlResults.Success &&
                    (latestExecution?.Acceptable != true || scopeUnconfirmed))
                    observationResult = BackupControlResults.Unconfirmed;
                DateTimeOffset? observationAt = useAgent ? runAt : manualAt;
                string? observationContextStatus = useAgent ? null : NullableString(reader,84);
                string? observationContextDetail = observationContextStatus switch
                {
                    "HISTORICAL_UNVERIFIED" => "Evidencia histórica sin sello de contexto. Conserva su fecha y resultado; revisa en lote su correspondencia con el activo, alcance y destino actuales antes de acreditarla.",
                    "CONTEXT_CHANGED" => "La evidencia pertenece a otra configuración de protección. Se conserva en el historial; el activo, alcance o destino actual requieren evidencia compatible.",
                    _ => null
                };
                string? observationDetail = useAgent
                    ? "Resultado recibido automáticamente desde Atlas Backup."
                    : NullableString(reader, 35);
                string? externalReference = useAgent
                    ? reader.IsDBNull(37) ? null : $"backup-run:{reader.GetInt64(37)}"
                    : NullableString(reader, 36);

                string? expectedHost = NullableString(reader, 15);
                string? expectedShare = NullableString(reader, 16);
                string? reportedHost = useAgent ? NullableString(reader, 40) : null;
                string? reportedShare = useAgent ? NullableString(reader, 41) : null;
                bool destinationMismatch = useAgent &&
                    (string.IsNullOrWhiteSpace(reportedHost) || string.IsNullOrWhiteSpace(reportedShare) ||
                     !string.Equals(reportedHost, expectedHost,
                        StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(reportedShare, expectedShare,
                        StringComparison.OrdinalIgnoreCase));

                BackupExecutionAssessment? latestAssessment = AssessLatestExecution(
                    latestExecution, expectedHost, expectedShare,
                    NullableString(reader, 40), NullableString(reader, 41),
                    reader.GetFieldValue<string[]>(65), reader.GetBoolean(75),
                    reader.GetFieldValue<string[]>(78));

                DateTimeOffset? acceptableAt = NullableDate(reader,73);
                string? acceptableResult = NormalizeRunResult(NullableString(reader,74));
                BackupExecutionAssessment? acceptedAssessment = reader.IsDBNull(81) ? null :
                    AssessLatestExecution(ReadAutomaticReport(reader.GetInt64(80), reader.GetString(81), NullableDate(reader,82), reader.GetBoolean(83), now,
                        reader.GetString(74), acceptableAt!.Value), expectedHost, expectedShare,
                        expectedHost, expectedShare, reader.GetFieldValue<string[]>(65), true, []);
                // Admission is checked again with the shared contract. A future
                // SQL/contract disagreement must never produce a false green.
                if (acceptedAssessment?.AcceptableForControl != true)
                { acceptableAt = null; acceptableResult = null; acceptedAssessment = null; }
                // Preserve the last acceptable copy independently from later attempts.
                // Manual evidence remains explicitly attributed to its operator.
                DateTimeOffset? manualAcceptedAt = NullableDate(reader,79);
                if (manualAcceptedAt.HasValue && (!acceptableAt.HasValue || manualAcceptedAt > acceptableAt))
                {
                    acceptableAt = manualAcceptedAt;
                    acceptableResult = BackupControlResults.Success;
                    acceptedAssessment = new("ACCEPTABLE", "Comprobación satisfactoria documentada; se conserva su fuente y fecha originales.", true, []);
                }

                BackupControlEvaluationResult evaluation =
                    BackupControlEvaluator.Evaluate(
                        trackingEnabled,
                        reader.GetString(26),
                        provider,
                        NullableGuid(reader, 3),
                        NullableGuid(reader, 13),
                        reader.GetInt32(18),
                        reader.GetInt32(19),
                        acceptableAt ?? observationAt,
                        acceptableAt.HasValue ? BackupControlResults.Success : observationContextDetail is not null ? BackupControlResults.Unconfirmed : observationResult,
                        destinationMismatch,
                        reader.IsDBNull(63) ? NullableInt(reader, 23) : null,
                        reader.IsDBNull(63) ? NullableDate(reader, 44) : null,
                        reader.IsDBNull(63) ? NullableDate(reader, 45) : null,
                        reader.GetString(60),
                        NullableDate(reader, 62),
                        now, reader.IsDBNull(63) ? NullableString(reader, 43) : null);

                if (scopeUnconfirmed && !acceptableAt.HasValue)
                    evaluation = new(BackupControlCompliance.NotConfirmed,
                        "La ejecución no acredita todas las carpetas del plan. Compara el alcance configurado con las carpetas del informe automático.", evaluation.RestoreTestDue);
                if (observationContextDetail is not null && !acceptableAt.HasValue && trackingEnabled && BackupApprovalStatuses.IsActive(reader.GetString(26)))
                    evaluation = new(BackupControlCompliance.NotConfirmed,observationContextDetail,evaluation.RestoreTestDue);

                if (reader.GetBoolean(67) && !deviceRetired)
                    evaluation = new(NullableDate(reader,69) <= now ? BackupControlCompliance.PauseReviewDue : BackupControlCompliance.Paused,
                        $"Seguimiento pausado por devolución: {NullableString(reader,68)}. Revisar antes de {NullableDate(reader,69):u}. Las tareas de copia mantienen su configuración.",false);

                if (deviceRetired)
                    evaluation = new(BackupControlCompliance.Inactive,
                        "Equipo desvinculado. Seguimiento finalizado; configuración e historial conservados.", false);

                profiles.Add(new BackupControlProfileView(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    NullableGuid(reader, 3),
                    NullableString(reader, 4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    provider,
                    reader.GetString(10),
                    reader.GetString(11),
                    NullableString(reader, 12),
                    NullableGuid(reader, 13),
                    NullableString(reader, 14),
                    expectedHost,
                    expectedShare,
                    NullableString(reader, 17),
                    reader.GetInt32(18),
                    reader.GetInt32(19),
                    NullableInt(reader, 20),
                    NullableInt(reader, 21),
                    NullableInt(reader, 22),
                    NullableInt(reader, 23),
                    reader.GetString(24),
                    NullableString(reader, 25),
                    reader.GetString(26),
                    NullableString(reader, 27),
                    NullableDate(reader, 28),
                    NullableString(reader, 29),
                    trackingEnabled,
                    observationId,
                    observationSource,
                    observationResult,
                    observationAt,
                    observationDetail,
                    externalReference,
                    NullableGuid(reader, 42),
                    NullableString(reader, 43),
                    NullableDate(reader, 44),
                    NullableDate(reader, 45),
                    evaluation.RestoreTestDue,
                    reader.GetInt32(46),
                    evaluation.Compliance,
                    evaluation.Detail,
                    reader.GetFieldValue<DateTimeOffset>(47),
                    reader.GetFieldValue<DateTimeOffset>(48),
                    reader.GetString(49),
                    reader.GetGuid(50), reader.GetString(51),
                    reader.GetString(52), reader.GetString(53),
                    reader.GetString(54), NullableString(reader, 55),
                    reader.GetFieldValue<string[]>(56),
                    NullableString(reader, 57), NullableString(reader, 58),
                    DeserializeOrigins(reader.GetString(59)),
                    reader.GetString(60), NullableString(reader, 61),
                    NullableDate(reader, 62), NullableGuid(reader, 63),
                    NullableInt(reader, 64), reader.GetFieldValue<string[]>(65), reader.GetBoolean(66),
                    reader.GetBoolean(67) && !deviceRetired,NullableString(reader,68),NullableDate(reader,69),
                    acceptableAt, acceptableResult, latestExecution,deviceRetired,NullableDate(reader,77),latestAssessment,acceptedAssessment,
                    observationContextStatus,observationContextDetail));
            }
        }

        var schedules=await new BackupScheduleService(dataSource,timeProvider).EvaluateAsync(profiles,now,cancellationToken);
        for(int i=0;i<profiles.Count;i++)
        {
            var profile=profiles[i];if(!schedules.TryGetValue(profile.Id,out var schedule))continue;
            profile=profile with{ScheduleStatus=schedule};
            bool eligible=profile.Enabled&&!profile.TrackingPaused&&!profile.DeviceRetired&&BackupApprovalStatuses.IsActive(profile.ApprovalStatus);
            bool timeOnly=profile.Compliance is BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue or BackupControlCompliance.Overdue ||
                profile.Compliance==BackupControlCompliance.NotConfirmed&&profile.LastObservationAtUtc is null&&profile.LatestExecution is null;
            if(eligible&&schedule.Mode=="CALENDAR"&&timeOnly)
                profile=profile with{Compliance=schedule.WaitingForFirstCopy?BackupControlCompliance.Scheduled:schedule.Overdue?BackupControlCompliance.Overdue:
                    profile.RestoreTestDue?BackupControlCompliance.RestoreTestDue:BackupControlCompliance.Compliant,ComplianceDetail=schedule.Detail};
            profiles[i]=profile;
        }
        IReadOnlyList<BackupControlStandardView> standards =
            await ReadStandardsAsync(cancellationToken);
        IReadOnlyList<BackupControlDeviceOption> devices =
            await ReadDeviceOptionsAsync(cancellationToken);
        IReadOnlyList<BackupControlNasOption> nas =
            await ReadNasOptionsAsync(cancellationToken);
        int activeTotal = profiles.Count(item => item.Enabled && !item.TrackingPaused &&
            BackupApprovalStatuses.IsActive(item.ApprovalStatus));
        int compliant = profiles.Count(item =>
            item.Enabled && item.Compliance is BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue);
        int failed = profiles.Count(item => item.Enabled &&
            item.Compliance is BackupControlCompliance.Failed or
                BackupControlCompliance.DestinationMismatch);
        int overdue = profiles.Count(item =>
            item.Enabled && item.Compliance == BackupControlCompliance.Overdue);
        int restoreDue = profiles.Count(item => item.Enabled && item.RestoreTestDue);
        int pending = profiles.Count(item =>
            !item.DeviceRetired && !BackupApprovalStatuses.IsActive(item.ApprovalStatus));
        int attention = profiles.Count(item => item.Enabled && !item.TrackingPaused &&
            (item.LatestExecution is { Acceptable: false, Result: not "RUNNING" } && item.LatestExecutionAssessment?.AcceptableForControl != true || item.Compliance is BackupControlCompliance.Cancelled or BackupControlCompliance.Warning or
                BackupControlCompliance.NotConfirmed or
                BackupControlCompliance.ConfigurationRequired or
                BackupControlCompliance.ExceptionExpired or
                BackupControlCompliance.RestoreTestDue || item.ScheduleStatus?.ExceptionExpired==true));
        int approvedActive = profiles.Count(item => item.Enabled && !item.TrackingPaused &&
            BackupApprovalStatuses.IsActive(item.ApprovalStatus));
        int coverage = approvedActive == 0
            ? 0
            : (int)Math.Round(compliant * 100d / approvedActive,
                MidpointRounding.AwayFromZero);

        var demonstrations = await BackupPlanDemonstrationService.ReadSummariesAsync(dataSource,now,cancellationToken);
        return new BackupControlDashboardView(
            now,
            new BackupControlSummary(
                activeTotal, compliant, attention, failed, overdue,
                restoreDue + demonstrations.Count(d=>d.Pending), pending, coverage,profiles.Count(p=>p.Enabled&&p.Compliance==BackupControlCompliance.Scheduled)),
            profiles, standards,
            devices,
            nas, demonstrations);
    }

    public async Task<BackupControlProfileDetail?> ReadDetailAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        BackupControlDashboardView dashboard =
            await ReadDashboardAsync(cancellationToken);
        BackupControlProfileView? profile = dashboard.Profiles
            .FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return null;
        }

        return new BackupControlProfileDetail(
            profile,
            await ReadObservationsAsync(profileId, cancellationToken),
            await ReadRestorationsAsync(profileId, cancellationToken),
            await ReadEvidenceAsync(profileId, cancellationToken),
            await ReadAutomaticReportsAsync(profileId, cancellationToken));
    }

    private async Task<IReadOnlyList<BackupAutomaticReportView>> ReadAutomaticReportsAsync(Guid profileId, CancellationToken token)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT b.id, b.payload::text, b.received_at_utc, b.acceptable, b.status, b.finished_at_utc,
                   n.host,n.share_name,p.protected_folders,atlas_backup_scope_matches(p.protected_folders,b.payload),
                   ARRAY(SELECT expected.folder FROM unnest(p.protected_folders) WITH ORDINALITY expected(folder,position)
                     WHERE NOT EXISTS (
                       SELECT 1 FROM jsonb_array_elements(COALESCE(b.payload#>'{evidence,folders}','[]'::jsonb)) actual
                       WHERE atlas_backup_folder_key(expected.folder)=atlas_backup_folder_key(actual->>'name')
                         AND actual->>'state' IN ('COMPLETED','FAILED')
                     ) ORDER BY expected.position)
            FROM backup_runs b JOIN backup_control_profiles p ON p.device_id=b.device_id
            LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id
            WHERE p.id=$1 AND p.provider='ATLAS_BACKUPNAS'
            ORDER BY b.finished_at_utc DESC,b.id DESC LIMIT 20;
            """);
        command.Parameters.AddWithValue(profileId);
        List<BackupAutomaticReportView> reports = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var report = ReadAutomaticReport(reader.GetInt64(0),reader.GetString(1),NullableDate(reader,2),reader.GetBoolean(3),timeProvider.GetUtcNow(),reader.GetString(4),reader.GetFieldValue<DateTimeOffset>(5));
            var original = JsonSerializer.Deserialize<BackupRunReport>(reader.GetString(1),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            reports.Add(report with { ControlAssessment = AssessLatestExecution(report, NullableString(reader,6), NullableString(reader,7),
                original.DestinationHost, original.DestinationShare,reader.GetFieldValue<string[]>(8),reader.GetBoolean(9),reader.GetFieldValue<string[]>(10)) });
        }
        return reports;
    }

    private static BackupAutomaticReportView ReadAutomaticReport(long id,string payload,DateTimeOffset? received,bool acceptable,DateTimeOffset now,string storedStatus,DateTimeOffset observedAt)
    {
        var report = JsonSerializer.Deserialize<BackupRunReport>(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        report = report with { Status = Enum.TryParse<BackupRunStatus>(storedStatus,out var status) ? status : BackupRunStatus.Unconfirmed, FinishedAtUtc = observedAt };
        string result = BackupTelemetry.Result(report, now);
        return new($"backup-run:{id}",report.FinishedAtUtc,NormalizeRunResult(result)!,report.FilesCopied,report.FilesSkipped,
            report.DestinationBasePath is { Length: > 0 } basePath ? basePath : report.DestinationHost is { Length: > 0 } host && report.DestinationShare is { Length: > 0 } share ? $"\\\\{host}\\{share}" : null,
            report.MissingFolders ?? [],report.FailedFolders ?? [],report.StartedAtUtc == default ? null : report.StartedAtUtc,received,acceptable,report.Evidence,report.BytesCopied);
    }

    // Applies the operational decision to immutable execution evidence. Raw
    // results/counts remain intact; acceptance never renews the execution date.
    private static BackupExecutionAssessment? AssessLatestExecution(
        BackupAutomaticReportView? run, string? expectedHost, string? expectedShare,
        string? reportedHost, string? reportedShare, IReadOnlyList<string> expectedFolders,
        bool scopeMatches, IReadOnlyList<string> missingExpectedFolders)
    {
        if (run is null) return null;
        var folders = run.Evidence?.Folders ?? [];
        bool countsComplete = folders.Count > 0 && folders.All(f => f.FilesFailed.HasValue);
        long knownFailedFiles = folders.Sum(f => f.FilesFailed ?? 0);
        long? pendingFiles = countsComplete || knownFailedFiles > 0 ? knownFailedFiles : null;
        var pendingReasons = folders.Where(f => f.State == "FAILED" || f.FilesFailed > 0)
            .Select(f => $"{f.Name}: {(f.FilesFailed.HasValue ? $"{f.FilesFailed} archivo(s) pendientes" : "cantidad pendiente no informada")}; " +
                (f.ExitCode.HasValue ? $"código de Robocopy {f.ExitCode}." : "código de salida no informado.") +
                " El detalle por archivo y el motivo específico dependen del registro de ejecución.")
            .Concat(run.Evidence?.Warnings ?? []).Distinct().ToArray();
        BackupExecutionAssessment Decision(string code, string detail, bool accepted = false) =>
            new(code, detail, accepted, missingExpectedFolders, pendingFiles, countsComplete, pendingReasons);
        BackupExecutionAssessment Rejected(string code, string detail) => Decision(code, detail);
        if (run.Result == BackupControlResults.Running)
            return Rejected("RUNNING", "La copia está en curso; todavía no hay un resultado final.");
        if (run.Result == BackupControlResults.Cancelled)
            return Rejected("CANCELLED", "La ejecución fue cancelada. Se conserva la última copia aceptable registrada.");
        if (run.Result == BackupControlResults.Skipped)
            return Rejected("NOT_STARTED", "La aplicación se cerró sin iniciar la copia. No significa que se hayan omitido archivos de una copia realizada.");

        var failedFolders = run.FailedFolders.Concat(folders.Where(f => f.State == "FAILED").Select(f => f.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missingFolders = run.MissingFolders.Concat(folders.Where(f => f.State == "MISSING").Select(f => f.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingFolders.Length > 0)
            return Rejected("MISSING_FOLDERS", $"No se encontraron estas carpetas de origen: {string.Join(", ", missingFolders)}.");
        bool acceptablePending = run.Result is BackupControlResults.Partial or BackupControlResults.Failed &&
            BackupTelemetry.IsAcceptableWithPendingFiles(run.Evidence) &&
            run.FailedFolders.All(name => folders.Any(f => string.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase) && f.State=="FAILED" && f.FilesFailed>0));
        if (!acceptablePending && (knownFailedFiles > 0 || failedFolders.Length > 0))
            return Rejected(run.Result == BackupControlResults.Failed ? "FAILED" : "FILES_PENDING", knownFailedFiles > 0
                ? !countsComplete
                    ? $"Se confirmaron {knownFailedFiles} archivo(s) que no pudieron copiarse; faltan recuentos de otras carpetas. El informe conserva los datos disponibles."
                    : $"{knownFailedFiles} archivo(s) no pudieron copiarse. La evidencia no confirma una ejecución aceptable con contenido protegido copiado o sin cambios; se conserva la última copia aceptable."
                : $"No se completó la copia de estas carpetas: {string.Join(", ", failedFolders)}.");
        if (run.Result == BackupControlResults.Failed && !acceptablePending)
            return Rejected("FAILED", "La ejecución terminó con un error. El informe conserva el motivo recibido y los archivos que sí se copiaron.");
        if (run.Result is BackupControlResults.Unconfirmed or BackupControlResults.Unknown ||
            folders.Any(f => f.State is "UNCONFIRMED" or "PENDING" or "RUNNING"))
            return Rejected("RESULT_UNCONFIRMED", "El informe no permite confirmar el resultado final de todas las carpetas. No se supone una cancelación ni una copia realizada.");

        if (string.IsNullOrWhiteSpace(reportedHost) || string.IsNullOrWhiteSpace(reportedShare) ||
            !string.Equals(reportedHost, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reportedShare, expectedShare, StringComparison.OrdinalIgnoreCase))
            return Rejected("DESTINATION_MISMATCH", "La ejecución reportó un destino distinto del autorizado o no informó el destino. No acredita la copia de este control.");
        if (run.Evidence is not null && expectedFolders.Count == 0)
            return Rejected("SCOPE_CONFIGURATION_REQUIRED", "El control necesita una lista explícita de carpetas protegidas para comparar este informe. La ejecución conserva su resultado; no se acredita un alcance que aún no está configurado.");
        if (!scopeMatches && !(acceptablePending && missingExpectedFolders.Count == 0))
            return Rejected("SCOPE_MISMATCH", $"La ejecución terminó, pero no acredita estas carpetas esperadas por el control: {string.Join(", ", missingExpectedFolders)}. Compara el alcance del control con las carpetas del informe.");
        if (acceptablePending)
            return Decision("ACCEPTABLE_WITH_PENDING_FILES",
                $"Copia aceptable con {knownFailedFiles} archivo(s) pendientes. La ejecución terminó y reporta archivos copiados o sin cambios/excluidos según configuración para el alcance y destino configurados. Los pendientes conservan su cantidad, carpeta y motivo disponible; no se supone que ya se copiaron ni se garantiza su recuperación posterior.", true);
        if (!run.Acceptable)
            return Rejected("RESULT_UNCONFIRMED", run.Evidence is null
                ? "El reporte histórico no contiene el desglose necesario para acreditar esta ejecución. Se conserva su resultado original."
                : "El resultado recibido no acredita una ejecución aceptable. Se conserva el informe y la última copia aceptable registrada.");
        return Decision("ACCEPTABLE", run.Evidence is null
            ? "Ejecución histórica aceptable según el criterio original, sin desglose por carpeta. La vigencia depende de su fecha y de la frecuencia del control."
            : "La ejecución es aceptable para el alcance y el destino configurados. Los archivos sin cambios o excluidos por configuración no la invalidan. La vigencia depende de su fecha y de la frecuencia del control.",
            true);
    }

    public async Task<bool> ReferencesBelongToAgencyAsync(
        BackupControlProfileRequest request,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT EXISTS(SELECT 1 FROM agencies WHERE code = $1)
               AND ($2::uuid IS NULL OR EXISTS
                   (SELECT 1 FROM devices WHERE id = $2 AND agency_code = $1 AND enabled))
               AND ($3::uuid IS NULL OR EXISTS
                   (SELECT 1 FROM nas_appliances WHERE id = $3 AND agency_code = $1 AND (enabled OR NOT $5)))
               AND EXISTS
                   (SELECT 1 FROM backup_control_standards
                    WHERE id = $4 AND active = TRUE);
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(request.AgencyCode);
        AddNullable(command, NpgsqlDbType.Uuid, request.DeviceId);
        AddNullable(command, NpgsqlDbType.Uuid, request.NasApplianceId);
        command.Parameters.AddWithValue(request.StandardId!.Value);
        command.Parameters.AddWithValue(request.Enabled);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<Guid?> CreateProfileAsync(
        BackupControlProfileRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO backup_control_profiles
                (id, code, scope_type, device_id, agency_code, system_name,
                 protected_scope, provider, operation_mode, technology,
                 task_name, nas_appliance_id, expected_destination,
                 frequency_hours, grace_minutes, retention_days, rpo_hours,
                 rto_minutes, restore_test_interval_days,
                 technical_responsible, process_owner, approval_status,
                 enabled, created_at_utc, created_by, updated_at_utc, updated_by,
                 standard_id, configuration_mode, configuration_reason,
                 protected_scope_categories, protected_scope_additional,
                 protected_scope_exclusions, field_origins, exception_type,
                 exception_reason, exception_expires_at_utc, protected_folders)
            SELECT
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                 $13, $14, $15, $16, $17, $18, $19, $20, $21, $35,
                 $22, $23, $24, $23, $24, $25, $26, $27, $28, $29,
                 $30, $31, $32, $33, $34, COALESCE($36::text[],'{}'::text[])
            WHERE $4::uuid IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=$4 AND d.enabled)
            RETURNING id;
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        AddProfileParameters(command, id, request, now, actor);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid created ? created : null;
    }

    public async Task<bool> UpdateProfileAsync(
        Guid id,
        BackupControlProfileRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            UPDATE backup_control_profiles
            SET code = $2, scope_type = $3, device_id = $4,
                agency_code = $5, system_name = $6, protected_scope = $7,
                provider = $8, operation_mode = $9, technology = $10,
                task_name = $11, nas_appliance_id = $12,
                expected_destination = $13, frequency_hours = $14,
                grace_minutes = $15, retention_days = $16, rpo_hours = $17,
                rto_minutes = $18, restore_test_interval_days = $19,
                technical_responsible = $20, process_owner = $21,
                approval_status = $35, approved_by = NULL,
                approved_at_utc = NULL, approval_comment = NULL,
                enabled = $22, updated_at_utc = $23, updated_by = $24,
                standard_id = $25, configuration_mode = $26,
                configuration_reason = $27,
                protected_scope_categories = $28,
                protected_scope_additional = $29,
                protected_scope_exclusions = $30, field_origins = $31,
                exception_type = $32, exception_reason = $33,
                exception_expires_at_utc = $34,
                plan_id = CASE WHEN device_id IS NOT DISTINCT FROM $4 AND provider=$8 AND protected_scope=$7
                    AND nas_appliance_id IS NOT DISTINCT FROM $12 AND expected_destination IS NOT DISTINCT FROM $13
                    AND task_name IS NOT DISTINCT FROM $11 AND protected_scope_exclusions IS NOT DISTINCT FROM $30
                    AND frequency_hours=$14 AND grace_minutes=$15 AND rpo_hours IS NOT DISTINCT FROM $17
                    AND rto_minutes IS NOT DISTINCT FROM $18 AND restore_test_interval_days IS NOT DISTINCT FROM $19
                    AND ($36::text[] IS NULL OR protected_folders=$36) THEN plan_id ELSE NULL END,
                plan_revision = CASE WHEN device_id IS NOT DISTINCT FROM $4 AND provider=$8 AND protected_scope=$7
                    AND nas_appliance_id IS NOT DISTINCT FROM $12 AND expected_destination IS NOT DISTINCT FROM $13
                    AND task_name IS NOT DISTINCT FROM $11 AND protected_scope_exclusions IS NOT DISTINCT FROM $30
                    AND frequency_hours=$14 AND grace_minutes=$15 AND rpo_hours IS NOT DISTINCT FROM $17
                    AND rto_minutes IS NOT DISTINCT FROM $18 AND restore_test_interval_days IS NOT DISTINCT FROM $19
                    AND ($36::text[] IS NULL OR protected_folders=$36) THEN plan_revision ELSE NULL END,
                protected_folders = COALESCE($36::text[],CASE WHEN protected_scope=$7 THEN protected_folders ELSE '{}'::text[] END)
            WHERE id = $1
              AND (device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=backup_control_profiles.device_id AND d.enabled))
              AND ($4::uuid IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=$4 AND d.enabled));
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        AddProfileParameters(command, id, request, now, actor);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task<BackupProfileDecisionResult> ActivateAsync(
        Guid id,
        DateTimeOffset expectedUpdatedAtUtc,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ActivateProfileAsync(id, expectedUpdatedAtUtc, actor, now, cancellationToken);

    public async Task<Guid?> AddObservationAsync(
        Guid profileId,
        BackupControlObservationRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO backup_control_observations
                (id, profile_id, source, result, period_start_utc,
                 period_end_utc, started_at_utc, finished_at_utc,
                 external_reference, detail, created_at_utc, created_by,context_snapshot,context_key)
            SELECT $1, p.id, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                   atlas_backup_context_snapshot(p),atlas_backup_context_key(p)
            FROM backup_control_profiles p
            WHERE p.id = $2
              AND (p.device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled))
            RETURNING id;
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(profileId);
        command.Parameters.AddWithValue(request.Source);
        command.Parameters.AddWithValue(request.Result);
        command.Parameters.AddWithValue(request.PeriodStartUtc);
        command.Parameters.AddWithValue(request.PeriodEndUtc);
        AddNullable(command, NpgsqlDbType.TimestampTz, request.StartedAtUtc);
        AddNullable(command, NpgsqlDbType.TimestampTz, request.FinishedAtUtc);
        AddNullable(command, NpgsqlDbType.Varchar, request.ExternalReference);
        AddNullable(command, NpgsqlDbType.Varchar, request.Detail);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid created ? created : null;
    }

    public async Task<bool> CanAcceptObservationAsync(
        Guid profileId,
        string source,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM backup_control_profiles p
                WHERE p.id = $1
                  AND (p.device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled))
                  AND ($2 <> 'ACRONIS_EVIDENCE' OR p.provider = 'ACRONIS')
            );
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(profileId);
        command.Parameters.AddWithValue(source);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<Guid?> AddRestorationAsync(
        Guid profileId,
        BackupRestorationRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO backup_restoration_tests
                (id, profile_id, operational_case_id, modality, result,
                 backup_used_at_utc, started_at_utc, finished_at_utc,
                 tested_scope, integrity_verified, functionality_verified,
                 rto_achieved_minutes, executed_by, validated_by,
                 validated_at_utc, notes, historical, next_test_due_utc,
                 created_at_utc, created_by,context_snapshot,context_key)
            SELECT $1, p.id, $3, $4, $5, $6, $7, $8, $9, $10, $11,
                   $12, $13, $14, $15, $16, $17, $18, $19, $20,
                   atlas_backup_context_snapshot(p),atlas_backup_context_key(p)
            FROM backup_control_profiles p
            WHERE p.id = $2
              AND (p.device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled))
            RETURNING id;
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(profileId);
        AddNullable(command, NpgsqlDbType.Uuid, request.OperationalCaseId);
        command.Parameters.AddWithValue(request.Modality);
        command.Parameters.AddWithValue(request.Result);
        AddNullable(command, NpgsqlDbType.TimestampTz, request.BackupUsedAtUtc);
        command.Parameters.AddWithValue(request.StartedAtUtc);
        command.Parameters.AddWithValue(request.FinishedAtUtc);
        command.Parameters.AddWithValue(request.TestedScope);
        command.Parameters.AddWithValue(request.IntegrityVerified);
        command.Parameters.AddWithValue(request.FunctionalityVerified);
        AddNullable(command, NpgsqlDbType.Integer, request.RtoAchievedMinutes);
        command.Parameters.AddWithValue(request.ExecutedBy);
        AddNullable(command, NpgsqlDbType.Varchar, request.ValidatedBy);
        AddNullable(command, NpgsqlDbType.TimestampTz, request.ValidatedAtUtc);
        AddNullable(command, NpgsqlDbType.Varchar, request.Notes);
        command.Parameters.AddWithValue(request.Historical);
        AddNullable(command, NpgsqlDbType.TimestampTz, request.NextTestDueUtc);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid created ? created : null;
    }

    public async Task<bool> AddEvidenceAsync(
        Guid id,
        Guid profileId,
        BackupEvidenceUploadMetadata metadata,
        string mediaType,
        string storedRelativePath,
        long sizeBytes,
        string sha256,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            INSERT INTO backup_control_evidence
                (id, profile_id, observation_id, restoration_test_id,
                 operational_case_id, evidence_type, original_file_name,
                 media_type, stored_relative_path, size_bytes, sha256,
                 evidence_at_utc, description, source, status,
                 created_at_utc, created_by)
            SELECT $1, p.id, $3, $4, $5, $6, $7, $8, $9, $10, $11,
                   $12, $13, $14, 'ACTIVE', $15, $16
            FROM backup_control_profiles p
            WHERE p.id = $2
              AND (p.device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled))
              AND ($3::uuid IS NULL OR EXISTS
                  (SELECT 1 FROM backup_control_observations o
                   WHERE o.id = $3 AND o.profile_id = p.id))
              AND ($4::uuid IS NULL OR EXISTS
                  (SELECT 1 FROM backup_restoration_tests r
                   WHERE r.id = $4 AND r.profile_id = p.id))
              AND ($5::uuid IS NULL OR EXISTS
                  (SELECT 1 FROM operational_cases c WHERE c.id = $5));
            """;
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(profileId);
        AddNullable(command, NpgsqlDbType.Uuid, metadata.ObservationId);
        AddNullable(command, NpgsqlDbType.Uuid, metadata.RestorationTestId);
        AddNullable(command, NpgsqlDbType.Uuid, metadata.OperationalCaseId);
        command.Parameters.AddWithValue(metadata.EvidenceType);
        command.Parameters.AddWithValue(metadata.FileName);
        command.Parameters.AddWithValue(mediaType);
        command.Parameters.AddWithValue(storedRelativePath);
        command.Parameters.AddWithValue(sizeBytes);
        command.Parameters.AddWithValue(sha256);
        command.Parameters.AddWithValue(metadata.EvidenceAtUtc);
        AddNullable(command, NpgsqlDbType.Varchar, metadata.Description);
        AddNullable(command, NpgsqlDbType.Varchar, metadata.Source);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<BackupEvidenceFileRecord?> ReadEvidenceFileAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT stored_relative_path, original_file_name, media_type
            FROM backup_control_evidence
            WHERE id = $1 AND status = 'ACTIVE';
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(evidenceId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task<BackupProfileDecisionResult> ActivateProfileAsync(
        Guid id,
        DateTimeOffset expectedUpdatedAtUtc,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE backup_control_profiles
            SET approval_status = 'ACTIVE', enabled = TRUE, approved_by = NULL,
                approved_at_utc = NULL, approval_comment = NULL,
                updated_at_utc = $3, updated_by = $4
            WHERE id = $1 AND updated_at_utc = $2
              AND (device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=backup_control_profiles.device_id AND d.enabled))
              AND approval_status IN ('DRAFT', 'PENDING_APPROVAL', 'RETURNED')
              AND exception_type <> 'NORMATIVE'
              AND (provider <> 'ATLAS_BACKUPNAS' OR
                   (device_id IS NOT NULL AND nas_appliance_id IS NOT NULL))
            RETURNING id;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(expectedUpdatedAtUtc);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid)
        {
            return BackupProfileDecisionResult.Updated;
        }

        await using NpgsqlCommand exists = dataSource.CreateCommand(
            "SELECT exception_type FROM backup_control_profiles WHERE id = $1;");
        exists.Parameters.AddWithValue(id);
        object? exceptionType = await exists.ExecuteScalarAsync(cancellationToken);
        return exceptionType is null ? BackupProfileDecisionResult.NotFound :
            exceptionType.ToString() == BackupControlExceptionTypes.Normative
                ? BackupProfileDecisionResult.NormativeException
                : BackupProfileDecisionResult.InvalidTransition;
    }

    private async Task<IReadOnlyList<BackupControlObservationView>>
        ReadObservationsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source, result, period_start_utc, period_end_utc,
                   started_at_utc, finished_at_utc, external_reference,
                   detail, created_at_utc, created_by
            FROM backup_control_observations
            WHERE profile_id = $1
            ORDER BY period_end_utc DESC, created_at_utc DESC
            LIMIT 50;
            """;
        List<BackupControlObservationView> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(profileId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BackupControlObservationView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                NullableDate(reader, 5), NullableDate(reader, 6),
                NullableString(reader, 7), NullableString(reader, 8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10)));
        }
        return items;
    }

    private async Task<IReadOnlyList<BackupRestorationView>>
        ReadRestorationsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, operational_case_id, modality, result,
                   backup_used_at_utc, started_at_utc, finished_at_utc,
                   tested_scope, integrity_verified, functionality_verified,
                   rto_achieved_minutes, executed_by, validated_by,
                   validated_at_utc, notes, historical, next_test_due_utc,
                   created_at_utc, created_by
            FROM backup_restoration_tests
            WHERE profile_id = $1
            ORDER BY finished_at_utc DESC, created_at_utc DESC
            LIMIT 50;
            """;
        List<BackupRestorationView> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(profileId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BackupRestorationView(
                reader.GetGuid(0), NullableGuid(reader, 1), reader.GetString(2),
                reader.GetString(3), NullableDate(reader, 4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7),
                reader.GetBoolean(8), reader.GetBoolean(9), NullableInt(reader, 10),
                reader.GetString(11), NullableString(reader, 12),
                NullableDate(reader, 13), NullableString(reader, 14),
                reader.GetBoolean(15), NullableDate(reader, 16),
                reader.GetFieldValue<DateTimeOffset>(17), reader.GetString(18)));
        }
        return items;
    }

    private async Task<IReadOnlyList<BackupEvidenceView>> ReadEvidenceAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, observation_id, restoration_test_id,
                   operational_case_id, evidence_type, original_file_name,
                   media_type, size_bytes, sha256, evidence_at_utc,
                   description, source, status, created_at_utc, created_by
            FROM backup_control_evidence
            WHERE profile_id = $1
            ORDER BY evidence_at_utc DESC, created_at_utc DESC
            LIMIT 100;
            """;
        List<BackupEvidenceView> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(profileId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BackupEvidenceView(
                reader.GetGuid(0), NullableGuid(reader, 1), NullableGuid(reader, 2),
                NullableGuid(reader, 3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt64(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9), NullableString(reader, 10),
                NullableString(reader, 11), reader.GetString(12),
                reader.GetFieldValue<DateTimeOffset>(13), reader.GetString(14)));
        }
        return items;
    }

    private async Task<IReadOnlyList<BackupControlDeviceOption>>
        ReadDeviceOptionsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.machine_name, d.agency_code, d.backup_provider,
                   d.nas_appliance_id, n.display_name, n.host, n.share_name,
                   run.payload ->> 'destinationHost',
                   run.payload ->> 'destinationShare',
                   d.interactive_user_account, d.interactive_user_display_name,
                   d.interactive_user_checked_at_utc
            FROM devices d
            LEFT JOIN nas_appliances n ON n.id = d.nas_appliance_id
            LEFT JOIN LATERAL
            (
                SELECT b.payload
                FROM backup_runs b
                WHERE b.device_id = d.id
                ORDER BY b.finished_at_utc DESC, b.id DESC
                LIMIT 1
            ) run ON TRUE
            WHERE d.enabled = TRUE
            ORDER BY d.agency_code, d.machine_name;
            """;
        List<BackupControlDeviceOption> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                NormalizeDeviceProvider(reader.GetString(3)),
                NullableGuid(reader, 4), NullableString(reader, 5),
                NullableString(reader, 6), NullableString(reader, 7),
                NullableString(reader, 8), NullableString(reader, 9),
                NullableString(reader, 10), NullableString(reader, 11),
                NullableDate(reader, 12), reader.GetString(3)));
        }
        return items;
    }

    private async Task<IReadOnlyList<BackupControlStandardView>> ReadStandardsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, version, name, description, scope_type, provider,
                   operation_mode, technology, task_name, protected_scope,
                   protected_scope_categories, frequency_hours, grace_minutes,
                   retention_days, rpo_hours, rto_minutes,
                   restore_test_interval_days, technical_responsible, is_system
            FROM backup_control_standards
            WHERE active = TRUE
            ORDER BY CASE code WHEN 'BKP-BASE-INSTITUCIONAL' THEN 0 ELSE 1 END,
                     name, version;
            """;
        List<BackupControlStandardView> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BackupControlStandardView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), NullableString(reader, 5),
                NullableString(reader, 6), NullableString(reader, 7),
                NullableString(reader, 8), NullableString(reader, 9),
                NullableString(reader, 10), reader.GetFieldValue<string[]>(11),
                NullableInt(reader, 12), NullableInt(reader, 13),
                NullableInt(reader, 14), NullableInt(reader, 15),
                NullableInt(reader, 16), NullableInt(reader, 17),
                reader.GetString(18), reader.GetBoolean(19)));
        }
        return items;
    }

    private async Task<IReadOnlyList<BackupControlNasOption>> ReadNasOptionsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, display_name, agency_code, host, share_name
            FROM nas_appliances
            WHERE enabled = TRUE
            ORDER BY agency_code, display_name;
            """;
        List<BackupControlNasOption> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        }
        return items;
    }

    private static void AddProfileParameters(
        NpgsqlCommand command,
        Guid id,
        BackupControlProfileRequest request,
        DateTimeOffset now,
        string actor)
    {
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Code);
        command.Parameters.AddWithValue(request.ScopeType);
        AddNullable(command, NpgsqlDbType.Uuid, request.DeviceId);
        command.Parameters.AddWithValue(request.AgencyCode);
        command.Parameters.AddWithValue(request.SystemName);
        command.Parameters.AddWithValue(request.ProtectedScope);
        command.Parameters.AddWithValue(request.Provider);
        command.Parameters.AddWithValue(request.OperationMode);
        command.Parameters.AddWithValue(request.Technology);
        AddNullable(command, NpgsqlDbType.Varchar, request.TaskName);
        AddNullable(command, NpgsqlDbType.Uuid, request.NasApplianceId);
        AddNullable(command, NpgsqlDbType.Varchar, request.ExpectedDestination);
        command.Parameters.AddWithValue(request.FrequencyHours);
        command.Parameters.AddWithValue(request.GraceMinutes);
        AddNullable(command, NpgsqlDbType.Integer, request.RetentionDays);
        AddNullable(command, NpgsqlDbType.Integer, request.RpoHours);
        AddNullable(command, NpgsqlDbType.Integer, request.RtoMinutes);
        AddNullable(command, NpgsqlDbType.Integer, request.RestoreTestIntervalDays);
        command.Parameters.AddWithValue(request.TechnicalResponsible);
        AddNullable(command, NpgsqlDbType.Varchar, request.ProcessOwner);
        command.Parameters.AddWithValue(request.Enabled);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(request.StandardId!.Value);
        command.Parameters.AddWithValue(request.ConfigurationMode);
        AddNullable(command, NpgsqlDbType.Varchar, request.ConfigurationReason);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = request.ProtectedScopeCategories?.ToArray() ?? []
        });
        AddNullable(command, NpgsqlDbType.Text, request.ProtectedScopeAdditional);
        AddNullable(command, NpgsqlDbType.Text, request.ProtectedScopeExclusions);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(request.FieldOrigins ??
                new Dictionary<string, string>())
        });
        command.Parameters.AddWithValue(request.ExceptionType);
        AddNullable(command, NpgsqlDbType.Varchar, request.ExceptionReason);
        AddNullable(
            command, NpgsqlDbType.TimestampTz, request.ExceptionExpiresAtUtc);
        command.Parameters.AddWithValue(BackupApprovalStatuses.ForSave(request));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Array|NpgsqlDbType.Text,
            Value=request.ProtectedFolders is null ? DBNull.Value : request.ProtectedFolders.ToArray() });
    }

    private static string NormalizeDeviceProvider(string value) => value switch
    {
        "ATLAS" => BackupControlProviders.AtlasBackupNas,
        "ACRONIS" => BackupControlProviders.Acronis,
        _ => BackupControlProviders.Other
    };

    private static IReadOnlyDictionary<string, string> DeserializeOrigins(
        string value) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(value) ??
        new Dictionary<string, string>();

    private static string? NormalizeRunResult(string? value) => value switch
    {
        "Running" => BackupControlResults.Running,
        "Skipped" => BackupControlResults.Skipped,
        "Partial" => BackupControlResults.Partial,
        "Unconfirmed" or "UNCONFIRMED" => BackupControlResults.Unconfirmed,
        "Successful" => BackupControlResults.Success,
        "Warning" => BackupControlResults.Warning,
        "Cancelled" => BackupControlResults.Cancelled,
        "Failed" => BackupControlResults.Failed,
        _ when value is not null => BackupControlResults.Unknown,
        _ => null
    };

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? NullableDate(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNullable(
        NpgsqlCommand command,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value
        });
}
