using Matigr.Appeals.Domain;

namespace Matigr.Appeals.Tests;

public class ReceiptPermutationTests
{
    public static IEnumerable<object[]> Orders()
    {
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders) foreach (var changed in new[] { false, true }) yield return [order, changed];
    }
    [Theory]
    [MemberData(nameof(Orders))]
    public void Assigned_Completed_Applied_orders_always_close_once_without_replacing_basis(int[] order, bool changed)
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now); var receipt = AppealTests.Receipt(a, changed);
        var assigned = new ReviewProgress(a.Attempt!.RequestId, receipt.EvaluationId, a.Attempt.ReviewerId, 1, receipt.AssignmentId, receipt.ReviewerId, null);
        var completed = assigned with { CheckVersion = 2 }; var basis = a.Basis;
        foreach (var message in order) a = message switch { 0 => a.Observe(assigned, AppealState.RecheckPending),
            1 => a.Observe(completed, AppealState.AwaitingResultApplication), _ => a.Applied(receipt) };
        Assert.Equal(AppealState.Closed, a.State); Assert.Equal(receipt, a.Receipt); Assert.Equal(basis, a.Basis);
        var version = a.Version;
        a = a.Observe(assigned, AppealState.RecheckPending).Observe(completed, AppealState.AwaitingResultApplication).Applied(receipt);
        Assert.Equal(version, a.Version); Assert.Equal(receipt.PublicationId, a.Receipt!.PublicationId);
    }
    [Fact]
    public void A_known_assignment_cannot_be_replaced_by_an_unrelated_final_assignment()
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now); var receipt = AppealTests.Receipt(a);
        var progress = new ReviewProgress(a.Attempt!.RequestId, receipt.EvaluationId, a.Attempt.ReviewerId, 1, receipt.AssignmentId, receipt.ReviewerId, null);
        a = a.Observe(progress, AppealState.RecheckPending);
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { AssignmentId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { EvaluationId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { Basis = a.Basis with { ResultId = Guid.NewGuid() } }));
        Assert.Equal(AppealState.Closed, a.Applied(receipt).State);
    }
    [Fact]
    public void Old_attempt_assignment_and_busy_do_not_cancel_a_new_manual_choice()
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now); var old = a.Attempt!;
        a = a.RequestRejected(old.RequestId, "ReviewerBusy").Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now);
        var late = new ReviewProgress(old.RequestId, Guid.NewGuid(), old.ReviewerId, 1, Guid.NewGuid(), old.ReviewerId, null);
        Assert.Equal(a, a.RequestRejected(old.RequestId, "ReviewerBusy")); Assert.Equal(a, a.Observe(late, AppealState.RecheckPending));
        Assert.Equal(AppealState.AcceptedAwaitingRecheck, a.State);
    }
}
