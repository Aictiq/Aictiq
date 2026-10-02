import type { WorkItem, WorkItemPriority } from '@/api/items'
import type { Workflow, WorkflowState } from '@/api/workflows'

/**
 * The states an item may move to from where it is, by the same rule the server applies to
 * a transition or a board drop: a workflow without transitions allows every move, otherwise
 * only the listed ones (a `null` source meaning "from anywhere"). The current state always
 * stays in the list so a select can show it.
 */
export function allowedStates(workflow: Workflow | undefined, stateId: string): WorkflowState[] {
  if (!workflow) return []
  const states = [...workflow.states].sort((a, b) => a.position - b.position)
  if (!workflow.transitions.length) return states
  return states.filter(
    (state) =>
      state.id === stateId ||
      workflow.transitions.some(
        (transition) =>
          transition.toStateId === state.id &&
          (transition.fromStateId === null || transition.fromStateId === stateId),
      ),
  )
}

/**
 * Swaps one item inside whatever list shape a query cached: a page of items or a team
 * backlog's sections. Anything else, or a list without the item, comes back unchanged so a
 * broad cache sweep leaves unrelated queries alone.
 */
export function replaceListedItem<T>(data: T, id: string, change: (item: WorkItem) => WorkItem): T {
  const swap = (items: WorkItem[]) => {
    const index = items.findIndex((entry) => entry.id === id)
    if (index < 0) return items
    const next = [...items]
    next[index] = change(items[index]!)
    return next
  }
  if (!data || typeof data !== 'object') return data
  const record = data as Record<string, unknown>
  if (Array.isArray(record.items)) {
    const items = swap(record.items as WorkItem[])
    return items === record.items ? data : ({ ...record, items } as T)
  }
  if (Array.isArray(record.currentSprint) && Array.isArray(record.backlog)) {
    const currentSprint = swap(record.currentSprint as WorkItem[])
    const nextSprint = swap((record.nextSprint as WorkItem[] | undefined) ?? [])
    const backlog = swap(record.backlog as WorkItem[])
    if (
      currentSprint === record.currentSprint &&
      nextSprint === record.nextSprint &&
      backlog === record.backlog
    )
      return data
    return { ...record, currentSprint, nextSprint, backlog } as T
  }
  return data
}

/** Most urgent first, the order a person scanning for "what matters" reads in. */
export const priorityOptions: { value: WorkItemPriority; label: string }[] = [
  { value: 'urgent', label: 'Urgent' },
  { value: 'high', label: 'High' },
  { value: 'medium', label: 'Medium' },
  { value: 'low', label: 'Low' },
  { value: 'none', label: 'No priority' },
]
