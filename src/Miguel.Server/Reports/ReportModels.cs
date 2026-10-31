using System.Text.Json;

namespace Miguel.Server.Reports;

public sealed record ReportRequest(string Template, string? Agency = null, DateOnly? From = null,
    DateOnly? To = null, string View = "TECHNICAL");
public sealed record ReportJobRequest(Guid RequestKey, ReportRequest Report);
public sealed record ReportVariantRequest(string Name, ReportRequest Report);
public sealed record ReportScheduleRequest(string Name, ReportRequest Report, string Cadence,
    string TimeZone, DateTimeOffset FirstAtUtc);
public sealed record ReportScheduleUpdate(int Revision, bool Enabled);
public sealed record ReportTemplate(string Id, string Title, string Description, string Policy,
    bool AgencyFilter, string[] Sections);
public sealed record ReportTable(string Title, string Basis, string Source, string[] Columns, List<string[]> Rows);
public sealed record ReportSnapshot(Guid Id, string Title, int TemplateVersion, ReportRequest Filters,
    DateTimeOffset CutoffUtc, DateTimeOffset PeriodFromUtc, DateTimeOffset PeriodToExclusiveUtc,
    string TimeZone, string Institution, string Classification, string Author,
    string Policy, string[] Notes, List<ReportTable> Tables);
public sealed class PortalException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
public static class PortalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}
