using Matigr.Appeals.Domain;
namespace Matigr.Appeals.Tests;
public class SeniorPermutationTests
{
    public static IEnumerable<object[]> Orders()
    {
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders) foreach (var first in new[] { false, true }) foreach (var changed in new[] { false, true }) yield return [order, first, changed];
    }
    [Theory]
    [MemberData(nameof(Orders))]
    public void Escalation_senior_assignment_and_Applied_permutations_preserve_one_process_and_selected(int[] order, bool selectedFirst, bool changed)
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now); var selected = a.Attempt!.ReviewerId;
        var receipt = AppealTests.Receipt(a, changed) with { ReviewerId = Guid.NewGuid(), Origin = DecisionOrigin.SeniorEscalation, EscalationId = Guid.NewGuid() };
        var initial = new ReviewProgress(a.Attempt.RequestId, receipt.EvaluationId, selected, 1, Guid.NewGuid(), selected, null);
        var escalation = initial with { CheckVersion = 2, AssignmentId = null, ReviewerId = null, EscalationId = receipt.EscalationId };
        var senior = escalation with { CheckVersion = 3, AssignmentId = receipt.AssignmentId, ReviewerId = receipt.ReviewerId };
        if (selectedFirst) a = a.Observe(initial, AppealState.RecheckPending);
        foreach (var message in order) a = message switch { 0 => a.Observe(escalation, AppealState.AwaitingSeniorReview),
            1 => a.Observe(senior, AppealState.AwaitingSeniorReview), _ => a.Applied(receipt) };
        if (!selectedFirst) a = a.Observe(initial, AppealState.RecheckPending);
        Assert.Equal(AppealState.Closed, a.State); Assert.Equal(selected, a.Attempt!.ReviewerId); Assert.Equal(selected, a.Receipt!.SelectedReviewerId);
        Assert.Equal(receipt.ReviewerId, a.Receipt.ReviewerId); Assert.Equal(receipt.AssignmentId, a.Receipt.AssignmentId); Assert.Equal(receipt.EscalationId, a.Receipt.EscalationId);
        var version = a.Version; Assert.Equal(version, a.Observe(escalation, AppealState.AwaitingSeniorReview).Applied(receipt).Version);
        Assert.Throws<AppealRuleException>(() => a.Observe(senior with { EscalationId = Guid.NewGuid() }, AppealState.AwaitingSeniorReview));
    }
    [Fact]
    public void Senior_receipt_requires_escalation_and_cannot_replace_known_actual_senior_assignment()
    {
        var a = AppealTests.Registered().Claim(Guid.NewGuid(), Guid.NewGuid(), AppealTests.Now);
        var receipt = AppealTests.Receipt(a) with { Origin = DecisionOrigin.SeniorEscalation, ReviewerId = Guid.NewGuid(), EscalationId = Guid.NewGuid() };
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { EscalationId = null }));
        var progress = new ReviewProgress(a.Attempt!.RequestId, receipt.EvaluationId, a.Attempt.ReviewerId, 3, receipt.AssignmentId, receipt.ReviewerId, receipt.EscalationId);
        a = a.Observe(progress, AppealState.AwaitingSeniorReview);
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { AssignmentId = Guid.NewGuid() }));
        Assert.Throws<AppealRuleException>(() => a.Applied(receipt with { EscalationId = Guid.NewGuid() }));
        Assert.Equal(AppealState.Closed, a.Applied(receipt).State);
    }
}
