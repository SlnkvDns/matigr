namespace Matigr.Checking.Domain;

public enum CheckKind
{
    Initial,
    Appeal
}

public enum CheckState
{
    Queued,
    Assigned,
    SeniorQueued,
    SeniorAssigned,
    Completed
}

public enum CheckOutcome
{
    Correct,
    Incorrect
}

public enum DecisionOrigin
{
    OrdinaryReview,
    AppealReview,
    SeniorEscalation
}

public sealed record AppealBasis
{
    public Guid AppealId { get; }
    public Guid RecheckRequestId { get; }
    public Guid ResultId { get; }
    public long ExpectedResultVersion { get; }
    public Guid BaseCheckId { get; }
    public Guid BasePublicationId { get; }
    public Guid SelectedReviewerId { get; }
    public CheckOutcome BaseOutcome { get; }

    public AppealBasis(Guid appealId, Guid recheckRequestId, Guid resultId, long expectedResultVersion,
        Guid baseCheckId, Guid basePublicationId, Guid selectedReviewerId, CheckOutcome baseOutcome)
    {
        if (new[] { appealId, recheckRequestId, resultId, baseCheckId, basePublicationId, selectedReviewerId }.Contains(Guid.Empty))
            throw new ArgumentException("Appeal basis identities must be present.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedResultVersion);
        if (!Enum.IsDefined(baseOutcome)) throw new ArgumentOutOfRangeException(nameof(baseOutcome));
        AppealId = appealId;
        RecheckRequestId = recheckRequestId;
        ResultId = resultId;
        ExpectedResultVersion = expectedResultVersion;
        BaseCheckId = baseCheckId;
        BasePublicationId = basePublicationId;
        SelectedReviewerId = selectedReviewerId;
        BaseOutcome = baseOutcome;
    }
}

public sealed class Check
{
    public Guid CheckId { get; }
    public SolutionReference Solution { get; }
    public CheckKind Kind { get; }
    public long ReviewRevision { get; }
    public CheckState State { get; private set; }
    public long Version { get; private set; } = 1;
    public Assignment? CurrentAssignment { get; private set; }
    public CheckOutcome? Outcome { get; private set; }
    public DecisionOrigin? Origin { get; private set; }
    public Guid? EscalationId { get; private set; }
    public AppealBasis? Appeal { get; }

    private Check(Guid checkId, SolutionReference solution, CheckKind kind, long reviewRevision,
        CheckState state, AppealBasis? appeal)
    {
        if (checkId == Guid.Empty) throw new ArgumentException("Check identity is required.", nameof(checkId));
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reviewRevision);
        CheckId = checkId;
        Solution = solution;
        Kind = kind;
        ReviewRevision = reviewRevision;
        State = state;
        Appeal = appeal;
    }

    public static Check CreateInitial(Guid checkId, SolutionReference solution, long reviewRevision = 1) =>
        new(checkId, solution, CheckKind.Initial, reviewRevision, CheckState.Queued, null);

    // Rehydrate only an unassigned persisted queue entry before applying Assign.
    public static Check RestoreQueued(Guid checkId, SolutionReference solution, CheckKind kind, long reviewRevision,
        long version, Guid? escalationId, AppealBasis? appeal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (!Enum.IsDefined(kind) || (kind == CheckKind.Appeal) != (appeal is not null)
            || escalationId == Guid.Empty || (kind == CheckKind.Appeal && escalationId is null))
            throw new ArgumentException("Invalid persisted queue state.");
        return new Check(checkId, solution, kind, reviewRevision,
            escalationId is null ? CheckState.Queued : CheckState.SeniorQueued, appeal)
        { Version = version, EscalationId = escalationId };
    }

