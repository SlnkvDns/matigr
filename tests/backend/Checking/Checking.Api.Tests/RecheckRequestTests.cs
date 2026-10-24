using Matigr.Checking.Application;
using Matigr.Checking.Domain;

namespace Matigr.Checking.Api.Tests;

public class RecheckRequestTests
{
    private static RecheckRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 9007199254740993, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        CheckOutcome.Correct, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    [Fact]
    public void Technical_retry_hash_is_stable_without_message_or_correlation_metadata()
    {
        var request = Request(); Assert.Equal(request.SemanticHash(), (request with { }).SemanticHash());
        Assert.Equal(64, request.SemanticHash().Length);
    }
    [Theory]
    [InlineData("request")]
    [InlineData("reviewer")]
    [InlineData("material")]
    [InlineData("base")]
    [InlineData("publication")]
    [InlineData("version")]
    [InlineData("outcome")]
    [InlineData("time")]
    public void Conflicting_semantics_cannot_reuse_receipt_hash(string change)
    {
        var request = Request();
        var altered = change switch {
            "request" => request with { RequestId = Guid.NewGuid() }, "reviewer" => request with { RequestedReviewerId = Guid.NewGuid() },
            "material" => request with { MaterialVersionId = Guid.NewGuid() }, "base" => request with { BaseCheckId = Guid.NewGuid() },
            "publication" => request with { PublicationId = Guid.NewGuid() }, "version" => request with { ExpectedResultVersion = request.ExpectedResultVersion + 1 },
            "outcome" => request with { BaseOutcome = CheckOutcome.Incorrect }, _ => request with { AcceptedAt = request.AcceptedAt.AddSeconds(1) }
        };
        Assert.NotEqual(request.SemanticHash(), altered.SemanticHash());
    }
    [Fact]
    public void Invalid_identity_version_outcome_and_selection_before_submission_are_not_requests()
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => (request with { RequestId = Guid.Empty }).SemanticHash());
        Assert.Throws<ArgumentException>(() => (request with { ExpectedResultVersion = 0 }).SemanticHash());
        Assert.Throws<ArgumentException>(() => (request with { BaseOutcome = (CheckOutcome)999 }).SemanticHash());
        Assert.Throws<ArgumentException>(() => (request with { AcceptedAt = request.SubmittedAt.AddSeconds(-1) }).SemanticHash());
    }
}
