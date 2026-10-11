import { reactive } from 'vue'
import type { QueryClient } from '@tanstack/vue-query'
import { createItem, type CreateItem, type WorkItem, type WorkItemType } from '@/api/items'
import { listItemTemplates } from '@/api/templates'

/**
 * The "Create ticket" quick title, hosted once by `AppShell` like the item dialog. Any page
 * opens it for the project it is showing, optionally into one team's backlog; Enter files the
 * ticket and opens it in the item detail, where everything else about it is written.
 */
const state = reactive<{
  open: boolean
  projectKey: string | null
  teamId: string | null
  type: WorkItemType
}>({ open: false, projectKey: null, teamId: null, type: 'story' })

export function useCreateTicket() {
  function open(projectKey: string, options: { teamId?: string | null; type?: WorkItemType } = {}) {
    state.projectKey = projectKey
    state.teamId = options.teamId ?? null
    state.type = options.type ?? 'story'
    state.open = true
  }

  return { state, open }
}

/**
 * Creates a ticket from a quick title, prefilled from its type's default template: its
 * description, priority and labels, so the item detail opens on the project's usual outline.
 */
export async function createTicket(
  client: QueryClient,
  slug: string,
  projectKey: string,
  item: CreateItem,
): Promise<WorkItem> {
  const templates = await client
    .fetchQuery({
      queryKey: [slug, projectKey, 'item-templates'],
      queryFn: () => listItemTemplates(slug, projectKey),
    })
    .catch(() => [])
  const template = templates.find((entry) => entry.type === item.type && entry.isDefault)
  return createItem(slug, projectKey, {
    ...item,
    descriptionMarkdown: item.descriptionMarkdown ?? template?.descriptionMarkdown,
    priority: item.priority ?? template?.defaultPriority ?? undefined,
    labelIds: item.labelIds ?? template?.defaultLabelIds,
  })
}
