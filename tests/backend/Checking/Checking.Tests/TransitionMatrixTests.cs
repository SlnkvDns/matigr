using Matigr.Checking.Domain;

namespace Matigr.Checking.Tests;

public sealed partial class CheckTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var state in Enum.GetValues<CheckState>())
        foreach (var command in new[] { "assign", "senior", "correct", "incorrect", "refuse" })
            yield return [state, command];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Allowed_and_forbidden_transition_matrix(CheckState state, string command)
    {
        var (s, r, senior) = Fixture();
        var c = Check.CreateInitial(Guid.NewGuid(), s);
        Assignment? a = null;
        if (state != CheckState.Queued) a = c.Assign(Guid.NewGuid(), r, At(1));
        if (state is CheckState.SeniorQueued or CheckState.SeniorAssigned)
            c.RefuseToSenior(a!.AssignmentId, r.ReviewerId, Guid.NewGuid(), At(2));
        if (state == CheckState.SeniorAssigned) a = c.Assign(Guid.NewGuid(), senior, At(3));
        if (state == CheckState.Completed) c.Complete(a!.AssignmentId, r.ReviewerId, CheckOutcome.Correct, At(2));
        var version = c.Version;
        var expected = (state, command) switch
        {
            (CheckState.Queued, "assign") or (CheckState.SeniorQueued, "senior") => true,
            (CheckState.Assigned, "correct" or "incorrect" or "refuse") => true,
            (CheckState.SeniorAssigned, "correct" or "incorrect") => true,
            _ => false
        };
        void Act()
        {
            if (command is "assign" or "senior") c.Assign(Guid.NewGuid(), command == "senior" ? senior : r, At(4));
            else if (command == "refuse") c.RefuseToSenior(a?.AssignmentId ?? Guid.NewGuid(), r.ReviewerId, Guid.NewGuid(), At(4));
            else c.Complete(a?.AssignmentId ?? Guid.NewGuid(), a?.Scope.ReviewerId ?? r.ReviewerId,
                command == "correct" ? CheckOutcome.Correct : CheckOutcome.Incorrect, At(4));
        }
        if (expected) { Act(); Assert.Equal(version + 1, c.Version); }
        else { Assert.Throws<InvalidOperationException>(Act); Assert.Equal(state, c.State); Assert.Equal(version, c.Version); }
        Assert.Same(s, c.Solution);
    }

    [Fact]
    public void Foreign_owner_assignment_invalid_outcome_time_and_second_work_are_rejected_without_changes()
    {
        var (s, r, _) = Fixture(); var c = Check.CreateInitial(Guid.NewGuid(), s);
        var a = c.Assign(Guid.NewGuid(), r, At(1));
        Assert.Throws<InvalidOperationException>(() => c.Assign(Guid.NewGuid(), r, At(2)));
        Assert.Throws<InvalidOperationException>(() => c.Complete(a.AssignmentId, Guid.NewGuid(), CheckOutcome.Correct, At(2)));
        Assert.Throws<InvalidOperationException>(() => c.Complete(Guid.NewGuid(), r.ReviewerId, CheckOutcome.Correct, At(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Complete(a.AssignmentId, r.ReviewerId, (CheckOutcome)55, At(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Complete(a.AssignmentId, r.ReviewerId, CheckOutcome.Correct, At(0)));
        Assert.Equal(2, c.Version); Assert.True(a.IsActive); Assert.Null(c.Outcome); Assert.Null(c.Origin);
    }

    [Fact]
    public void Original_reviewer_can_finish_appeal_but_final_senior_cannot_be_appealed_or_refuse_again()
    {
        var (s, r, senior) = Fixture(); var b = Check.CreateInitial(Guid.NewGuid(), s);
        var a = b.Assign(Guid.NewGuid(), r, At(1)); b.Complete(a.AssignmentId, r.ReviewerId, CheckOutcome.Incorrect, At(2));
        var basis = new AppealBasis(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, b.CheckId, Guid.NewGuid(), r.ReviewerId, CheckOutcome.Incorrect);
        var appeal = Check.CreateAppeal(Guid.NewGuid(), s, 2, basis, b, Guid.NewGuid(), r, At(3));
        appeal.Complete(appeal.CurrentAssignment!.AssignmentId, r.ReviewerId, CheckOutcome.Correct, At(4));
        Assert.Equal(DecisionOrigin.AppealReview, appeal.Origin);
        var escalated = Check.CreateAppeal(Guid.NewGuid(), s, 3, basis, b, Guid.NewGuid(), r, At(3));
        escalated.RefuseToSenior(escalated.CurrentAssignment!.AssignmentId, r.ReviewerId, Guid.NewGuid(), At(4));
        var sa = escalated.Assign(Guid.NewGuid(), senior, At(5));
        Assert.Throws<InvalidOperationException>(() => escalated.RefuseToSenior(sa.AssignmentId, senior.ReviewerId, Guid.NewGuid(), At(6)));
        escalated.Complete(sa.AssignmentId, senior.ReviewerId, CheckOutcome.Correct, At(6));
        Assert.Same(basis, escalated.Appeal); Assert.Equal(DecisionOrigin.SeniorEscalation, escalated.Origin);
        Assert.Throws<ArgumentException>(() => Check.CreateAppeal(Guid.NewGuid(), s, 4, basis, escalated, Guid.NewGuid(), r, At(7)));
    }
}
