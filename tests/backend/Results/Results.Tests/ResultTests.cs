using Matigr.Results.Domain;

namespace Matigr.Results.Tests;

public class ResultTests
{
    public static Evaluation Initial(Outcome outcome = Outcome.Correct) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), EvaluationKind.Initial, outcome, DecisionOrigin.OrdinaryReview,
        Guid.NewGuid(), Guid.NewGuid(), 0, 1, DateTimeOffset.UtcNow);
    public static Evaluation Appeal(ResultPublication p, Outcome outcome) => p.Source with { CheckId = Guid.NewGuid(), Kind = EvaluationKind.Appeal,
        Outcome = outcome, Origin = DecisionOrigin.AppealReview, ExpectedResultVersion = p.ResultVersion, ReviewRevision = 2,
        Appeal = new(Guid.NewGuid(), Guid.NewGuid(), p.Source.ReviewerId, p.ResultId, p.Source.CheckId, p.PublicationId) };

    [Theory]
    [InlineData(Outcome.Correct, 10)]
    [InlineData(Outcome.Incorrect, 0)]
    public void Initial_binary_verdict_publishes_fixed_points(Outcome outcome, int points)
    {
        var result = TeamTaskResult.Initial(Guid.NewGuid(), Guid.NewGuid(), Initial(outcome), new(1, 10), DateTimeOffset.UtcNow);
        Assert.Equal(points, result.Publication.Score); Assert.Equal(1, result.Publication.ResultVersion);
    }
    [Fact]
    public void No_result_is_not_a_zero_point_publication()
    {
        TeamTaskResult? pending = null;
        Assert.Null(pending);
        var published = TeamTaskResult.Initial(Guid.NewGuid(), Guid.NewGuid(), Initial(Outcome.Incorrect), new(1, 10), DateTimeOffset.UtcNow);
        Assert.Equal(Outcome.Incorrect, published.Publication.Source.Outcome);
        Assert.Equal(0, published.Publication.Score);
    }
}
