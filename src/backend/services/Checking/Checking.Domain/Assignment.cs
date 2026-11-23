namespace Matigr.Checking.Domain;

public enum AssignmentState
{
    Active,
    Completed,
    Refused
}

public sealed class Assignment
{
    public Guid AssignmentId { get; }
    public Guid CheckId { get; }
    public ReviewerScope Scope { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public AssignmentState State { get; private set; } = AssignmentState.Active;
    public bool IsActive => State == AssignmentState.Active;

    internal Assignment(Guid assignmentId, Guid checkId, ReviewerScope scope, DateTimeOffset startedAt)
    {
        if (assignmentId == Guid.Empty) throw new ArgumentException("Assignment identity is required.", nameof(assignmentId));
        if (checkId == Guid.Empty) throw new ArgumentException("Check identity is required.", nameof(checkId));
        ArgumentNullException.ThrowIfNull(scope);
        AssignmentId = assignmentId;
        CheckId = checkId;
        Scope = scope;
        StartedAt = startedAt.ToUniversalTime();
    }

    internal void Close(AssignmentState terminalState, DateTimeOffset endedAt)
    {
        if (!IsActive) throw new InvalidOperationException("Only an active assignment can be closed.");
        if (terminalState is not (AssignmentState.Completed or AssignmentState.Refused))
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        var utcEndedAt = endedAt.ToUniversalTime();
        if (utcEndedAt < StartedAt) throw new ArgumentOutOfRangeException(nameof(endedAt));
        State = terminalState;
        EndedAt = utcEndedAt;
    }
}
