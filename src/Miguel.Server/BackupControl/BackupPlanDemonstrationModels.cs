using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miguel.Server.BackupControl;

public sealed record BackupDemonstrationCandidate(Guid ProfileId, Guid? DeviceId, string Name,
    string Agency, int PlanRevision, Guid? NasId, string Destination, string Scope,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset? LastTestAtUtc)
{
    public string BindingStamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { ProfileId, DeviceId, Name, Agency, PlanRevision, NasId, Destination, Scope, UpdatedAtUtc }))));
    public string Identity => (DeviceId ?? ProfileId).ToString("D");
}
public sealed record BackupDemonstrationSampleRequest(Guid ProfileId, string BindingStamp,
    BackupRestorationRequest Restoration, Guid? RepeatsSampleId = null);
public sealed record BackupDemonstrationRequest(Guid RequestId, int ExpectedRevision,
    IReadOnlyList<BackupDemonstrationSampleRequest> Samples, string? Notes = null);
public sealed record BackupPlanDemonstrationSummary(Guid PlanId, string Name, int Revision,
    int EligibleCount, int RequiredSamples, Guid? LatestId, int Tested, int Successful,
    int UnresolvedSamples, DateTimeOffset? LastAtUtc, DateTimeOffset? NextDueUtc,
    string Status, bool Pending, int? LatestRequiredSamples = null, string? Provider = null);
public sealed record BackupDemonstrationAttempt(Guid Id, Guid ProfileId, Guid? RepeatsSampleId,
    BackupDemonstrationCandidate Snapshot, BackupRestorationRequest Restoration,
    DateTimeOffset CreatedAtUtc, string CreatedBy);
public sealed record BackupDemonstrationEvidence(Guid Id, string FileName, string Sha256, Guid RequestId);
public sealed record BackupDemonstrationView(Guid Id, int PlanRevision, string PlanName,
    int PopulationCount, int RequiredSamples, string? Notes, DateTimeOffset CreatedAtUtc,
    string CreatedBy, IReadOnlyList<BackupDemonstrationAttempt> Attempts,
    IReadOnlyList<BackupDemonstrationEvidence> Evidence, int? OriginalRequiredSamples = null);
public sealed record BackupDemonstrationWorkspace(BackupPlanDemonstrationSummary Summary,
    IReadOnlyList<BackupDemonstrationCandidate> Candidates, IReadOnlyList<Guid> SuggestedProfiles,
    IReadOnlyList<BackupDemonstrationView> History, IReadOnlyList<BackupLegacyRestorationFailure> LegacyFailures);
public sealed record BackupLegacyRestorationFailure(Guid ProfileId,string Name,string Result,DateTimeOffset AtUtc);

public static class BackupDemonstrationRules
{
    // Evaluate the provider captured with the session, without rewriting its original requirement or evidence.
    // SQL callers use alias d for backup_plan_demonstrations.
    internal const string EffectiveRequiredSql = "CASE WHEN d.configuration->>'provider'='ACRONIS' THEN 1 ELSE d.required_samples END";

    public static int Required(int population, string? provider = null) =>
        Math.Min(provider == BackupControlProviders.Acronis ? 1 : 3, Math.Max(0,population));

    public static BackupDemonstrationRequest Normalize(BackupDemonstrationRequest request, DateTimeOffset now, bool retry)
    {
        if (request.RequestId == Guid.Empty || request.ExpectedRevision < 1 || request.Notes?.Length > 4000 ||
            request.Samples is null || request.Samples.Count < 1 || request.Samples.Count > (retry ? 1 : 3) ||
            request.Samples.Any(s => s is null) || request.Samples.Select(s => s.ProfileId).Distinct().Count()!=request.Samples.Count)
            throw new BackupWorkspaceException("Selecciona muestras diferentes y revisa los datos de la demostración.");
        List<BackupDemonstrationSampleRequest> samples=[];
        foreach (var sample in request.Samples)
        {
            var r=sample.Restoration;
            if (sample.ProfileId==Guid.Empty || string.IsNullOrWhiteSpace(sample.BindingStamp) ||
                (sample.RepeatsSampleId.HasValue != retry) || r is null ||
                !BackupControlInput.TryNormalize(r,out var normalized) ||
                normalized.Result is not ("SUCCESS" or "WARNING" or "FAILED") ||
                normalized.StartedAtUtc==default || normalized.FinishedAtUtc>now.AddMinutes(5) ||
                normalized.BackupUsedAtUtc is null || normalized.BackupUsedAtUtc>normalized.StartedAtUtc ||
                normalized.ValidatedAtUtc>now.AddMinutes(5) || normalized.ValidatedAtUtc<normalized.FinishedAtUtc ||
                normalized.Historical || normalized.NextTestDueUtc.HasValue || normalized.OperationalCaseId.HasValue)
                throw new BackupWorkspaceException("Revisa la copia utilizada, fechas, alcance y comprobaciones de cada muestra. Un resultado satisfactorio requiere integridad y funcionamiento comprobados.");
            samples.Add(sample with { Restoration=normalized });
        }
        return request with { Samples=samples,Notes=request.Notes?.Trim() };
    }

    // Rotation and destination diversity are operational suggestions, never a random/statistical sample.
    public static IReadOnlyList<Guid> Suggest(IReadOnlyList<BackupDemonstrationCandidate> candidates, string? provider = null)
    {
        List<BackupDemonstrationCandidate> remaining=candidates.OrderBy(c=>c.LastTestAtUtc).ThenBy(c=>c.Name,StringComparer.Ordinal).ToList();
        List<BackupDemonstrationCandidate> chosen=[];
        while(chosen.Count<Required(candidates.Select(c=>c.Identity).Distinct().Count(),provider) && remaining.Count>0)
        {
            var next=remaining.FirstOrDefault(c=>!chosen.Any(s=>s.Destination==c.Destination)) ?? remaining[0];
            chosen.Add(next);remaining.RemoveAll(c=>c.Identity==next.Identity);
        }
        return chosen.Select(c=>c.ProfileId).ToArray();
    }
}