    public static Check CreateAppeal(Guid checkId, SolutionReference solution, long reviewRevision,
        AppealBasis appeal, Check baseCheck, Guid assignmentId, ReviewerScope selectedReviewer, DateTimeOffset assignedAt)
    {
        ArgumentNullException.ThrowIfNull(appeal);
        ArgumentNullException.ThrowIfNull(baseCheck);
        ArgumentNullException.ThrowIfNull(selectedReviewer);
        if (appeal.BaseCheckId != baseCheck.CheckId || baseCheck.State != CheckState.Completed ||
            baseCheck.Origin == DecisionOrigin.SeniorEscalation || baseCheck.Outcome != appeal.BaseOutcome ||
            !Equals(baseCheck.Solution, solution))
            throw new ArgumentException("The appeal basis must match a completed, appealable check for the same solution.", nameof(baseCheck));
        if (baseCheck.CheckId == checkId) throw new ArgumentException("An appeal cannot be based on itself.", nameof(appeal));
        if (selectedReviewer.ReviewerId != appeal.SelectedReviewerId)
            throw new ArgumentException("The assignment must belong to the selected reviewer.", nameof(selectedReviewer));
        if (selectedReviewer.TournamentId != solution.TournamentId)
            throw new ArgumentException("The reviewer scope must include the solution tournament.", nameof(selectedReviewer));
        if (selectedReviewer.Role != ReviewerRole.Reviewer)
            throw new ArgumentException("Appeal selection starts with an ordinary reviewer.", nameof(selectedReviewer));

        var check = new Check(checkId, solution, CheckKind.Appeal, reviewRevision, CheckState.Assigned, appeal);
        check.CurrentAssignment = new Assignment(assignmentId, checkId, selectedReviewer, assignedAt);
        return check;
    }

    public Assignment Assign(Guid assignmentId, ReviewerScope reviewer, DateTimeOffset assignedAt)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        if (State is not (CheckState.Queued or CheckState.SeniorQueued))
            throw new InvalidOperationException("Only a queued check can be assigned.");
        EnsureTournamentScope(reviewer);
        var expectedRole = State == CheckState.SeniorQueued ? ReviewerRole.Senior : ReviewerRole.Reviewer;
        if (reviewer.Role != expectedRole) throw new InvalidOperationException("Reviewer role does not match the queue.");
        var assignment = new Assignment(assignmentId, CheckId, reviewer, assignedAt);
        CurrentAssignment = assignment;
        State = State == CheckState.SeniorQueued ? CheckState.SeniorAssigned : CheckState.Assigned;
        AdvanceVersion();
        return assignment;
    }

    public DecisionOrigin Complete(Guid assignmentId, Guid reviewerId, CheckOutcome outcome, DateTimeOffset completedAt)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        var assignment = RequireCurrentAssignment(assignmentId, reviewerId);
        if (State is not (CheckState.Assigned or CheckState.SeniorAssigned))
            throw new InvalidOperationException("Only an assigned check can be completed.");
        var origin = State == CheckState.SeniorAssigned
            ? DecisionOrigin.SeniorEscalation
            : Kind == CheckKind.Appeal ? DecisionOrigin.AppealReview : DecisionOrigin.OrdinaryReview;
        if (origin == DecisionOrigin.SeniorEscalation && EscalationId is null)
            throw new InvalidOperationException("Senior completion requires an escalation.");
        assignment.Close(AssignmentState.Completed, completedAt);
        Outcome = outcome;
        Origin = origin;
        State = CheckState.Completed;
        AdvanceVersion();
        return origin;
    }

    public Guid RefuseToSenior(Guid assignmentId, Guid reviewerId, Guid escalationId, DateTimeOffset refusedAt)
    {
        if (escalationId == Guid.Empty) throw new ArgumentException("Escalation identity is required.", nameof(escalationId));
        if (State != CheckState.Assigned) throw new InvalidOperationException("Only an ordinary assigned reviewer can refuse to senior.");
        var assignment = RequireCurrentAssignment(assignmentId, reviewerId);
        if (assignment.Scope.Role != ReviewerRole.Reviewer) throw new InvalidOperationException("Senior work cannot be escalated again.");
        assignment.Close(AssignmentState.Refused, refusedAt);
        CurrentAssignment = null;
        EscalationId = escalationId;
        State = CheckState.SeniorQueued;
        AdvanceVersion();
        return escalationId;
    }

    private Assignment RequireCurrentAssignment(Guid assignmentId, Guid reviewerId)
    {
        var assignment = CurrentAssignment;
        if (assignment is null || !assignment.IsActive || assignment.AssignmentId != assignmentId ||
            assignment.Scope.ReviewerId != reviewerId)
            throw new InvalidOperationException("The assignment is no longer owned by this reviewer.");
        return assignment;
    }

    private void EnsureTournamentScope(ReviewerScope reviewer)
    {
        if (reviewer.TournamentId != Solution.TournamentId)
            throw new InvalidOperationException("The reviewer is not scoped to this solution tournament.");
    }

    private void AdvanceVersion() => Version = checked(Version + 1);
}
