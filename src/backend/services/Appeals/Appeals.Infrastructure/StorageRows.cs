namespace Matigr.Appeals.Infrastructure;

internal sealed class AppealRow
{
    public Guid AppealId { get; set; }
    public Guid TournamentId { get; set; }
    public Guid TeamId { get; set; }
    public Guid TaskId { get; set; }
    public Guid ResultId { get; set; }
    public long ExpectedResultVersion { get; set; }
    public Guid SolutionId { get; set; }
    public Guid MaterialVersionId { get; set; }
    public Guid BaseCheckId { get; set; }
    public Guid BasePublicationId { get; set; }
    public Guid? FinalPublicationId { get; set; }
    public long WindowVersion { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string Basis { get; set; } = "{}";
    public string Snapshot { get; set; } = "{}";
    public string State { get; set; } = "Registered";
    public long Version { get; set; } = 1;
    public Guid? CurrentRequestId { get; set; }
    public Guid? CheckId { get; set; }
}
internal sealed class AttemptRow
{
    public Guid RequestId { get; set; }
    public Guid AppealId { get; set; }
    public Guid ReviewerId { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public string State { get; set; } = "Pending";
    public string PayloadHash { get; set; } = "";
    public string? ReasonCode { get; set; }
}
internal sealed class HistoryRow
{
    public Guid HistoryId { get; set; }
    public Guid AppealId { get; set; }
    public long Version { get; set; }
    public string State { get; set; } = "";
    public Guid? ActorId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? CheckId { get; set; }
    public string Snapshot { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
internal sealed class EndFactRow
{
    public Guid FactId { get; set; }
    public Guid TournamentId { get; set; }
    public Guid ActorId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Response { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}
internal sealed class HttpReceiptRow
{
    public Guid SubjectId { get; set; }
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string Response { get; set; } = "{}";
    public string? ETag { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
internal sealed class InboxRow
{
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}
internal sealed class OutboxRow
{
    public Guid MessageId { get; set; }
    public string Envelope { get; set; } = "{}";
    public string RoutingKey { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
internal sealed class FailureRow
{
    public Guid FailureId { get; set; }
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Envelope { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public string State { get; set; } = "Deferred";
    public string ReasonCode { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int RedriveCount { get; set; }
    public DateTimeOffset? RedrivenAt { get; set; }
}
internal sealed class RedriveRow
{
    public Guid RedriveId { get; set; }
    public Guid FailureId { get; set; }
    public Guid MessageId { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset RedrivenAt { get; set; }
}
