using Matigr.Appeals.Domain;

namespace Matigr.Appeals.Tests;

public class AdmissionMatrixTests
{
    [Theory]
    [InlineData("team", "Forbidden")]
    [InlineData("tournament", "Forbidden")]
    [InlineData("closed", "WindowClosed")]
    [InlineData("quota", "AlreadyAppealed")]
    [InlineData("senior", "BaseNotAppealable")]
    public void Admission_rejects_only_confirmed_business_restrictions(string change, string code)
    {
        var b = AppealTests.Basis(); var team = change == "team" ? Guid.NewGuid() : b.TeamId;
        var window = new SubmissionWindow(change == "tournament" ? Guid.NewGuid() : b.TournamentId, change != "closed");
        if (change == "senior") b = b with { Origin = DecisionOrigin.SeniorEscalation };
        Assert.Equal(code, Assert.Throws<AppealRuleException>(() => Appeal.Register(Guid.NewGuid(), b, team, window, AppealTests.Now, change == "quota")).Message);
    }
    [Theory]
    [InlineData(AppealState.Registered)]
    [InlineData(AppealState.Closed)]
    [InlineData(AppealState.NeedsResolution)]
    public void Quota_does_not_depend_on_status_or_new_result_version(AppealState state)
    {
        var a = AppealTests.Registered() with { State = state };
        var next = a.Basis with { ResultVersion = 2, PublicationId = Guid.NewGuid() };
        Assert.Equal("AlreadyAppealed", Assert.Throws<AppealRuleException>(() => Appeal.Register(Guid.NewGuid(), next, next.TeamId,
            new(next.TournamentId), AppealTests.Now, true)).Message);
    }
    [Fact]
    public void Busy_requires_confirmation_new_selection_preserves_basis_and_stale_busy_cannot_remove_current_attempt()
    {
        var a = AppealTests.Registered(); var request = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var waiting = a.Claim(request, reviewer, AppealTests.Now);
        Assert.Throws<AppealRuleException>(() => waiting.Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now));
        var busy = waiting.RequestRejected(request, "ReviewerBusy"); Assert.Equal(AppealState.Registered, busy.State);
        Assert.Equal(AttemptState.RejectedBusy, busy.Attempt!.State);
        var retry = busy.Claim(Guid.NewGuid(), reviewer, AppealTests.Now.AddDays(1));
        Assert.Equal(a.AppealId, retry.AppealId); Assert.Equal(a.Basis, retry.Basis); Assert.Equal(a.SubmittedAt, retry.SubmittedAt);
        Assert.Equal(retry, retry.RequestRejected(request, "ReviewerBusy"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Noop_and_changed_require_correlated_published_receipt(bool changed)
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now);
        var receipt = AppealTests.Receipt(a, changed);
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { RequestId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { SelectedReviewerId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { ReviewerId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { Basis = a.Basis with { MaterialVersionId = Guid.NewGuid() } }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { ResultVersion = 99 }));
        var closed = a.Applied(receipt); Assert.Equal(AppealState.Closed, closed.State);
        Assert.Equal(closed, closed.RequestRejected(a.Attempt!.RequestId, "ReviewerBusy"));
        Assert.Throws<AppealRuleException>(() => closed.Applied(receipt with { AssignmentId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => closed.Observe(new(a.Attempt.RequestId, Guid.NewGuid(), a.Attempt.ReviewerId, 3,
            receipt.AssignmentId, receipt.ReviewerId, null), AppealState.RecheckPending));
    }
    [Fact]
    public void Permanent_conflict_is_observable_without_rebasing_or_another_quota()
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now);
        var conflict = a.RequestRejected(a.Attempt!.RequestId, "BaseNotAppealable");
        Assert.Equal(AppealState.NeedsResolution, conflict.State); Assert.Equal(a.Basis, conflict.Basis);
        Assert.Equal("BaseNotAppealable", conflict.Conflict);
        Assert.Throws<AppealRuleException>(() => conflict.Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now));
    }
}
