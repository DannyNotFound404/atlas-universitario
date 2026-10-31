using System.Text.Json;
using Miguel.Server.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.BackupControl;

public sealed class BackupScheduleService(NpgsqlDataSource db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Stored(int Revision, BackupScheduleSetting Setting);
    private static string Identity(string type, string key) => type + ":" + key;

    private async Task<Dictionary<string, Stored>> ReadSettings(CancellationToken ct, DateTimeOffset? at = null)
    {
        Dictionary<string, Stored> values = [];
        await using var cmd = db.CreateCommand(at.HasValue ? """
            SELECT DISTINCT ON (scope_type,scope_key) scope_type,scope_key,revision,after_value::text
            FROM backup_control_schedule_history WHERE occurred_at_utc<=$1
            ORDER BY scope_type,scope_key,revision DESC
            """ : "SELECT scope_type,scope_key,revision,value::text FROM backup_control_schedules");
        if (at.HasValue) cmd.Parameters.AddWithValue(at.Value.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values[Identity(reader.GetString(0), reader.GetString(1))] =
            new(reader.GetInt32(2), JsonSerializer.Deserialize<BackupScheduleSetting>(reader.GetString(3), Json)!);
        return values;
    }

    public async Task<IReadOnlyDictionary<Guid, BackupScheduleSetting>> ResolveSettingsAsync(
        IReadOnlyList<BackupControlProfileView> profiles, DateTimeOffset at, CancellationToken ct)
        => await ResolveSettingsAsync(profiles.Select(p=>(p.Id,p.PlanId,p.AgencyCode)).ToArray(),at,ct);

    public async Task<IReadOnlyDictionary<Guid, BackupScheduleSetting>> ResolveSettingsAsync(
        IReadOnlyList<(Guid Id,Guid? PlanId,string AgencyCode)> profiles, DateTimeOffset at, CancellationToken ct)
    {
        // Historical report cutoffs use only settings actually registered then.
        var values = await ReadSettings(ct, at);
        Dictionary<Guid, BackupScheduleSetting> result = [];
        foreach (var profile in profiles)
        {
            BackupScheduleSetting chosen = BackupScheduleRules.Inherit() with { Mode = "LEGACY" };
            foreach (var (type, key) in new[] { ("PROFILE", profile.Id.ToString("D")),
                ("PLAN", profile.PlanId?.ToString("D") ?? ""), ("AGENCY", profile.AgencyCode) })
                if (values.TryGetValue(Identity(type, key), out var value) && BackupScheduleRules.Active(value.Setting, at))
                { chosen = value.Setting; break; }
            result[profile.Id] = chosen;
        }
        return result;
    }

    public async Task<BackupScheduleDashboard> ReadAsync(CancellationToken ct)
    {
        var settings = await ReadSettings(ct);
        async Task<IReadOnlyList<BackupScheduleOption>> Options(string type, string sql)
        {
            List<BackupScheduleOption> result = [];
            await using var cmd = db.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string key = reader.GetString(0);
                settings.TryGetValue(Identity(type, key), out var value);
                result.Add(new(key, reader.GetString(1), value?.Revision ?? 0,
                    value?.Setting ?? BackupScheduleRules.Inherit(), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            return result;
        }
        return new(clock.GetUtcNow(),
            await Options("AGENCY", "SELECT code,display_name,code FROM agencies WHERE enabled ORDER BY display_name"),
            await Options("PLAN", "SELECT id::text,name,NULL::text FROM backup_control_plans ORDER BY name"),
            await Options("PROFILE", "SELECT id::text,system_name,agency_code FROM backup_control_profiles WHERE enabled ORDER BY agency_code,system_name"));
    }

    public async Task<IReadOnlyDictionary<Guid, BackupScheduleStatus>> EvaluateAsync(
        IReadOnlyList<BackupControlProfileView> profiles, DateTimeOffset now, CancellationToken ct)
    {
        var values = await ReadSettings(ct);
        Dictionary<Guid, BackupScheduleStatus> result = [];
        foreach (var profile in profiles)
        {
            bool expired = false;
            Stored? chosen = null;
            string source = "UNCONFIGURED";
            foreach (var (type, key) in new[] { ("PROFILE", profile.Id.ToString("D")),
                ("PLAN", profile.PlanId?.ToString("D") ?? ""), ("AGENCY", profile.AgencyCode) })
            {
                if (!values.TryGetValue(Identity(type, key), out var value)) continue;
                if (type == "PROFILE" && value.Setting.Mode == "CALENDAR" && value.Setting.ValidUntil.HasValue)
                {
                    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,
                        ConfigurationService.Zone(value.Setting.TimeZone)).DateTime);
                    expired = date > value.Setting.ValidUntil.Value;
                }
                if (!BackupScheduleRules.Active(value.Setting, now)) continue;
                chosen = value; source = type; break;
            }
            result[profile.Id] = BackupScheduleRules.EvaluateCalendar(chosen?.Setting ?? BackupScheduleRules.Inherit() with { Mode = "LEGACY" },
                profile.LastAcceptableAtUtc, now, source, expired, chosen?.Revision ?? 0);
        }
        return result;
    }

    public async Task<BackupScheduleOption> SaveAsync(string type, string key, BackupScheduleChange request,
        string actor, CancellationToken ct)
    {
        if (type == "AGENCY") key = key.Trim().ToUpperInvariant();
        else if (Guid.TryParse(key, out var id)) key = id.ToString("D");
        else throw new BackupWorkspaceException("Identificador de calendario no válido.");
        DateTimeOffset now = clock.GetUtcNow();
        var setting = BackupScheduleRules.Normalize(request, type, now);
        string target = type switch
        {
            "AGENCY" => "SELECT display_name FROM agencies WHERE code=$1 AND enabled FOR KEY SHARE",
            "PLAN" => "SELECT name FROM backup_control_plans WHERE id::text=$1 FOR KEY SHARE",
            "PROFILE" => "SELECT system_name FROM backup_control_profiles WHERE id::text=$1 AND enabled FOR KEY SHARE",
            _ => throw new BackupWorkspaceException("Tipo de calendario no válido.")
        };
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(393221)", connection, transaction))
            await gate.ExecuteNonQueryAsync(ct);
        string label;
        await using (var command = new NpgsqlCommand(target, connection, transaction))
        {
            command.Parameters.AddWithValue(key);
            label = (string?)await command.ExecuteScalarAsync(ct) ?? throw new BackupWorkspaceException("El destino del calendario ya no está disponible.", true);
        }
        int revision = 0; string? previous = null;
        await using (var command = new NpgsqlCommand("SELECT revision,value::text FROM backup_control_schedules WHERE scope_type=$1 AND scope_key=$2 FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue(type); command.Parameters.AddWithValue(key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) { revision = reader.GetInt32(0); previous = reader.GetString(1); }
        }
        if (revision != request.ExpectedRevision) throw new BackupWorkspaceException("Otro usuario cambió el calendario. Actualice antes de guardar.", true);
        // A descriptive edit does not silently reset the first scheduled obligation.
        if (previous is not null)
        {
            var old = JsonSerializer.Deserialize<BackupScheduleSetting>(previous, Json)!;
            if (old.Mode == setting.Mode && old.TimeZone == setting.TimeZone &&
                old.Weekdays.SequenceEqual(setting.Weekdays) && old.ExcludedDates.SequenceEqual(setting.ExcludedDates) &&
                old.ValidFrom == setting.ValidFrom && old.ValidUntil == setting.ValidUntil)
                setting = setting with { AppliesFromUtc = old.AppliesFromUtc };
        }
        string json = JsonSerializer.Serialize(setting, Json);
        await using (var command = new NpgsqlCommand("""
            WITH saved AS (
            INSERT INTO backup_control_schedules(scope_type,scope_key,revision,value,updated_at_utc,updated_by)
            VALUES($1,$2,$3,$4::jsonb,$5,$6)
            ON CONFLICT(scope_type,scope_key) DO UPDATE SET revision=EXCLUDED.revision,value=EXCLUDED.value,
                updated_at_utc=EXCLUDED.updated_at_utc,updated_by=EXCLUDED.updated_by
            RETURNING scope_type)
            INSERT INTO backup_control_schedule_history(scope_type,scope_key,revision,before_value,after_value,reason,actor,occurred_at_utc)
            VALUES($1,$2,$3,$7::jsonb,$4::jsonb,$8,$6,$5);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(type); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(revision + 1);
            command.Parameters.AddWithValue(json); command.Parameters.AddWithValue(now.ToUniversalTime()); command.Parameters.AddWithValue(actor);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)previous ?? DBNull.Value });
            command.Parameters.AddWithValue(setting.Reason); await command.ExecuteNonQueryAsync(ct);
        }
        await ConfigurationService.AuditAsync(connection, transaction, actor, "admin.backup.schedule.changed", "BackupSchedule", type + ":" + key,
            new { revision = revision + 1, before = previous is null ? null : JsonSerializer.Deserialize<BackupScheduleSetting>(previous, Json), after = setting }, ct);
        await transaction.CommitAsync(ct);
        return new(key, label, revision + 1, setting);
    }
}
