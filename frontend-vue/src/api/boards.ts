import { apiFetch } from '@/utils/api'
import type { WorkItem, WorkItemType } from '@/api/items'
import { listTeams } from '@/api/teams'

export type BoardGeneralState = 'new' | 'doing' | 'done'
export interface BoardColumn { id: string; name: string; stateIds: string[]; generalState: BoardGeneralState; wipLimit: number | null; count: number; wipExceeded: boolean; cards: WorkItem[] }
export interface Board { id: string; teamId: string; kind: 'kanban' | 'taskboard'; swimlane: string; cardFields: string[]; types: WorkItemType[]; columns: BoardColumn[]; version: number }
export interface BoardColumnConfig { id: string; name: string; stateIds: string[]; generalState?: BoardGeneralState | null; wipLimit?: number | null }
export interface UpdateBoard { columns?: BoardColumnConfig[]; swimlane?: string; cardFields?: string[]; types?: WorkItemType[]; version: number }

const base = (slug: string, teamId: string) => `/orgs/${slug}/teams/${teamId}/board`

export interface BoardQuery {
  filter?: string
  /** Search box text. */
  q?: string
  assigneeIds?: string[]
  /** Cards returned per column (server default 50); `count` is always the full column total. */
  take?: number
  /** Per-column overrides of `take`, for columns that have been scrolled further. */
  expand?: Record<string, number>
}

export const getBoard = (slug: string, teamId: string, options: BoardQuery = {}) =>
  apiFetch<Board>(base(slug, teamId), {
    query: {
      filter: options.filter || undefined,
      q: options.q || undefined,
      assigneeIds: options.assigneeIds?.length ? options.assigneeIds.join(',') : undefined,
      take: options.take,
      expand:
        options.expand && Object.keys(options.expand).length
          ? Object.entries(options.expand)
              .map(([columnId, count]) => `${columnId}:${count}`)
              .join(',')
          : undefined,
    },
  })

export const updateBoard = (slug: string, teamId: string, body: UpdateBoard) =>
  apiFetch<Board>(base(slug, teamId), { method: 'PUT', body })

export const boardMove = (slug: string, key: string, body: { toStateId: string; toColumnId?: string; afterKey?: string | null; version: number; force?: boolean }) =>
  apiFetch<WorkItem>(`/orgs/${slug}/items/${key}/board-move`, { method: 'POST', body })

/**
 * Names each workflow state the way the project's boards show it. Teams rename columns
 * freely while the underlying states keep their workflow names, so a state picker that
 * lists raw state names does not match what people see on the board. A column that groups
 * several states keeps the state name in parentheses so the choice stays unambiguous.
 * Falls back to the state's own name when no board shows it or boards cannot be read.
 */
export async function boardStateLabels(
  slug: string,
  projectKey: string,
  states: { id: string; name: string }[],
): Promise<Map<string, string>> {
  const columnsByState = new Map<string, Set<string>>()
  try {
    const teams = await listTeams(slug, projectKey)
    const boards = await Promise.all(teams.map((team) => getBoard(slug, team.id, { take: 0 })))
    for (const column of boards.flatMap((board) => board.columns)) {
      for (const stateId of column.stateIds) {
        const label =
          column.stateIds.length > 1 ? `${column.name} (${stateNameOf(stateId)})` : column.name
        if (!columnsByState.has(stateId)) columnsByState.set(stateId, new Set())
        columnsByState.get(stateId)!.add(label)
      }
    }
  } catch {
    columnsByState.clear()
  }
  return new Map(
    states.map((state) => [state.id, [...(columnsByState.get(state.id) ?? [state.name])].join(' / ')]),
  )

  function stateNameOf(stateId: string) {
    return states.find((state) => state.id === stateId)?.name ?? 'state'
  }
}

/** The same states, renamed to what the project's boards call them (see `boardStateLabels`). */
export async function withBoardNames<T extends { id: string; name: string }>(
  slug: string,
  projectKey: string,
  states: T[],
): Promise<T[]> {
  const labels = await boardStateLabels(slug, projectKey, states)
  return states.map((state) => ({ ...state, name: labels.get(state.id) ?? state.name }))
}
