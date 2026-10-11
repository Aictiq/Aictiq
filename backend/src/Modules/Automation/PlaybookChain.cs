using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Playbook chaining: a builder playbook's ordered follow-up playbooks (<see cref="PlaybookStage"/>)
/// check its pull request before a person does. Decided here, in the transaction that finishes
/// each run, is what comes next:
/// <list type="bullet">
/// <item>a builder (implement) run that succeeded on a pull request branch hands over to stage 1;</item>
/// <item>a stage that passed hands over to the next stage, and the last one ends the chain;</item>
/// <item>a stage that requested changes hands over to a rework run of the builder with its
/// findings - the chain then restarts from stage 1 - until the item has had
/// <see cref="MaxReworkRounds"/> rework runs, when the item goes to human review with them open;</item>
/// <item>a stage that failed, timed out, was cancelled or gave no verdict ends the chain at
/// human review: a chain never blocks its ticket.</item>
/// </list>
/// A hand-over is a <see cref="ChainStep"/> that <see cref="ChainQueue"/> starts once the item is
/// free, and an ended chain sends the item to the builder playbook's success state.
/// </summary>
internal static class PlaybookChain
{
    public const int MaxReworkRounds = 3;

    public const string Pass = "pass";

    public const string ChangesRequested = "changes_requested";

    public static string? VerdictName(StageVerdict? verdict) => verdict switch
    {
        StageVerdict.Pass => Pass,
        StageVerdict.ChangesRequested => ChangesRequested,
        _ => null,
    };

    /// <summary>A playbook's follow-up playbooks, in order, with their names.</summary>
    public static async Task<IReadOnlyList<(Guid Id, string Name)>> StagesAsync(
        AutomationDbContext db, Guid playbookId, CancellationToken ct)
    {
        var rows = await (
                from stage in db.PlaybookStages.AsNoTracking()
                join playbook in db.Playbooks.AsNoTracking() on stage.StagePlaybookId equals playbook.Id
                where stage.PlaybookId == playbookId
                orderby stage.Position
                select new { playbook.Id, playbook.Name })
            .ToListAsync(ct);
        return [.. rows.Select(row => (row.Id, row.Name))];
    }

    /// <summary>Rework rounds the item has had, across every chain it went through; a continued rework is still one round.</summary>
    public static Task<int> ReworkRoundsAsync(AutomationDbContext db, Guid itemId, CancellationToken ct) =>
        db.Runs.Where(run => run.ItemId == itemId && run.ReworkOfRunId != null)
            .Select(run => run.ReworkOfRunId).Distinct().CountAsync(ct);

    /// <summary>What the chain does after <paramref name="run"/>: the next step to queue, or where the item goes.</summary>
    /// <param name="Next">The step to queue; the item then stays where it is.</param>
    /// <param name="ReviewStateId">Where the item goes when the chain ended: the builder playbook's success state.</param>
    /// <param name="Note">What happens next or why the chain ended, for the outcome comment.</param>
    public sealed record Decision(ChainStep? Next, bool Ended, Guid? ReviewStateId, string? Note, string? StageName);

    /// <summary>
    /// The chain's decision for a finished implement or stage run, or null when the run is not
    /// part of a chain and finishes as it always did.
    /// </summary>
    public static async Task<Decision?> DecideAsync(
        AutomationDbContext db, Run run, string outcome, DateTimeOffset now, CancellationToken ct)
    {
        if (run.Kind == RunKind.Implement)
        {
            // Stages check a pull request; a run that delivers straight to the default branch has none.
            if (outcome != RunOutcomes.Succeeded || run.WorkOnDefaultBranch)
            {
                return null;
            }
            var stages = await StagesAsync(db, run.PlaybookId, ct);
            if (stages.Count == 0)
            {
                return null;
            }
            return new Decision(Step(run, run.PlaybookId, ChainStepKind.Stage, 0, now), false, null,
                $"Next: follow-up playbook 1 of {stages.Count}, {stages[0].Name}, checks the pull request.", null);
        }

        if (run.Kind != RunKind.Stage || run.ChainPlaybookId is not { } chainPlaybookId)
        {
            return null;
        }

        var chain = await db.Playbooks.AsNoTracking().SingleOrDefaultAsync(row => row.Id == chainPlaybookId, ct);
        var stageName = await db.Playbooks.AsNoTracking().Where(row => row.Id == run.PlaybookId)
            .Select(row => row.Name).SingleOrDefaultAsync(ct);
        var review = chain?.OnSuccessStateId;
        if (chain is null)
        {
            return new Decision(null, true, null, "The chain's builder playbook was deleted, so the chain ends here.", stageName);
        }

        if (outcome != RunOutcomes.Succeeded || run.StageVerdict is null)
        {
            var why = outcome switch
            {
                RunOutcomes.Succeeded => "finished without a verdict",
                RunOutcomes.Cancelled => "was cancelled",
                RunOutcomes.TimedOut => "timed out",
                _ => "failed",
            };
            return new Decision(null, true, review,
                $"The follow-up playbook {why}, so the chain ends here and the item goes to human review.", stageName);
        }

        var chainStages = await StagesAsync(db, chainPlaybookId, ct);
        if (run.StageVerdict == StageVerdict.Pass)
        {
            var next = run.StageIndex!.Value + 1;
            if (next < chainStages.Count)
            {
                return new Decision(Step(run, chainPlaybookId, ChainStepKind.Stage, next, now), false, null,
                    $"Next: follow-up playbook {next + 1} of {chainStages.Count}, {chainStages[next].Name}, checks the pull request.", stageName);
            }
            return new Decision(null, true, review,
                "Every follow-up playbook passed, so the item goes to human review.", stageName);
        }

        var rounds = await ReworkRoundsAsync(db, run.ItemId, ct);
        if (rounds < MaxReworkRounds)
        {
            return new Decision(Step(run, chainPlaybookId, ChainStepKind.Rework, 0, now), false, null,
                $"Next: the builder reworks the pull request with these findings (round {rounds + 1} of {MaxReworkRounds}), then the follow-up playbooks start again from the first.",
                stageName);
        }
        return new Decision(null, true, review,
            $"The item already had {MaxReworkRounds} rework rounds, so it goes to human review with these findings open.", stageName);
    }

    private static ChainStep Step(Run after, Guid chainPlaybookId, ChainStepKind kind, int stageIndex, DateTimeOffset now) => new()
    {
        OrganizationId = after.OrganizationId,
        ProjectId = after.ProjectId,
        ItemId = after.ItemId,
        ItemKey = after.ItemKey,
        AfterRunId = after.Id,
        ChainPlaybookId = chainPlaybookId,
        Kind = kind,
        StageIndex = stageIndex,
        CreatedAt = now,
        Status = ChainStepStatus.Pending,
    };
}
