import type { WorkItemType } from '@/api/items'

/** Mirrors the server's `ItemHierarchy` matrix (and the `work.check_item_parent_type()`
 * trigger that guarantees it). The UI uses it only to offer valid choices; the server
 * still decides. */
const parents: Record<WorkItemType, readonly WorkItemType[]> = {
  epic: [],
  feature: ['epic'],
  story: ['epic', 'feature'],
  bug: ['epic', 'feature', 'story'],
  task: ['story', 'bug'],
}

export const typeLabels: Record<WorkItemType, string> = { epic: 'Epic', feature: 'Feature', story: 'Story', bug: 'Bug', task: 'Task' }

export function allowsParent(parent: WorkItemType, child: WorkItemType): boolean {
  return parents[child].includes(parent)
}

/** Features and Tasks only make sense beneath something; Stories and Bugs may stand alone. */
export function requiresParent(type: WorkItemType): boolean {
  return type === 'feature' || type === 'task'
}

/** Child types offered by a row's "add" action, most common first. */
export function childTypes(parent: WorkItemType): WorkItemType[] {
  const order: WorkItemType[] = ['story', 'bug', 'feature', 'task']
  return order.filter((child) => allowsParent(parent, child))
}

/** Stories and Bugs carry the detail (description, acceptance criteria, estimate), so a quick
 * add opens them for editing; the other types stay quick. */
export function opensOnCreate(type: WorkItemType): boolean {
  return type === 'story' || type === 'bug'
}
