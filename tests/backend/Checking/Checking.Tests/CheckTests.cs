using Matigr.Checking.Domain;

namespace Matigr.Checking.Tests;

public sealed partial class CheckTests
{
    [Fact]
    public void Restored_senior_queue_assigns_same_appeal_at_next_version_and_keeps_origin_lineage()
    {
        var (solution, reviewer, senior) = Fixture();
        var basis = new AppealBasis(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            Guid.NewGuid(), reviewer.ReviewerId, CheckOutcome.Incorrect);
        var escalation = Guid.NewGuid();
        var check = Check.RestoreQueued(Guid.NewGuid(), solution, CheckKind.Appeal, 2, 3, escalation, basis);
        var assigned = check.Assign(Guid.NewGuid(), senior, At(3));
        Assert.Equal(4, check.Version); Assert.Equal(CheckState.SeniorAssigned, check.State);
        check.Complete(assigned.AssignmentId, senior.ReviewerId, CheckOutcome.Correct, At(4));
        Assert.Equal(basis, check.Appeal); Assert.Equal(escalation, check.EscalationId);
        Assert.Equal(DecisionOrigin.SeniorEscalation, check.Origin);
        Assert.Throws<ArgumentException>(() => Check.RestoreQueued(Guid.NewGuid(), solution, CheckKind.Initial, 1, 1, null, basis));
        Assert.Throws<ArgumentException>(() => Check.RestoreQueued(Guid.NewGuid(), solution, CheckKind.Appeal, 2, 1, null, basis));
    }
    [Fact]
    public void Initial_check_records_binary_outcome_and_closes_its_assignment()
    {
        var (solution, reviewer, _) = Fixture();
        var check = Check.CreateInitial(Guid.NewGuid(), solution);
        var assignment = check.Assign(Guid.NewGuid(), reviewer, At(1));

        var origin = check.Complete(assignment.AssignmentId, reviewer.ReviewerId, CheckOutcome.Correct, At(2));

        Assert.Equal(CheckState.Completed, check.State);
        Assert.Equal(CheckOutcome.Correct, check.Outcome);
        Assert.Equal(DecisionOrigin.OrdinaryReview, origin);
        Assert.Equal(DecisionOrigin.OrdinaryReview, check.Origin);
        Assert.Equal(AssignmentState.Completed, assignment.State);
        Assert.Equal(3, check.Version);
    }

    [Fact]
    public void Refusal_releases_the_reviewer_and_keeps_the_same_check_for_senior()
    {
        var (solution, reviewer, senior) = Fixture();
        var check = Check.CreateInitial(Guid.NewGuid(), solution);
        var originalAssignment = check.Assign(Guid.NewGuid(), reviewer, At(1));
        var escalationId = Guid.NewGuid();

        check.RefuseToSenior(originalAssignment.AssignmentId, reviewer.ReviewerId, escalationId, At(2));
        var seniorAssignment = check.Assign(Guid.NewGuid(), senior, At(3));
        var origin = check.Complete(seniorAssignment.AssignmentId, senior.ReviewerId, CheckOutcome.Incorrect, At(4));

        Assert.Equal(CheckState.Completed, check.State);
        Assert.Equal(escalationId, check.EscalationId);
        Assert.Equal(AssignmentState.Refused, originalAssignment.State);
        Assert.Equal(DecisionOrigin.SeniorEscalation, origin);
        Assert.Equal(DecisionOrigin.SeniorEscalation, check.Origin);
        Assert.Equal(CheckOutcome.Incorrect, check.Outcome);
        Assert.Throws<InvalidOperationException>(() => check.Complete(
            originalAssignment.AssignmentId, reviewer.ReviewerId, CheckOutcome.Correct, At(5)));
    }

    [Fact]
    public void Appeal_starts_assigned_to_selected_reviewer_on_same_material()
    {
        var (solution, reviewer, _) = Fixture();
        var baseCheck = Check.CreateInitial(Guid.NewGuid(), solution);
        var baseAssignment = baseCheck.Assign(Guid.NewGuid(), reviewer, At(1));
        baseCheck.Complete(baseAssignment.AssignmentId, reviewer.ReviewerId, CheckOutcome.Incorrect, At(2));
        var basis = new AppealBasis(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            baseCheck.CheckId, Guid.NewGuid(), reviewer.ReviewerId, CheckOutcome.Incorrect);

        var appeal = Check.CreateAppeal(Guid.NewGuid(), solution, 2, basis, baseCheck,
            Guid.NewGuid(), reviewer, At(3));

        Assert.Equal(CheckKind.Appeal, appeal.Kind);
        Assert.Equal(CheckState.Assigned, appeal.State);
        Assert.Equal(reviewer.ReviewerId, appeal.CurrentAssignment!.Scope.ReviewerId);
        Assert.Equal(baseCheck.CheckId, appeal.Appeal!.BaseCheckId);
    }

    [Fact]
    public void Appeal_cannot_use_senior_verdict_and_wrong_tournament_cannot_claim_initial_check()
    {
        var (solution, reviewer, senior) = Fixture();
        var baseCheck = Check.CreateInitial(Guid.NewGuid(), solution);
        var initialAssignment = baseCheck.Assign(Guid.NewGuid(), reviewer, At(1));
        baseCheck.RefuseToSenior(initialAssignment.AssignmentId, reviewer.ReviewerId, Guid.NewGuid(), At(2));
        var seniorAssignment = baseCheck.Assign(Guid.NewGuid(), senior, At(3));
        baseCheck.Complete(seniorAssignment.AssignmentId, senior.ReviewerId, CheckOutcome.Correct, At(4));
        var basis = new AppealBasis(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            baseCheck.CheckId, Guid.NewGuid(), reviewer.ReviewerId, CheckOutcome.Correct);

        Assert.Throws<ArgumentException>(() => Check.CreateAppeal(Guid.NewGuid(), solution, 2, basis,
            baseCheck, Guid.NewGuid(), reviewer, At(5)));
        Assert.Throws<InvalidOperationException>(() => Check.CreateInitial(Guid.NewGuid(), solution)
            .Assign(Guid.NewGuid(), new ReviewerScope(Guid.NewGuid(), Guid.NewGuid(), ReviewerRole.Reviewer), At(1)));
    }

    private static (SolutionReference Solution, ReviewerScope Reviewer, ReviewerScope Senior) Fixture()
    {
        var tournamentId = Guid.NewGuid();
        return (
            new SolutionReference(Guid.NewGuid(), tournamentId, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), "prepared/solution-1", At(0), 1),
            new ReviewerScope(Guid.NewGuid(), tournamentId, ReviewerRole.Reviewer),
            new ReviewerScope(Guid.NewGuid(), tournamentId, ReviewerRole.Senior));
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 23, hour, 0, 0, TimeSpan.Zero);
}
