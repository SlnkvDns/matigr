namespace Matigr.Results.Domain;

public enum EvaluationKind { Initial, Appeal }
public enum Outcome { Correct, Incorrect }
public enum DecisionOrigin { OrdinaryReview, AppealReview, SeniorEscalation }
public sealed record ResultKey(Guid TournamentId, Guid TeamId, Guid TaskId);
public sealed record AppealEvaluation(Guid AppealId, Guid RecheckRequestId, Guid SelectedReviewerId,
    Guid ResultId, Guid BaseCheckId, Guid BasePublicationId);

// Local command, never an ORM record or a Checking.Domain reference.
public sealed record Evaluation(Guid CheckId, Guid SolutionId, Guid MaterialVersionId, ResultKey Key,
    EvaluationKind Kind, Outcome Outcome, DecisionOrigin Origin, Guid ReviewerId, Guid AssignmentId,
    long ExpectedResultVersion, long ReviewRevision, DateTimeOffset CompletedAt,
    AppealEvaluation? Appeal = null, Guid? EscalationId = null)
{
    public void Validate()
    {
        if (new[] { CheckId, SolutionId, MaterialVersionId, Key.TournamentId, Key.TeamId, Key.TaskId, ReviewerId, AssignmentId }.Contains(Guid.Empty)
            || !Enum.IsDefined(Kind) || !Enum.IsDefined(Outcome) || !Enum.IsDefined(Origin) || ReviewRevision < 1
            || (Kind == EvaluationKind.Appeal) != (Appeal is not null)
            || (Kind == EvaluationKind.Initial ? ExpectedResultVersion != 0 || Origin == DecisionOrigin.AppealReview
                : ExpectedResultVersion < 1 || Origin == DecisionOrigin.OrdinaryReview)
            || (Origin == DecisionOrigin.SeniorEscalation) != (EscalationId is not null) || EscalationId == Guid.Empty)
            throw new ArgumentException("Invalid evaluation lineage.");
        if (Appeal is { } a && (new[] { a.AppealId, a.RecheckRequestId, a.SelectedReviewerId, a.ResultId, a.BaseCheckId, a.BasePublicationId }.Contains(Guid.Empty)
            || (Origin == DecisionOrigin.AppealReview && a.SelectedReviewerId != ReviewerId)))
            throw new ArgumentException("Invalid appeal lineage.");
    }
}

public sealed record ScoringRule
{
    public long Version { get; }
    public decimal Points { get; }
    public ScoringRule(long version, decimal points)
    {
        if (version < 1 || points < 0 || points != decimal.Truncate(points) || points >= 1e28m)
            throw new ArgumentOutOfRangeException(nameof(points));
        Version = version; Points = points;
    }
    public decimal Score(Outcome outcome) => outcome switch
    { Outcome.Correct => Points, Outcome.Incorrect => 0, _ => throw new ArgumentOutOfRangeException(nameof(outcome)) };
}

public sealed record ResultPublication(Guid PublicationId, long ResultVersion, Guid ResultId,
    Evaluation Source, decimal Score, ScoringRule Rule, DateTimeOffset PublishedAt);
public sealed record EvaluationReceipt(Guid EvaluationId, bool Applied, bool Changed,
    ResultPublication? Publication, string? ReasonCode);

public sealed class TeamTaskResult
{
    public ResultPublication Publication { get; private set; }
    public ResultKey Key => Publication.Source.Key;
    public TeamTaskResult(ResultPublication publication)
    {
        publication.Source.Validate();
        if (publication.PublicationId == Guid.Empty || publication.ResultId == Guid.Empty || publication.ResultVersion < 1
            || publication.Score != publication.Rule.Score(publication.Source.Outcome)) throw new ArgumentException("Invalid publication.");
        Publication = publication;
    }

    public static TeamTaskResult Initial(Guid resultId, Guid publicationId, Evaluation evaluation, ScoringRule rule, DateTimeOffset publishedAt)
    {
        evaluation.Validate();
        if (evaluation.Kind != EvaluationKind.Initial) throw new InvalidOperationException("An initial result needs an Initial evaluation.");
        return new(new(publicationId, 1, resultId, evaluation, rule.Score(evaluation.Outcome), rule, publishedAt.ToUniversalTime()));
    }

    public EvaluationReceipt Apply(Evaluation evaluation, Guid publicationId, DateTimeOffset publishedAt)
    {
        evaluation.Validate();
        var p = Publication;
        var a = evaluation.Appeal;
        if (evaluation.Key != Key || evaluation.SolutionId != p.Source.SolutionId || evaluation.MaterialVersionId != p.Source.MaterialVersionId)
            return new(evaluation.CheckId, false, false, null, "SolutionConflict");
        if (a is null || evaluation.ExpectedResultVersion != p.ResultVersion || a.ResultId != p.ResultId
            || a.BaseCheckId != p.Source.CheckId || a.BasePublicationId != p.PublicationId)
            return new(evaluation.CheckId, false, false, null, "ResultVersionConflict");
        if (evaluation.Outcome == p.Source.Outcome)
            return new(evaluation.CheckId, true, false, p, null);
        if (publicationId == Guid.Empty) throw new ArgumentException("Publication ID required.");
        Publication = new(publicationId, checked(p.ResultVersion + 1), p.ResultId, evaluation,
            p.Rule.Score(evaluation.Outcome), p.Rule, publishedAt.ToUniversalTime());
        return new(evaluation.CheckId, true, true, Publication, null);
    }
}
