import type { SprintTaskboard, TaskboardCell } from '@/api/sprints'
import type { WorkItem } from '@/api/items'

/** The taskboard API repeats the same columns for every story row. Keep the heading
 * source in one place so an empty sprint still has a useful grid. */
export function taskboardColumns(board: SprintTaskboard | undefined): TaskboardCell[] {
  return board?.rows[0]?.cells ?? board?.unparentedTasks ?? []
}

/**
 * Produces the optimistic taskboard state for a drag. The server is still authoritative:
 * callers replace this with the refetched board after the move settles.
 */
export function moveTaskboardTask(
  board: SprintTaskboard,
  task: WorkItem,
  destination: { cellKey: string; stateId: string },
): SprintTaskboard {
  const moveCells = (cells: TaskboardCell[]) => {
    const source = cells.find((cell) => cell.tasks.some((entry) => entry.id === task.id))
    if (!source) return cells
    return cells.map((cell) => {
      const withoutTask = cell.tasks.filter((entry) => entry.id !== task.id)
      const tasks =
        cell.key === destination.cellKey
          ? [...withoutTask, { ...task, stateId: destination.stateId }]
          : withoutTask
      const remainingHours = tasks.reduce((sum, entry) => sum + (entry.remainingHours ?? 0), 0)
      return {
        ...cell,
        tasks,
        remainingHours:
          source === cell || cell.key === destination.cellKey
            ? remainingHours
            : cell.remainingHours,
      }
    })
  }
  const rows = board.rows.map((row) => ({ ...row, cells: moveCells(row.cells) }))
  return { ...board, rows, unparentedTasks: moveCells(board.unparentedTasks) }
}
