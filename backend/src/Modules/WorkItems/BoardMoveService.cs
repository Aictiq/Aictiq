using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems;

public sealed record BoardMoveCommand(uint Version, Guid? ToStateId = null, Guid? ToColumnId = null,
    string? Column = null, string? AfterKey = null, bool Force = false);

public enum BoardMoveError { NotFound, NoTeam, NoBoard, VersionConflict, Validation, Transition, WipLimit }
public sealed record BoardMoveFailure(BoardMoveError Error, string Message, string? Field = null);
public sealed record BoardMoveResult(WorkItem? Item, WorkflowState? State, BoardColumnConfig? Column, BoardMoveFailure? Failure)
{
    public static BoardMoveResult Failed(BoardMoveError error, string message, string? field = null) =>
        new(null, null, null, new(error, message, field));
}

/// <summary>One serialized move path for board drag-and-drop and agent column moves.</summary>
public sealed class BoardMoveService(WorkItemsDbContext db, TenancyDbContext tenancy,
    IProjectAccess access, ICurrentUser user, TimeProvider clock)
{
    public async Task<BoardMoveResult> MoveAsync(string key, BoardMoveCommand request, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, key, ct);
        if (item is null) return BoardMoveResult.Failed(BoardMoveError.NotFound, $"item '{key}' not found or no access");
        if (item.TeamId is null) return BoardMoveResult.Failed(BoardMoveError.NoTeam, "The item has no team.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({item.ProjectId.ToString()}))", ct);
        db.ChangeTracker.Clear();
        item = await WorkItemEndpoints.FindWritable(db, access, user, key, ct);
        if (item is null) return BoardMoveResult.Failed(BoardMoveError.NotFound, $"item '{key}' not found or no access");
        if (item.TeamId is null) return BoardMoveResult.Failed(BoardMoveError.NoTeam, "The item has no team.");
        if (item.Version != request.Version) return BoardMoveResult.Failed(BoardMoveError.VersionConflict, "conflict: version changed");

        var board = await BoardEndpoints.FindAsync(db, item.TeamId.Value, ct);
        if (board is null)
            return request.Column is not null
                ? BoardMoveResult.Failed(BoardMoveError.NoBoard, "The item's team has no board.")
                : BoardMoveResult.Failed(BoardMoveError.Validation, "The target state is not on this team's board.", "toStateId");

        BoardColumnConfig column;
        WorkflowState target;
        if (request.Column is not null)
        {
            var matches = board.Config.Columns.Where(x => x.Name.Equals(request.Column.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return BoardMoveResult.Failed(BoardMoveError.Validation, $"column '{request.Column}' not found on the item's team board", "column");
            if (matches.Count > 1) return BoardMoveResult.Failed(BoardMoveError.Validation, $"column '{request.Column}' is ambiguous; column names must be unique to move by name", "column");
            column = matches[0];
            var states = await db.WorkflowStates.Where(x => column.StateIds.Contains(x.Id)
                && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == item.ProjectId)).ToDictionaryAsync(x => x.Id, ct);
            var workflowIds = states.Values.Select(x => x.WorkflowId).Distinct().ToArray();
            var transitions = await db.WorkflowTransitions.Where(x => workflowIds.Contains(x.WorkflowId)).ToListAsync(ct);
            // A column can share a state with another column. Preserve that state before
            // considering the first mapped state reachable under its workflow's rules.
            var selected = states.GetValueOrDefault(item.StateId) ?? column.StateIds.Select(id => states.GetValueOrDefault(id))
                .FirstOrDefault(state => state is not null && (!transitions.Any(t => t.WorkflowId == state.WorkflowId)
                    || transitions.Any(t => t.WorkflowId == state.WorkflowId && t.ToStateId == state.Id && (t.FromStateId == null || t.FromStateId == item.StateId))));
            if (selected is null) return BoardMoveResult.Failed(BoardMoveError.Transition, "That transition is not allowed: no state in the destination column is reachable.");
            target = selected;
        }
        else
        {
            if (request.ToStateId is not { } stateId || !BoardEndpoints.ContainsState(board, stateId))
                return BoardMoveResult.Failed(BoardMoveError.Validation, "The target state is not on this team's board.", "toStateId");
            var state = await db.WorkflowStates.FirstOrDefaultAsync(x => x.Id == stateId
                && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == item.ProjectId), ct);
            if (state is null) return BoardMoveResult.Failed(BoardMoveError.Validation, "Unknown project workflow state.", "toStateId");
            target = state;
            var transitions = db.WorkflowTransitions.Where(x => x.WorkflowId == target.WorkflowId);
            if (item.StateId != target.Id && await transitions.AnyAsync(ct)
                && !await transitions.AnyAsync(x => x.ToStateId == target.Id && (x.FromStateId == null || x.FromStateId == item.StateId), ct))
                return BoardMoveResult.Failed(BoardMoveError.Transition, "That transition is not allowed.");
            column = BoardEndpoints.ColumnFor(board, request.ToColumnId) ?? BoardEndpoints.ColumnFor(board, target.Id)!;
            if (!column.StateIds.Contains(target.Id))
                return BoardMoveResult.Failed(BoardMoveError.Validation, "The target column does not allow this workflow state.", "toColumnId");
        }

        var boardItems = await db.Items.Where(x => x.TeamId == item.TeamId && x.Id != item.Id && x.Rank != null).OrderBy(x => x.Rank).ToListAsync(ct);
        var count = boardItems.Count(x => BoardEndpoints.ColumnFor(x, board)?.Id == column.Id && x.Type is WorkItemType.Story or WorkItemType.Bug);
        if (column.WipLimit is { } limit && count >= limit)
        {
            var team = await tenancy.Teams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.TeamId, ct);
            if (!request.Force || team is null || !await BoardEndpoints.CanManageAsync(team, tenancy, access, user, ct))
                return BoardMoveResult.Failed(BoardMoveError.WipLimit, "The destination column has reached its WIP limit.");
        }
        var after = request.AfterKey is null ? null : await WorkItemEndpoints.FindVisible(db, access, user, request.AfterKey, ct);
        if (after is not null && (after.ProjectId != item.ProjectId || BoardEndpoints.ColumnFor(after, board)?.Id != column.Id))
            return BoardMoveResult.Failed(BoardMoveError.Validation, "The preceding card must be in the destination column.", "afterKey");
        var before = boardItems.FirstOrDefault(x => BoardEndpoints.ColumnFor(x, board)?.Id == column.Id && (after == null || string.CompareOrdinal(x.Rank, after.Rank) > 0));
        var rank = ItemRanking.Between(after?.Rank, before?.Rank);
        while (await db.Items.AnyAsync(x => x.ProjectId == item.ProjectId && x.Id != item.Id && x.Rank == rank, ct))
            rank = ItemRanking.Between(rank, before?.Rank);

        db.Entry(item).Property(x => x.Version).OriginalValue = request.Version;
        var now = clock.GetUtcNow();
        if (item.StateId != target.Id) item.Transition(target.Id, target.Category, user.UserId!, now);
        else item.UpdatedAt = now;
        item.BoardColumnId = column.Id;
        item.Rank = rank;
        item.Moved(user.UserId!);
        if (target.Category == WorkflowStateCategory.Completed && item.Type is WorkItemType.Task or WorkItemType.Bug) item.RemainingHours = 0;
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return BoardMoveResult.Failed(BoardMoveError.VersionConflict, "conflict: version changed"); }
        return new(item, target, column, null);
    }
}
