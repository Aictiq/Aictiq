import { apiFetch } from '@/utils/api'
import type { WorkItem, WorkItemType } from '@/api/items'

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
