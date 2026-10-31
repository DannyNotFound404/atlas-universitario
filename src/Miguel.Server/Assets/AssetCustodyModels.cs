namespace Miguel.Server.Assets;

public sealed record PreparationCheck(string Result, string Reference);
public sealed record AssetPreparation(Dictionary<string, PreparationCheck> Checks, string ExecutedBy, string? ValidatedBy = null);
public sealed record AssetReceipt(string PreviousAssignee, string Condition, string Accessories, string Reference);
public sealed record AssetCustodyRequest(string Action, string ExpectedVersion, string Reason,
    string? AssignedTo = null, string? Custodian = null, string? Location = null,
    AssetReceipt? Receipt = null, AssetPreparation? Preparation = null,
    bool PauseBackups = false, DateTimeOffset? ReviewDueUtc = null, bool BackupReviewed = false,
    Guid? NewDeviceId = null, bool PhysicalIdentityVerified = false, string? SuggestionVersion = null);
public sealed record AssetCustodyState(Guid DeviceId, long Revision, string State, string? AssignedTo,
    string Custodian, string Location, AssetReceipt? Receipt, AssetPreparation? Preparation,
    bool BackupsPaused, string? PauseReason, DateTimeOffset? ReviewDueUtc,
    DateTimeOffset? TrackingSinceUtc, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy, Guid CurrentDeviceId);
public sealed record AssetBackupContext(Guid Id, string Code, string Provider, string Scope, string? Destination,
    bool Enabled, string Status, DateTimeOffset UpdatedAtUtc, bool DestinationValid);
public sealed record AssetCustodyEvent(long Revision, string Action, DateTimeOffset OccurredAtUtc, string Actor, System.Text.Json.JsonElement Detail);
public sealed record AssetCustodyView(string MachineName, string AgencyCode, string? SerialNumber, bool Enabled,
    string BackupProvider, string Version, AssetCustodyState Custody,
    IReadOnlyList<AssetBackupContext> Backups, IReadOnlyList<AssetCustodyEvent> Events,
    IReadOnlyList<AssetIdentityCandidate> IdentityCandidates, AssetCustodyProposal? Suggestion = null);
public sealed record AssetIdentityCandidate(Guid DeviceId, string MachineName, string SerialNumber);
public sealed class AssetCustodyConflict(string code) : Exception(code);

public static class AssetCustodyInput
{
    // Evidence references describe work done elsewhere; no files, secrets or backup contents here.
    public static readonly string[] CheckKeys = ["information", "access", "sanitization", "hardware", "security", "software", "atlas", "backup"];
    public static string Text(string? value, int max, string field)
    {
        string text = value?.Trim() ?? "";
        if (text.Length < 2 || text.Length > max || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("invalid_" + field);
        return text;
    }
    public static AssetPreparation Preparation(AssetPreparation? value, bool complete)
    {
        if (value?.Checks is null || value.Checks.Count != CheckKeys.Length || CheckKeys.Any(k => !value.Checks.ContainsKey(k)))
            throw new ArgumentException("invalid_preparation");
        Dictionary<string, PreparationCheck> checks = [];
        foreach (string key in CheckKeys)
        {
            var check = value.Checks[key];
            if (check is null || check.Result is not ("PENDING" or "DONE" or "NA")) throw new ArgumentException("invalid_preparation");
            if (complete && check.Result == "PENDING") throw new ArgumentException("preparation_incomplete");
            // Core safety checks must be completed, even for a device whose access removal does not apply.
            if (check.Result == "NA" && key is "information" or "sanitization" or "hardware" or "security" or "atlas" or "backup")
                throw new ArgumentException("required_preparation_check");
            string reference = check.Result == "PENDING" ? (check.Reference ?? "").Trim() : Text(check.Reference, 1000, "check_reference");
            if (reference.Length > 1000) throw new ArgumentException("invalid_check_reference");
            checks[key] = new(check.Result, reference);
        }
        return new(checks, Text(value.ExecutedBy, 160, "executed_by"),
            complete ? Text(value.ValidatedBy, 160, "validated_by") : null);
    }
}
