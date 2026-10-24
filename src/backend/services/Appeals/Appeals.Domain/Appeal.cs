namespace Matigr.Appeals.Domain;

public enum AppealState { Registered, AcceptedAwaitingRecheck, RecheckPending, AwaitingSeniorReview, AwaitingResultApplication, Closed, NeedsResolution }
public enum DecisionOrigin { OrdinaryReview, AppealReview, SeniorEscalation }
public enum Outcome { Correct, Incorrect }
public enum AttemptState { Pending, Assigned, RejectedBusy, RejectedConflict }
public sealed class AppealRuleException(string code) : Exception(code);
public sealed record PublishedBasis(Guid ResultId, long ResultVersion, Guid TournamentId, Guid TeamId, Guid TaskId,
    Guid SolutionId, Guid MaterialVersionId, Guid BaseCheckId, DecisionOrigin Origin, Outcome Outcome,
    string ScoreRuleVersion, Guid PublicationId, DateTimeOffset PublishedAt)
{
    public void Validate()
    {
        if (new[] { ResultId, TournamentId, TeamId, TaskId, SolutionId, MaterialVersionId, BaseCheckId, PublicationId }.Contains(Guid.Empty)
            || ResultVersion < 1 || string.IsNullOrWhiteSpace(ScoreRuleVersion) || !Enum.IsDefined(Origin) || !Enum.IsDefined(Outcome))
            throw new ArgumentException("Invalid published basis.");
    }
}
public sealed record SelectionAttempt(Guid RequestId, Guid ReviewerId, DateTimeOffset AcceptedAt, AttemptState State);
public sealed record ReviewProgress(Guid RequestId, Guid CheckId, Guid SelectedReviewerId, long CheckVersion,
    Guid? AssignmentId, Guid? ReviewerId, Guid? EscalationId);
public sealed record PublishedReceipt(Guid EvaluationId, Guid RequestId, Guid SelectedReviewerId, Guid ReviewerId, Guid AssignmentId,
    DecisionOrigin Origin, Guid? EscalationId, PublishedBasis Basis, long ResultVersion, Guid PublicationId, Outcome Outcome, bool Changed);

