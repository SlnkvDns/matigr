using Matigr.Results.Domain;

namespace Matigr.Results.Tests;

public class ScoringPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    public void Changed_outcome_replaces_score_and_noop_preserves_publication_source(int cost)
    {
        var result = TeamTaskResult.Initial(Guid.NewGuid(), Guid.NewGuid(), ResultTests.Initial(), new(1, cost), DateTimeOffset.UtcNow);
        var original = result.Publication;
        var confirm = result.Apply(ResultTests.Appeal(original, Outcome.Correct), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(confirm.Applied); Assert.False(confirm.Changed); Assert.Same(original, confirm.Publication);
        Assert.Same(original, result.Publication);
        var evaluation = ResultTests.Appeal(original, Outcome.Incorrect);
        var changed = result.Apply(evaluation, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(changed.Applied); Assert.True(changed.Changed);
        Assert.Equal(0, result.Publication.Score); Assert.Equal(2, result.Publication.ResultVersion);
        Assert.Same(original.Rule, result.Publication.Rule);
        Assert.Equal(evaluation, result.Publication.Source);
        var old = result.Apply(ResultTests.Appeal(original, Outcome.Correct), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.False(old.Applied); Assert.Equal("ResultVersionConflict", old.ReasonCode);
        Assert.Equal(0, result.Publication.Score);
    }
    [Theory]
    [InlineData("solution")]
    [InlineData("material")]
    [InlineData("base")]
    [InlineData("publication")]
    [InlineData("result")]
    public void Wrong_basis_never_overwrites_current_publication(string field)
    {
        var result = TeamTaskResult.Initial(Guid.NewGuid(), Guid.NewGuid(), ResultTests.Initial(), new(1, 20), DateTimeOffset.UtcNow);
        var p = result.Publication; var e = ResultTests.Appeal(p, Outcome.Incorrect);
        e = field switch {
            "solution" => e with { SolutionId = Guid.NewGuid() }, "material" => e with { MaterialVersionId = Guid.NewGuid() },
            "base" => e with { Appeal = e.Appeal! with { BaseCheckId = Guid.NewGuid() } },
            "publication" => e with { Appeal = e.Appeal! with { BasePublicationId = Guid.NewGuid() } },
            _ => e with { Appeal = e.Appeal! with { ResultId = Guid.NewGuid() } }
        };
        Assert.False(result.Apply(e, Guid.NewGuid(), DateTimeOffset.UtcNow).Applied);
        Assert.Same(p, result.Publication);
    }
    [Fact]
    public void Exact_large_scores_and_invalid_rule_values()
    {
        var rule = new ScoringRule(1, 9007199254740993m);
        Assert.Equal(9007199254740993m, rule.Score(Outcome.Correct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScoringRule(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScoringRule(1, 1.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScoringRule(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.Score((Outcome)44));
    }
}
