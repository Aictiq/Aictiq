import { reactive } from 'vue'
import type { WorkItemType } from '@/api/items'

/**
 * The "Create ticket" dialog, hosted once by `AppShell` like the item dialog. Any page opens
 * it for the project it is showing, optionally into one team's backlog.
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
