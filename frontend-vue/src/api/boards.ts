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
 * A place a playbook can send an item: a workflow state, and the board column that should show
 * it when several columns share that state. `columnId` is null for a state no board shows.
 */
export interface BoardTarget {
  stateId: string
  columnId: string | null
  /** The column's name, with the state in parentheses when the column groups several states. */
  name: string
  /** The team whose board has the column, when the project has more than one board. */
  team: string | null
}

/** How a target reads in a picker: the column name, then its team when that is ambiguous. */
export const boardTargetLabel = (target: BoardTarget) =>
  target.team ? `${target.name} · ${target.team}` : target.name

/**
 * Every column of the project's team boards, once per state it shows, in workflow order. Teams
 * rename columns freely while the underlying states keep their workflow names, so a picker that
 * lists raw state names does not match what people see on the board. States no board shows keep
 * their own name, and every state falls back to its own name when boards cannot be read.
 */
export async function boardTargets(
  slug: string,
  projectKey: string,
  states: { id: string; name: string }[],
): Promise<BoardTarget[]> {
  let columns: { column: BoardColumn; team: string }[] = []
  let boardCount = 0
  try {
    const teams = await listTeams(slug, projectKey)
    const boards = await Promise.all(teams.map((team) => getBoard(slug, team.id, { take: 0 })))
    boardCount = boards.length
    columns = boards.flatMap((board, index) =>
      board.columns.map((column) => ({ column, team: teams[index]!.name })),
    )
  } catch {
    columns = []
  }
  return states.flatMap((state): BoardTarget[] => {
    const showing = columns.filter(({ column }) => column.stateIds.includes(state.id))
    if (showing.length === 0) {
      return [{ stateId: state.id, columnId: null, name: state.name, team: null }]
    }
    return showing.map(({ column, team }) => ({
      stateId: state.id,
      columnId: column.id,
      name: column.stateIds.length > 1 ? `${column.name} (${state.name})` : column.name,
      team: boardCount > 1 ? team : null,
    }))
  })
}

/**
 * Names each workflow state the way the project's boards show it (see `boardTargets`). A state
 * several columns show is named after all of them, joined with " / ".
 */
export async function boardStateLabels(
  slug: string,
  projectKey: string,
  states: { id: string; name: string }[],
): Promise<Map<string, string>> {
  return stateLabels(await boardTargets(slug, projectKey, states))
}

/** One name per state from its targets, the way `boardStateLabels` names it. */
export function stateLabels(targets: BoardTarget[]): Map<string, string> {
  const names = new Map<string, Set<string>>()
  for (const target of targets) {
    if (!names.has(target.stateId)) names.set(target.stateId, new Set())
    names.get(target.stateId)!.add(target.name)
  }
  return new Map([...names].map(([stateId, set]) => [stateId, [...set].join(' / ')]))
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
