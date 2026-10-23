namespace Matigr.Results.Infrastructure;

internal sealed class ResultRow
{
    public Guid ResultId { get; set; }
    public Guid TournamentId { get; set; }
    public Guid TeamId { get; set; }
    public Guid TaskId { get; set; }
    public Guid SolutionId { get; set; }
    public Guid MaterialVersionId { get; set; }
    public long ResultVersion { get; set; }
    public decimal Score { get; set; }
    public string Outcome { get; set; } = "";
    public Guid PublicationId { get; set; }
    public string Publication { get; set; } = "{}";
}
internal sealed class PublicationRow
{
    public Guid PublicationId { get; set; }
    public Guid ResultId { get; set; }
    public long ResultVersion { get; set; }
    public string Snapshot { get; set; } = "{}";
}
internal sealed class ReceiptRow
{
    public Guid EvaluationId { get; set; }
    public string SemanticHash { get; set; } = "";
    public string Status { get; set; } = "";
    public Guid? ResultId { get; set; }
    public string Response { get; set; } = "{}";
}
internal sealed class SnapshotRow
{
    public Guid TournamentId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string ManifestHash { get; set; } = "";
    public long FixtureVersion { get; set; }
}
internal sealed class TeamRow
{
    public Guid TournamentId { get; set; }
    public Guid TeamId { get; set; }
    public string Name { get; set; } = "";
}
internal sealed class TaskRow
{
    public Guid TournamentId { get; set; }
    public Guid TaskId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
}
internal sealed class RuleRow
{
    public Guid TournamentId { get; set; }
    public Guid TaskId { get; set; }
    public long RuleVersion { get; set; }
    public decimal Points { get; set; }
    public string ManifestHash { get; set; } = "";
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
    public Guid? LockId { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
internal sealed class FailureRow
{
    public string RoutingKey { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public int RedriveCount { get; set; }
    public DateTimeOffset? RedrivenAt { get; set; }
    public Guid FailureId { get; set; }
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public string State { get; set; } = "Deferred";
    public string ReasonCode { get; set; } = "";
    public string Envelope { get; set; } = "{}";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}

internal sealed class TransportRedriveRow
{
    public Guid RedriveId { get; set; }
    public Guid FailureId { get; set; }
    public Guid MessageId { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset RedrivenAt { get; set; }
}
