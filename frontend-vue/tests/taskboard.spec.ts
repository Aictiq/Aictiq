import { describe, expect, it } from 'vitest'

import { moveTaskboardTask, taskboardColumns } from '@/lib/taskboard'
import type { SprintTaskboard } from '@/api/sprints'
import type { WorkItem } from '@/api/items'

const task = (id: string, stateId = 'todo'): WorkItem => ({
  id,
  key: `WEB-${id}`,
  type: 'task',
  title: id,
  stateId,
  descriptionMarkdown: '',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'none',
  assigneeId: null,
  teamId: 'team',
  sprintId: 'sprint',
  parentId: 'story',
  points: null,
  estimateHours: 3,
  remainingHours: 3,
  completedHours: 0,
  dueDate: null,
  claimedBy: null,
  claimedAt: null,
  claimHeartbeatAt: null,
  version: 1,
  labels: [],
  updatedAt: '',
  isWatching: false,
  watcherCount: 0,
  rollup: {
    totalCount: 0,
    completedCount: 0,
    pointsTotal: 0,
    pointsCompleted: 0,
    remainingHours: 0,
  },
})
const board = (): SprintTaskboard => ({
  sprintId: 'sprint',
  rows: [
    {
      parentKey: 'WEB-1',
      name: 'Story',
      remainingHours: 3,
      cells: [
        { key: 'todo', name: 'To do', category: 'proposed', remainingHours: 3, tasks: [task('2')] },
        { key: 'doing', name: 'Doing', category: 'active', remainingHours: 0, tasks: [] },
      ],
    },
  ],
  unparentedTasks: [],
})

describe('taskboard transforms', () => {
  it('uses the first row as the shared column definition', () =>
    expect(taskboardColumns(board()).map((cell) => cell.name)).toEqual(['To do', 'Doing']))
  it('moves a task between cells without mutating the rollback source', () => {
    const source = board()
    const next = moveTaskboardTask(source, source.rows[0]!.cells[0]!.tasks[0]!, {
      cellKey: 'doing',
      stateId: 'state-doing',
    })
    expect(source.rows[0]!.cells[0]!.tasks).toHaveLength(1)
    expect(next.rows[0]!.cells[0]!.tasks).toHaveLength(0)
    expect(next.rows[0]!.cells[1]!.tasks[0]!.stateId).toBe('state-doing')
  })
})
