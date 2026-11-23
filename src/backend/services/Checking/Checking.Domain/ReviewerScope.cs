namespace Matigr.Checking.Domain;

public enum ReviewerRole
{
    Reviewer,
    Senior
}

// A server-verified permission boundary carried into an assignment decision.
public sealed record ReviewerScope
{
    public Guid ReviewerId { get; }
    public Guid TournamentId { get; }
    public ReviewerRole Role { get; }

    public ReviewerScope(Guid reviewerId, Guid tournamentId, ReviewerRole role)
    {
        if (reviewerId == Guid.Empty) throw new ArgumentException("Reviewer identity is required.", nameof(reviewerId));
        if (tournamentId == Guid.Empty) throw new ArgumentException("Tournament scope is required.", nameof(tournamentId));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ReviewerId = reviewerId;
        TournamentId = tournamentId;
        Role = role;
    }
}
