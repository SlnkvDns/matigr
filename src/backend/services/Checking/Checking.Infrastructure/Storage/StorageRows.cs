using Matigr.Checking.Domain;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed class FixtureSetRow
{
    public string Name { get; set; } = "";
    public long Version { get; set; }
    public string ManifestHash { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
}

// Persistence records stay local to Checking. They are neither HTTP DTOs nor shared aggregates.
internal sealed class SolutionReferenceRow
{
    public Guid SolutionId { get; set; }
    public Guid TournamentId { get; set; }
    public Guid TeamId { get; set; }
    public Guid TaskId { get; set; }
    public Guid MaterialVersionId { get; set; }
    public string MaterialRef { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
    public long SourceVersion { get; set; }
}

internal sealed class CheckRow
{
    public Guid CheckId { get; set; }
    public Guid SolutionId { get; set; }
    public Guid TournamentId { get; set; }
    public CheckKind Kind { get; set; }
    public long ReviewRevision { get; set; }
    public CheckState State { get; set; }
    public long Version { get; set; } = 1;
    public Guid? CurrentAssignmentId { get; set; }
    public CheckOutcome? Outcome { get; set; }
    public DecisionOrigin? Origin { get; set; }
    public Guid? EscalationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? AppealId { get; set; }
    public Guid? RecheckRequestId { get; set; }
    public Guid? ResultId { get; set; }
    public long? ExpectedResultVersion { get; set; }
    public Guid? BaseCheckId { get; set; }
    public Guid? BasePublicationId { get; set; }
    public Guid? SelectedReviewerId { get; set; }
    public CheckOutcome? BaseOutcome { get; set; }
}

internal sealed class AssignmentRow
{
    public Guid AssignmentId { get; set; }
    public Guid CheckId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid TournamentId { get; set; }
    public ReviewerRole Role { get; set; }
    public string CapabilityHash { get; set; } = "";
    public string CapabilityCiphertext { get; set; } = "";
    public AssignmentState State { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

internal sealed class ReviewerScopeRow
{
    public Guid ReviewerId { get; set; }
    public Guid TournamentId { get; set; }
    public ReviewerRole Role { get; set; }
}

internal sealed class CheckHistoryRow
{
    public Guid HistoryId { get; set; }
    public Guid CheckId { get; set; }
    public long Version { get; set; }
    public string TransitionType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? EscalationId { get; set; }
    public CheckState? FromState { get; set; }
    public CheckState ToState { get; set; }
    public CheckOutcome? Outcome { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class RecheckRequestReceiptRow
{
    public Guid RequestId { get; set; }
    public Guid AppealId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Outcome { get; set; } = ""; // Assigned or Rejected; Deferred is not a receipt.
    public Guid? CheckId { get; set; }
    public string? ReasonCode { get; set; }
    public string ResponseEnvelope { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class HttpIdempotencyRow
{
    public Guid SubjectId { get; set; }
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ETag { get; set; }
    public string ResponseCiphertext { get; set; } = "";
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
    public Guid CorrelationId { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class ProcessingFailureRow
{
    public Guid FailureId { get; set; }
    public string Consumer { get; set; } = "";
    public Guid? MessageId { get; set; }
    public string PayloadHash { get; set; } = "";
    // text preserves even malformed JSON for quarantine/redrive.
    public string RawEnvelope { get; set; } = "";
    public string State { get; set; } = "Deferred";
    public string ReasonCode { get; set; } = "";
    public Guid? CorrelationId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}
