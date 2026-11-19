using System.Collections.Frozen;

namespace Matigr.Checking.Application.Identity;

// Internal identity, independent of the dev selector, JWT and any future IdP.
public sealed class AuthenticatedSubject(
    string subjectId, string displayName, IEnumerable<string> roles,
    IEnumerable<string> tournamentIds, string? teamId)
{
    public string SubjectId { get; } = subjectId;
    public string DisplayName { get; } = displayName;
    public IReadOnlySet<string> Roles { get; } = roles.ToFrozenSet(StringComparer.Ordinal);
    public IReadOnlySet<string> TournamentIds { get; } = tournamentIds.ToFrozenSet(StringComparer.Ordinal);
    public string? TeamId { get; } = teamId;
    public bool IsReviewer => Roles.Contains("Jury") || Roles.Contains("Senior");

    public bool CanReview(string tournamentId, bool seniorQueue = false) =>
        IsReviewer && TournamentIds.Contains(tournamentId) && (!seniorQueue || Roles.Contains("Senior"));
}
