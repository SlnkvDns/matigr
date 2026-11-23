using Matigr.Appeals.Domain;

namespace Matigr.Appeals.Tests;

public class AppealTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static PublishedBasis Basis() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), DecisionOrigin.OrdinaryReview, Outcome.Correct, "1", Guid.NewGuid(), Now.AddMinutes(-1));
    internal static Appeal Registered()
    {
        var b = Basis(); return Appeal.Register(Guid.NewGuid(), b, b.TeamId, new(b.TournamentId), Now, false);
    }
    internal static PublishedReceipt Receipt(Appeal a, bool changed = false) => new(Guid.NewGuid(), a.Attempt!.RequestId, a.Attempt.ReviewerId,
        a.Attempt.ReviewerId, Guid.NewGuid(), DecisionOrigin.AppealReview, null, a.Basis, changed ? 2 : 1,
        changed ? Guid.NewGuid() : a.Basis.PublicationId, changed ? Outcome.Incorrect : a.Basis.Outcome, changed);
    [Fact]
    public void Registration_is_manual_and_basis_is_immutable_with_permanent_quota()
    {
        var a = Registered(); Assert.Equal(AppealState.Registered, a.State); Assert.Null(a.Attempt); Assert.Null(a.Progress);
        var claimed = a.Claim(Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Equal(a.Basis, claimed.Basis); Assert.Equal(AppealState.AcceptedAwaitingRecheck, claimed.State);
        Assert.Throws<AppealRuleException>(() => Appeal.Register(Guid.NewGuid(), a.Basis, a.Basis.TeamId, new(a.Basis.TournamentId), Now, true));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_fact_never_closes_without_published_receipt_and_early_receipt_is_sufficient(bool changed)
    {
        var a = Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), Now); var receipt = Receipt(a, changed);
        var fact = new ReviewProgress(a.Attempt!.RequestId, receipt.EvaluationId, a.Attempt.ReviewerId, 3, receipt.AssignmentId, receipt.ReviewerId, null);
        var waiting = a.Observe(fact, AppealState.AwaitingResultApplication);
        Assert.Null(waiting.Receipt); Assert.Equal(AppealState.AwaitingResultApplication, waiting.State);
        var closed = a.Applied(receipt); Assert.Equal(AppealState.Closed, closed.State); Assert.Equal(a.Basis, closed.Basis);
        Assert.Equal(closed, closed.Applied(receipt)); Assert.Equal(closed, closed.Observe(fact, AppealState.RecheckPending));
        Assert.Equal(AppealState.Closed, waiting.Applied(receipt).State);
    }
    [Fact]
    public void Only_new_submission_checks_window_manual_end_does_not_block_registered_claim()
    {
        var a = Registered(); var window = new SubmissionWindow(a.Basis.TournamentId).Close(Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.False(window.Open); Assert.Equal(window, window.Close(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(1)));
        Assert.Throws<AppealRuleException>(() => Appeal.Register(Guid.NewGuid(), a.Basis, a.Basis.TeamId, window, Now, false));
        Assert.Equal(AppealState.AcceptedAwaitingRecheck, a.Claim(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(1)).State);
    }
}
