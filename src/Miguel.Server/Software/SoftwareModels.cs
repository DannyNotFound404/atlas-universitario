namespace Miguel.Server.Software;

public sealed record SoftwareQuery(string View = "products", string Search = "", string Agency = "", string Status = "", string Product = "", string Device = "", int Page = 1);
public sealed record SoftwareProductRequest(int ExpectedRevision, string Category, string LicenseMetric);
public sealed record SoftwareRuleRequest(int ExpectedRevision, string Version, string Authorization, DateOnly? SupportEndsOn, string Reference, string Notes);
public sealed record SoftwareLicenseRequest(int ExpectedRevision, string ProductId, string Reference, string Metric, int Quantity, DateOnly StartsOn, DateOnly? ExpiresOn, string Notes, bool Active = true, IReadOnlyList<string>? CoveredVersions = null);
public sealed record SoftwareAssignmentRequest(Guid? DeviceId, string Subject);
public sealed record SoftwareActionRequest(int ExpectedRevision, string? ProductId, Guid? DeviceId, string Title, string Owner, DateOnly DueOn, string Status, string Result, string Reference);
public sealed record SoftwareReviewRequest(Guid RequestId, string Period, string ExecutedBy, string ValidatedBy, string Reference, string Conclusion);
