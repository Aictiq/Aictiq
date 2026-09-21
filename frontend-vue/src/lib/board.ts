import type { Board, BoardColumn } from '@/api/boards'
import type { WorkItem } from '@/api/items'

export function columnForState(board: Board, stateId: string): BoardColumn | undefined {
  return board.columns.find((column) => column.stateIds.includes(stateId))
}

/** Pure optimistic move/reorder: the caller can restore its original query value on a rejected write. */
export function moveBoardCard(board: Board, card: WorkItem, toStateId: string, toColumnId: string, afterKey?: string | null): Board {
  const from = board.columns.find((column) => column.cards.some((entry) => entry.id === card.id))
  const to = board.columns.find((column) => column.id === toColumnId)
  if (!from || !to) return board
  const moved = { ...card, stateId: toStateId, boardColumnId: toColumnId }
  const destination = to.cards.filter((entry) => entry.id !== card.id)
  const index = afterKey === null ? 0 : afterKey ? destination.findIndex((entry) => entry.key === afterKey) + 1 : destination.length
  destination.splice(Math.max(0, index), 0, moved)
  return {
    ...board,
    columns: board.columns.map((column) => {
      if (column === from && column === to) return { ...column, cards: destination }
      if (column === from) return { ...column, count: column.count - 1, cards: column.cards.filter((entry) => entry.id !== card.id) }
      if (column === to) return { ...column, count: column.count + 1, cards: destination }
      return column
    }),
  }
}

export function destinationIsAtWipLimit(column: BoardColumn, card: WorkItem): boolean {
  return !column.stateIds.includes(card.stateId) && column.wipLimit !== null && column.count >= column.wipLimit
}

/** Workflow states no column shows yet — cards in them would be invisible on the board. */
export function unmappedStates<T extends { id: string }>(columns: ReadonlyArray<{ stateIds: readonly string[] }>, states: readonly T[]): T[] {
  const mapped = new Set(columns.flatMap((column) => column.stateIds))
  return states.filter((state) => !mapped.has(state.id))
}