// Local process model: no Checking/Results aggregate references and no scoring or assignment authority.
public sealed record Appeal(Guid AppealId, PublishedBasis Basis, DateTimeOffset SubmittedAt, long WindowVersion,
    AppealState State = AppealState.Registered, long Version = 1, SelectionAttempt? Attempt = null,
    ReviewProgress? Progress = null, PublishedReceipt? Receipt = null, string? Conflict = null)
{
    private bool Terminal => State is AppealState.Closed or AppealState.NeedsResolution;
    public static Appeal Register(Guid id, PublishedBasis basis, Guid team, SubmissionWindow window, DateTimeOffset now, bool quotaUsed)
    {
        basis.Validate();
        if (id == Guid.Empty) throw new ArgumentException("Appeal identity required.");
        if (basis.TeamId != team || basis.TournamentId != window.TournamentId) throw new AppealRuleException("Forbidden");
        if (quotaUsed) throw new AppealRuleException("AlreadyAppealed");
        if (!window.Open) throw new AppealRuleException("WindowClosed");
        if (basis.Origin == DecisionOrigin.SeniorEscalation) throw new AppealRuleException("BaseNotAppealable");
        if (now < basis.PublishedAt) throw new ArgumentException("Publication must precede submission.");
        return new(id, basis, now.ToUniversalTime(), window.Version);
    }
    public Appeal Claim(Guid request, Guid reviewer, DateTimeOffset now)
    {
        if (State != AppealState.Registered) throw new AppealRuleException("AppealNotRegistered");
        if (request == Guid.Empty || reviewer == Guid.Empty || now < SubmittedAt || request == Attempt?.RequestId) throw new ArgumentException("Invalid selection.");
        return this with { State = AppealState.AcceptedAwaitingRecheck, Version = checked(Version + 1),
            Attempt = new(request, reviewer, now.ToUniversalTime(), AttemptState.Pending), Progress = null };
    }
    public Appeal RequestRejected(Guid request, string reason)
    {
        if (Terminal || Attempt?.RequestId != request) return this;
        if (State != AppealState.AcceptedAwaitingRecheck || Progress is not null) throw new AppealRuleException("ConflictingRecheckReply");
        var busy = reason == "ReviewerBusy";
        return this with { State = busy ? AppealState.Registered : AppealState.NeedsResolution, Version = checked(Version + 1),
            Attempt = Attempt with { State = busy ? AttemptState.RejectedBusy : AttemptState.RejectedConflict }, Conflict = busy ? null : reason };
    }
    private void Correlate(ReviewProgress fact)
    {
        if (Attempt is null || fact.RequestId != Attempt.RequestId || fact.SelectedReviewerId != Attempt.ReviewerId
            || fact.CheckId == Guid.Empty || fact.CheckVersion < 1 || (Progress is not null && Progress.CheckId != fact.CheckId))
            throw new AppealRuleException("RecheckCorrelationConflict");
        if (fact.AssignmentId == Guid.Empty || fact.ReviewerId == Guid.Empty || fact.EscalationId == Guid.Empty
            || (fact.EscalationId is null && fact.ReviewerId is { } reviewer && reviewer != fact.SelectedReviewerId)
            || (Progress?.EscalationId is { } escalation && fact.EscalationId != escalation && fact.CheckVersion > Progress.CheckVersion))
            throw new AppealRuleException("RecheckLineageConflict");
    }
    public Appeal Observe(ReviewProgress fact, AppealState target)
    {
        // Facts from a previous rejected-busy attempt cannot affect a new manual choice.
        if (Attempt?.RequestId != fact.RequestId) return this;
        Correlate(fact);
        if (Terminal || (Progress is not null && fact.CheckVersion <= Progress.CheckVersion)) return this;
        if (State == AppealState.Registered || target is not (AppealState.RecheckPending or AppealState.AwaitingSeniorReview or AppealState.AwaitingResultApplication))
            throw new AppealRuleException("InvalidRecheckTransition");
        if (target == AppealState.AwaitingSeniorReview && fact.EscalationId is null) throw new AppealRuleException("MissingEscalation");
        if (State == AppealState.AwaitingResultApplication && target != AppealState.AwaitingResultApplication) return this;
        if (State == AppealState.AwaitingSeniorReview && target == AppealState.RecheckPending && fact.EscalationId is null) return this;
        return this with { State = target, Version = checked(Version + 1), Attempt = Attempt! with { State = AttemptState.Assigned }, Progress = fact };
    }
    public Appeal Applied(PublishedReceipt receipt)
    {
        receipt.Basis.Validate();
        if (Attempt is null) throw new AppealRuleException("ResultReceiptCorrelationConflict");
        if (receipt.Basis != Basis || receipt.RequestId != Attempt.RequestId || receipt.SelectedReviewerId != Attempt.ReviewerId
            || receipt.EvaluationId == Guid.Empty || receipt.AssignmentId == Guid.Empty || receipt.ReviewerId == Guid.Empty
            || receipt.PublicationId == Guid.Empty || !Enum.IsDefined(receipt.Outcome)
            || (receipt.Origin == DecisionOrigin.AppealReview ? receipt.ReviewerId != receipt.SelectedReviewerId || receipt.EscalationId is not null
                : receipt.Origin != DecisionOrigin.SeniorEscalation || receipt.EscalationId is null || receipt.EscalationId == Guid.Empty)
            || (Progress is not null && (Progress.CheckId != receipt.EvaluationId || (Progress.EscalationId is { } escalation && escalation != receipt.EscalationId))))
            throw new AppealRuleException("ResultReceiptCorrelationConflict");
        if (receipt.Changed ? receipt.ResultVersion != checked(Basis.ResultVersion + 1) || receipt.PublicationId == Basis.PublicationId || receipt.Outcome == Basis.Outcome
            : receipt.ResultVersion != Basis.ResultVersion || receipt.PublicationId != Basis.PublicationId || receipt.Outcome != Basis.Outcome)
            throw new AppealRuleException("ResultPublicationConflict");
        if (State == AppealState.Closed) {
            if (Receipt != receipt) throw new AppealRuleException("ConflictingTerminalReceipt");
            return this;
        }
        if (Terminal || State == AppealState.Registered) throw new AppealRuleException("InvalidResultReceiptState");
        return this with { State = AppealState.Closed, Version = checked(Version + 1), Receipt = receipt,
            Attempt = Attempt with { State = AttemptState.Assigned }, Conflict = null };
    }
    public Appeal ResultRejected(Guid request, string reason) => Terminal || Attempt?.RequestId != request ? this
        : this with { State = AppealState.NeedsResolution, Version = checked(Version + 1), Conflict = reason };
}

public sealed record SubmissionWindow(Guid TournamentId, bool Open = true, long Version = 1, Guid? EndFactId = null,
    Guid? EndedBy = null, DateTimeOffset? EndedAt = null)
{
    public SubmissionWindow Close(Guid fact, Guid organizer, DateTimeOffset now)
    {
        if (fact == Guid.Empty || organizer == Guid.Empty) throw new ArgumentException("Manual end identity required.");
        return !Open ? this : this with { Open = false, Version = checked(Version + 1), EndFactId = fact, EndedBy = organizer, EndedAt = now.ToUniversalTime() };
    }
}
