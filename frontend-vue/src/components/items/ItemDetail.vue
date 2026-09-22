<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import { Bot, Check, Copy, GitBranch, Save, Trash2, X } from '@lucide/vue'
import {
  attachmentContentTypes,
  attachmentUrl,
  commitAttachment,
  settleAttachments,
  uploadAttachment,
} from '@/api/attachments'
import { createComment, listComments } from '@/api/comments'
import { itemHistory } from '@/api/history'
import {
  deleteItem,
  getItem,
  itemDeletePreview,
  listItemChildren,
  listWatchers,
  releaseItem,
  transitionItem,
  unwatchItem,
  editItem,
  watchItem,
  type WorkItem,
  type WorkItemPriority,
} from '@/api/items'
import { boardMove, getBoard } from '@/api/boards'
import { listMembers } from '@/api/members'
import { avatarUrl } from '@/api/profile'
import { listItemRuns } from '@/api/runs'
import { getProject, hasProjectRole, listProjectMembers } from '@/api/projects'
import { createGitHubBranch, listGitHubBindings } from '@/api/github'
import { listLinks, listRelations } from '@/api/relations'
import { listWorkflows } from '@/api/workflows'
import ClaimBanner from '@/components/common/ClaimBanner.vue'
import DeleteConfirmDialog from '@/components/common/DeleteConfirmDialog.vue'
import Markdown from '@/components/common/Markdown.vue'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'
import PriorityIcon from '@/components/common/PriorityIcon.vue'
import TimeTrackingPopover from '@/components/common/TimeTrackingPopover.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import RunStatusBadge from '@/components/factory/RunStatusBadge.vue'
import StartRunDialog from '@/components/factory/StartRunDialog.vue'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { branchName } from '@/api/relations'
import type { TimeTrackingItem } from '@/api/time-tracking'
import { useItemModal } from '@/composables/useItemModal'
import { useCommands } from '@/composables/useCommands'
import { since } from '@/lib/claims'
import { factoryRunPath } from '@/router/paths'
import { useProjectRealtime } from '@/composables/useProjectRealtime'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { isLiveRun, runDuration, runRequesterLabel, startRunButton } from '@/lib/runs'
import { toApiError } from '@/utils/api'

/**
 * One work item: its page when opened by URL (`ItemDetailView`) and the dialog when opened
 * from a list (`ItemDetailDialog`). `modal` only changes how it leaves and where child links go.
 */
const props = defineProps<{ slug: string; projectKey: string; itemKey: string; modal?: boolean }>()
const emit = defineEmits<{ close: [] }>()
const router = useRouter()
const itemModal = useItemModal()
const client = useQueryClient()
const tab = ref<'comments' | 'activity' | 'relations'>('comments')
const item = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey]),
  queryFn: () => getItem(props.slug, props.itemKey),
})
const comments = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'comments']),
  queryFn: () => listComments(props.slug, props.itemKey),
})
const history = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'history']),
  queryFn: () => itemHistory(props.slug, props.itemKey),
})
const relations = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'relations']),
  queryFn: () => listRelations(props.slug, props.itemKey),
})
const links = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'links']),
  queryFn: () => listLinks(props.slug, props.itemKey),
})
const watchers = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'watchers']),
  queryFn: () => listWatchers(props.slug, props.itemKey),
})
const currentItem = computed(() => item.data.value ?? null)
const children = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'children']),
  queryFn: () => listItemChildren(props.slug, props.itemKey),
  // Epics are portfolio containers. Their children belong in planning, not in the
  // execution-focused child-work list shown on an item.
  enabled: computed(
    () => currentItem.value?.type !== undefined && currentItem.value.type !== 'epic',
  ),
})
const childItems = computed(() => children.data.value ?? [])
const workflow = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'workflows']),
  queryFn: () => listWorkflows(props.slug, props.projectKey),
})
const board = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'item-board', currentItem.value?.teamId]),
  queryFn: () => getBoard(props.slug, currentItem.value!.teamId!, { take: 0 }),
  enabled: computed(() => currentItem.value?.teamId != null),
})
const workflowStates = computed(() => workflow.data.value?.flatMap((entry) => entry.states) ?? [])
const boardColumnForState = computed(() => {
  const columns = board.data.value?.columns ?? []
  return new Map(
    columns.flatMap((column) => column.stateIds.map((stateId) => [stateId, column.name])),
  )
})
const boardIsLoading = computed(() => currentItem.value?.teamId != null && board.isPending.value)
const statusOptions = computed(() =>
  workflowStates.value.map((state) => ({
    ...state,
    column: boardColumnForState.value.get(state.id),
  })),
)
const selectedStateId = ref('')
const changingStatus = ref(false)
const statusError = ref<string | null>(null)
const commentItems = computed(() => comments.data.value?.items ?? [])
const historyItems = computed(() => history.data.value?.items ?? [])
const relationItems = computed(() => relations.data.value ?? [])
const linkItems = computed(() => links.data.value ?? [])
const commitItems = computed(() => linkItems.value.filter((link) => link.kind === 'commit'))
const pullRequestItems = computed(() =>
  linkItems.value.filter((link) => link.kind === 'pullRequest'),
)
const branchItems = computed(() => linkItems.value.filter((link) => link.kind === 'branch'))
const watcherItems = computed(() => (Array.isArray(watchers.data.value) ? watchers.data.value : []))
const session = useSessionStore()
// Only fetched while something is actually claimed: the banner is the one place on this
// screen that needs a name for an id, and most items are not claimed.
const claimedBy = computed(() => currentItem.value?.claimedBy ?? null)
const people = useQuery({
  queryKey: computed(() => [props.slug, 'members', 'claimant']),
  queryFn: () => listMembers(props.slug, { pageSize: 100 }),
  enabled: computed(() => claimedBy.value !== null),
})
const project = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'project']),
  queryFn: () => getProject(props.slug, props.projectKey),
})
const projectMembers = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'project-members']),
  queryFn: () => listProjectMembers(props.slug, props.projectKey),
})
const membersById = computed(
  () => new Map((projectMembers.data.value ?? []).map((member) => [member.userId, member])),
)
const githubBindings = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'github-bindings']),
  queryFn: () => listGitHubBindings(props.slug, props.projectKey),
})
const branchRepoId = ref<number | null>(null)
const creatingBranch = ref(false)
const holder = computed(() => {
  const member = people.data.value?.items.find((x) => x.userId === claimedBy.value)
  return member
    ? {
        id: member.userId,
        displayName: member.displayName,
        avatarKey: member.avatarKey,
        isAgent: member.isAgent,
      }
    : null
})
const releasing = ref(false)

// ── Agent runs ──────────────────────────────────────────────────────────────────────
// A run is the item's execution history: who dispatched it, what it left behind. The
// section shows it to everyone who can see the item; the raw log and the run page are
// the factory operators', and the API holds that line regardless of what the
// client draws.
const organizations = useOrganizationsStore()
const runsQuery = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'runs']),
  queryFn: () => listItemRuns(props.slug, props.itemKey),
})
const itemRuns = computed(() => runsQuery.data.value?.items ?? [])
const liveRun = computed(() => itemRuns.value.find((entry) => isLiveRun(entry.status)) ?? null)
const startRun = computed(() =>
  startRunButton({
    canOperateFactory: organizations.current?.canOperateFactory ?? false,
    projectRole: project.data.value?.role ?? null,
    projectArchived: project.data.value?.isArchived ?? false,
    claimedBy: claimedBy.value,
    claimedByName: holder.value?.displayName ?? null,
    hasLiveRun: liveRun.value !== null,
  }),
)
const startRunOpen = ref(false)
const claimRun = computed(() =>
  liveRun.value && liveRun.value.agentId === claimedBy.value ? liveRun.value : null,
)
/** The log and the run page are the operators'; everyone else sees status and PR only. */
const mayOpenRunLog = computed(() => organizations.current?.canOperateFactory === true)
const claimRunLogTo = computed(() =>
  mayOpenRunLog.value && claimRun.value
    ? factoryRunPath(props.slug, claimRun.value.id)
    : null,
)

const commands = useCommands(() => [
  {
    id: 'items.start-run',
    group: 'Item',
    label: 'Hand this item to an agent',
    icon: '▶',
    keywords: 'start run agent factory dispatch ai playbook develop',
    when: () => startRun.value.visible,
    run: () => {
      startRunOpen.value = true
    },
  },
])
onBeforeUnmount(() => commands.dispose())

async function onDispatched() {
  // The dispatch claimed the item and may have moved it; the server is the truth.
  await Promise.all([
    client.invalidateQueries({ queryKey: [props.slug, props.itemKey, 'runs'] }),
    client.invalidateQueries({ queryKey: [props.slug, props.itemKey] }),
    runsQuery.refetch(),
  ])
}

const description = ref('')
const title = ref('')
const comment = ref('')
const saving = ref(false)
const conflict = ref(false)
const changingAssignee = ref(false)
const assigneeError = ref<string | null>(null)
const priorities: { value: WorkItemPriority; label: string }[] = [
  { value: 'none', label: 'No priority' },
  { value: 'low', label: 'Low' },
  { value: 'medium', label: 'Medium' },
  { value: 'high', label: 'High' },
  { value: 'urgent', label: 'Urgent' },
]
// The server's estimation rules: stories carry points, tasks carry hours, bugs may carry
// both, and epics and features are estimated only by their children's rollup.
const usesPoints = computed(
  () => currentItem.value?.type === 'story' || currentItem.value?.type === 'bug',
)
const usesHours = computed(
  () => currentItem.value?.type === 'task' || currentItem.value?.type === 'bug',
)
type NumberField = 'points' | 'estimateHours' | 'remainingHours' | 'completedHours'
const drafts = ref<Record<NumberField, string>>({
  points: '',
  estimateHours: '',
  remainingHours: '',
  completedHours: '',
})
const savingPlanning = ref(false)
const planningError = ref<string | null>(null)
useProjectRealtime(
  () => props.slug,
  () => props.projectKey,
  () => props.itemKey,
)
watch(
  item.data,
  (value, previous) => {
    if (value) {
      // A refetch (a save elsewhere, a realtime event) must not wipe what someone is typing:
      // the server's text only replaces the local copy while the two still agree.
      if (!previous || title.value === previous.title) title.value = value.title
      if (!previous || description.value === (previous.descriptionMarkdown ?? ''))
        description.value = value.descriptionMarkdown ?? ''
      selectedStateId.value = value.stateId
      for (const field of Object.keys(drafts.value) as NumberField[])
        drafts.value[field] = value[field] === null ? '' : String(value[field])
    }
  },
  { immediate: true },
)
const isDirty = computed(() => {
  const current = currentItem.value
  if (!current) return false
  return (
    title.value.trim() !== current.title ||
    description.value !== (current.descriptionMarkdown ?? '') ||
    comment.value.trim() !== ''
  )
})
// The modal closes by changing `?item=` on the same route, which is a route *update* rather
// than a leave, so a global guard covers both it and the page's own navigation away.
const removeGuard = router.beforeEach((to, from) => {
  if (!isDirty.value) return true
  if (to.path === from.path && to.query.item === from.query.item) return true
  return askToLeave()
})
function warnBeforeUnload(event: BeforeUnloadEvent) {
  if (isDirty.value) event.preventDefault()
}
window.addEventListener('beforeunload', warnBeforeUnload)
onBeforeUnmount(() => {
  removeGuard()
  window.removeEventListener('beforeunload', warnBeforeUnload)
})
/** The open "save your changes?" question, answered by one of its three buttons. */
const leavePrompt = ref<{ resolve: (leave: boolean) => void } | null>(null)
const leaveError = ref<string | null>(null)
function askToLeave() {
  leaveError.value = null
  return new Promise<boolean>((resolve) => (leavePrompt.value = { resolve }))
}
function answerLeave(leave: boolean) {
  leavePrompt.value?.resolve(leave)
  leavePrompt.value = null
}
async function saveAndLeave() {
  if (await save()) answerLeave(true)
  else leaveError.value = conflict.value ? 'Someone changed this item first.' : 'Saving failed.'
}
function discardAndLeave() {
  const current = currentItem.value
  if (current) {
    title.value = current.title
    description.value = current.descriptionMarkdown ?? ''
  }
  comment.value = ''
  answerLeave(true)
}
const titleInput = ref<HTMLTextAreaElement | null>(null)
/** The title wraps instead of scrolling out of sight, so the box grows with it. */
function fitTitle() {
  const element = titleInput.value
  if (!element) return
  element.style.height = 'auto'
  element.style.height = `${element.scrollHeight}px`
}
watch([title, titleInput], () => void nextTick(fitTitle), { flush: 'post' })
function onTitleInput() {
  // A title is one line; pasted line breaks become spaces.
  if (/[\r\n]/.test(title.value)) title.value = title.value.replace(/[\r\n]+/g, ' ')
}
// Stories carry points; their hours live on the tasks under them.
const childHours = computed(() => {
  const sum = (field: 'estimateHours' | 'remainingHours' | 'completedHours') =>
    childItems.value.reduce((total, child) => total + (child[field] ?? 0), 0)
  return {
    estimate: sum('estimateHours'),
    remaining: sum('remainingHours'),
    completed: sum('completedHours'),
  }
})
/** The lists and the board show priority, points and hours, so they go stale with the item. */
function invalidateLists(current: WorkItem) {
  return Promise.all([
    client.invalidateQueries({ queryKey: [props.slug, props.itemKey, 'history'] }),
    client.invalidateQueries({ queryKey: [props.slug, props.projectKey, 'items'] }),
    ...(current.teamId
      ? [
          client.invalidateQueries({
            queryKey: ['board', props.slug, props.projectKey, current.teamId],
          }),
          client.invalidateQueries({ queryKey: [props.slug, current.teamId, 'items', 'backlog'] }),
        ]
      : []),
  ])
}
// ── Delete ──────────────────────────────────────────────────────────────────────────
// For good, with every item below it. The Removed state is how an item is set aside; this is
// how it stops existing, so the dialog first asks the server what would go with it.
const mayDelete = computed(
  () =>
    hasProjectRole(project.data.value?.role ?? 'guest', 'member') &&
    !project.data.value?.isArchived,
)
const deleteOpen = ref(false)
const deleting = ref(false)
const deleteError = ref<string | null>(null)
const deletePreview = useQuery({
  queryKey: computed(() => [props.slug, props.itemKey, 'delete-preview']),
  queryFn: () => itemDeletePreview(props.slug, props.itemKey),
  enabled: deleteOpen,
  gcTime: 0,
})
const doomedChildren = computed(() => deletePreview.data.value?.descendants ?? [])
function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`
}
const deleteConsequences = computed(() => {
  const preview = deletePreview.data.value
  const counted = (count: number | undefined, one: string, many: string, fallback: string) =>
    count === undefined ? fallback : plural(count, one, many)
  return [
    counted(preview?.comments, 'comment', 'comments', 'comments'),
    `${counted(preview?.attachments, 'attached file', 'attached files', 'attached files')}, removed from storage`,
    `history, labels, watchers and ${counted(preview?.links, 'commit/PR link', 'commit/PR links', 'linked commits and PRs')}`,
    `${counted(preview?.relations, 'relation', 'relations', 'relations')} to other items`,
    'notifications, metrics and wiki references',
  ]
})
function openDelete() {
  deleteError.value = null
  deleteOpen.value = true
}
async function destroy() {
  const current = currentItem.value
  if (!current) return
  deleting.value = true
  deleteError.value = null
  try {
    const doomed = [current.key, ...doomedChildren.value.map((child) => child.key)]
    await deleteItem(props.slug, current.key)
    deleteOpen.value = false
    // Nothing is left to save, so leaving must not ask about unsaved edits.
    title.value = current.title
    description.value = current.descriptionMarkdown ?? ''
    comment.value = ''
    for (const key of doomed) client.removeQueries({ queryKey: [props.slug, key] })
    await invalidateLists(current)
    await client.invalidateQueries({ queryKey: [props.slug, props.projectKey] })
    emit('close')
  } catch (error) {
    const problem = toApiError(error)
    deleteError.value = problem.problem?.detail ?? problem.title
  } finally {
    deleting.value = false
  }
}
async function editPlanning(changes: Partial<WorkItem>) {
  const current = item.data.value
  if (!current || savingPlanning.value) return
  savingPlanning.value = true
  planningError.value = null
  try {
    const updated = await editItem(props.slug, current, changes)
    client.setQueryData<WorkItem>([props.slug, props.itemKey], updated)
    await invalidateLists(current)
  } catch (error) {
    const problem = toApiError(error)
    planningError.value = Object.values(problem.fieldErrors).flat()[0] ?? problem.title
    // Put the inputs back to what the server holds rather than leaving a value it refused.
    for (const field of Object.keys(drafts.value) as NumberField[])
      drafts.value[field] = current[field] === null ? '' : String(current[field])
  } finally {
    savingPlanning.value = false
  }
}
function changePriority(event: Event) {
  const priority = (event.target as HTMLSelectElement).value as WorkItemPriority
  if (priority !== currentItem.value?.priority) void editPlanning({ priority })
}
function commitNumber(field: NumberField) {
  const current = item.data.value
  if (!current) return
  const raw = String(drafts.value[field]).trim()
  const value = raw === '' ? null : Number(raw)
  if (value === current[field]) return
  if (
    value !== null &&
    (!Number.isFinite(value) || value < 0 || value > 9999.99 || !/^\d+(?:\.\d{1,2})?$/.test(raw))
  ) {
    planningError.value = 'Enter a number from 0 to 9999.99 with at most two decimals.'
    drafts.value[field] = current[field] === null ? '' : String(current[field])
    return
  }
  const changes: Partial<WorkItem> = { [field]: value }
  // A first estimate is also the first remaining figure; after that they move independently.
  if (
    field === 'estimateHours' &&
    current.estimateHours === null &&
    current.remainingHours === null
  )
    changes.remainingHours = value
  void editPlanning(changes)
}
async function changeStatus() {
  const current = item.data.value
  const toStateId = selectedStateId.value
  if (!current || !toStateId || current.stateId === toStateId) return

  changingStatus.value = true
  statusError.value = null
  try {
    // A board move also assigns a rank and enforces the destination's WIP limit. States
    // outside the board remain valid workflow transitions, including for unteamed items.
    const isBoardState = boardColumnForState.value.has(toStateId)
    const updated = isBoardState
      ? await boardMove(props.slug, current.key, { toStateId, version: current.version })
      : await transitionItem(props.slug, current.key, { toStateId, version: current.version })
    client.setQueryData<WorkItem>([props.slug, props.itemKey], updated)
    // These screens use separate query keys and are often unmounted while the detail is
    // open. Marking them stale here guarantees they refetch when the user returns,
    // rather than waiting for a realtime event that may not arrive before navigation.
    await invalidateLists(current)
  } catch (error) {
    selectedStateId.value = current.stateId
    statusError.value = toApiError(error).title
  } finally {
    changingStatus.value = false
  }
}
async function save() {
  const current = item.data.value
  if (!current) return false
  saving.value = true
  conflict.value = false
  try {
    await editItem(props.slug, current, {
      title: title.value,
      descriptionMarkdown: description.value,
    })
    await Promise.all([
      client.invalidateQueries({ queryKey: [props.slug, props.itemKey] }),
      invalidateLists(current),
    ])
    return true
  } catch (error) {
    conflict.value = (error as { status?: number }).status === 409
    return false
  } finally {
    saving.value = false
  }
}
async function saveAndClose() {
  if (await save()) emit('close')
}
function openChild(key: string) {
  if (props.modal) itemModal.open(key)
  else void router.push(`/o/${props.slug}/p/${props.projectKey}/items/${key}`)
}
async function changeAssignee(event: Event) {
  const current = item.data.value
  const assigneeId = (event.target as HTMLSelectElement).value || null
  if (!current || current.assigneeId === assigneeId || changingAssignee.value) return
  changingAssignee.value = true
  assigneeError.value = null
  try {
    const updated = await editItem(props.slug, current, { assigneeId })
    client.setQueryData<WorkItem>([props.slug, props.itemKey], updated)
    await Promise.all([
      client.invalidateQueries({ queryKey: [props.slug, props.projectKey, 'items'] }),
      ...(current.teamId
        ? [
            client.invalidateQueries({
              queryKey: ['board', props.slug, props.projectKey, current.teamId],
            }),
          ]
        : []),
    ])
  } catch (error) {
    assigneeError.value = toApiError(error).title
  } finally {
    changingAssignee.value = false
  }
}
const acceptedFiles = attachmentContentTypes.join(',')
// The item exists, so a description's file is committed to it straight away.
async function uploadToDescription(file: File) {
  const current = item.data.value
  if (!current) throw new Error('The item is still loading.')
  const id = await uploadAttachment(props.slug, props.projectKey, file)
  await commitAttachment(props.slug, id, { itemId: current.id })
  return attachmentUrl(props.slug, id)
}
// A comment has no id until it is posted: its files stay pending (visible to their uploader)
// and are committed to the comment once it exists.
const pendingCommentFiles = new Set<string>()
const commentEditor = ref<{ uploading: boolean } | null>(null)
const sending = ref(false)
async function uploadToComment(file: File) {
  const id = await uploadAttachment(props.slug, props.projectKey, file)
  pendingCommentFiles.add(id)
  return attachmentUrl(props.slug, id)
}
async function addComment() {
  const body = comment.value.trim()
  if (!body || sending.value || commentEditor.value?.uploading) return
  sending.value = true
  try {
    const created = await createComment(props.slug, props.itemKey, body)
    const pending = [...pendingCommentFiles]
    pendingCommentFiles.clear()
    comment.value = ''
    await settleAttachments(props.slug, pending, body, { commentId: created.id })
    await client.invalidateQueries({ queryKey: [props.slug, props.itemKey, 'comments'] })
  } finally {
    sending.value = false
  }
}
async function copy(value: string) {
  await navigator.clipboard.writeText(value)
}
async function toggleWatch() {
  const current = item.data.value
  if (!current) return
  if (current.isWatching) await unwatchItem(props.slug, props.itemKey)
  else await watchItem(props.slug, props.itemKey)
  await Promise.all([item.refetch(), watchers.refetch()])
}
async function release() {
  releasing.value = true
  // Refetch rather than patching the cache: releasing can also change the assignee, and
  // the server is the one that knows what the item looks like afterwards.
  try {
    await releaseItem(props.slug, props.itemKey)
    await item.refetch()
  } finally {
    releasing.value = false
  }
}
watch(
  githubBindings.data,
  (bindings) => {
    if (branchRepoId.value === null && bindings?.length) branchRepoId.value = bindings[0]!.repoId
  },
  { immediate: true },
)
async function createBranch() {
  if (branchRepoId.value === null) return
  creatingBranch.value = true
  try {
    await createGitHubBranch(props.slug, props.itemKey, branchRepoId.value)
    await client.invalidateQueries({ queryKey: [props.slug, props.itemKey, 'links'] })
  } finally {
    creatingBranch.value = false
  }
}
function logged(updated: TimeTrackingItem) {
  const current = currentItem.value
  if (!current) return
  client.setQueryData<WorkItem>([props.slug, props.itemKey], { ...current, ...updated })
  void invalidateLists(current)
}
</script>
<template>
  <div
    v-if="currentItem"
    class="grid w-full gap-0 lg:grid-cols-[minmax(0,1fr)_18rem] lg:gap-8"
    :class="modal ? 'p-5' : 'px-5 py-8'"
    @keydown.meta.s.prevent="save"
    @keydown.ctrl.s.prevent="save"
  >
    <!-- On a phone the metadata rail follows the long description and activity stream,
         so its actions need a compact copy at the top where they remain discoverable. -->
    <div
      class="bg-background sticky top-0 z-20 mb-5 flex min-h-13 min-w-0 shrink-0 items-center gap-2 py-2 lg:hidden"
    >
      <button
        class="border-input rounded border px-3"
        :disabled="saving || !isDirty"
        aria-label="Save item"
        title="Save"
        @click="save"
      >
        <Save class="size-4" /></button
      ><button
        class="bg-primary text-primary-foreground inline-flex items-center gap-1.5 rounded px-3 text-sm whitespace-nowrap disabled:opacity-50"
        :disabled="saving"
        @click="saveAndClose"
      >
        <Save class="size-4" /> Save &amp; close
      </button>
      <span
        v-if="isDirty"
        class="text-muted-foreground self-center text-xs"
        title="There are unsaved changes"
        >Unsaved</span
      >
      <button
        v-if="modal"
        type="button"
        class="text-muted-foreground hover:text-foreground hover:bg-muted mr-1 ml-auto inline-flex items-center justify-center rounded px-1.5"
        aria-label="Close item"
        title="Close (Esc)"
        @click="emit('close')"
      >
        <X class="size-5" />
      </button>
    </div>

    <section>
      <label class="sr-only" for="item-title">Title</label>
      <textarea
        id="item-title"
        ref="titleInput"
        v-model="title"
        rows="1"
        maxlength="500"
        class="hover:border-input focus:border-input -mx-2 block w-[calc(100%+1rem)] resize-none overflow-hidden rounded border border-transparent bg-transparent px-2 py-0.5 text-2xl font-semibold leading-tight outline-none"
        @input="onTitleInput"
        @keydown.enter.prevent="save"
      />
      <ClaimBanner
        v-if="currentItem.claimedBy"
        class="mt-4"
        :claimed-by="currentItem.claimedBy"
        :claimed-at="currentItem.claimedAt"
        :claim-heartbeat-at="currentItem.claimHeartbeatAt"
        :holder="holder"
        :current-user-id="session.user?.id ?? null"
        :project-role="project.data.value?.role ?? null"
        :releasing="releasing"
        :live-run="claimRun"
        :run-log-to="claimRunLogTo"
        @release="release"
      />
      <div class="mt-5">
        <label class="font-label">Description</label
        ><MarkdownEditor
          v-model="description"
          class="mt-2"
          :upload="uploadToDescription"
          :accept="acceptedFiles"
        />
      </div>
      <p v-if="conflict" class="text-destructive mt-3 text-sm">
        Someone changed this item. Reload to compare before overwriting.
      </p>
      <section v-if="currentItem.type !== 'epic'" class="border-border mt-8 rounded-md border p-3">
        <div class="flex items-center justify-between gap-3">
          <div>
            <p class="font-label">Subtasks</p>
            <p class="text-muted-foreground mt-1 text-xs">
              Tasks and other work directly under this item.
            </p>
          </div>
          <span v-if="!children.isPending.value" class="text-muted-foreground text-xs">{{
            childItems.length
          }}</span>
        </div>
        <p v-if="children.isPending.value" class="text-muted-foreground mt-3 text-sm">
          Loading subtasks…
        </p>
        <p v-else-if="children.isError.value" class="text-destructive mt-3 text-sm">
          Subtasks could not be loaded.
        </p>
        <p v-else-if="childItems.length === 0" class="text-muted-foreground mt-3 text-sm">
          No subtasks yet.
        </p>
        <div v-else class="mt-3 space-y-1">
          <button
            v-for="child in childItems"
            :key="child.id"
            type="button"
            class="hover:bg-muted flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm"
            @click="openChild(child.key)"
          >
            <span class="text-muted-foreground font-mono text-xs">{{ child.key }}</span
            ><span class="capitalize">{{ child.type }}</span
            ><span class="truncate font-medium">{{ child.title }}</span
            ><span
              v-if="child.estimateHours !== null || child.remainingHours !== null"
              class="text-muted-foreground ml-auto shrink-0 text-xs"
              :title="`Remaining ${child.remainingHours ?? 0}h of ${child.estimateHours ?? '-'}h estimated`"
              >{{ child.remainingHours ?? 0 }}h / {{ child.estimateHours ?? '-' }}h</span
            ><span
              class="text-muted-foreground shrink-0 capitalize text-xs"
              :class="child.estimateHours === null && child.remainingHours === null && 'ml-auto'"
              >{{ child.stateCategory }}</span
            >
          </button>
        </div>
      </section>
      <section class="border-border mt-8 rounded-md border p-3" data-testid="item-runs">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <p class="font-label">
              Agent runs<span
                v-if="!runsQuery.isPending.value && itemRuns.length"
                class="text-muted-foreground ml-1.5"
                >{{ itemRuns.length }}</span
              >
            </p>
            <p class="text-muted-foreground mt-1 text-xs">
              An AI agent takes the item, follows a playbook on a runner, and reports back here
              with its pull request.
            </p>
          </div>
          <Button
            v-if="startRun.visible"
            size="sm"
            :disabled="startRun.disabledReason !== null"
            :title="startRun.disabledReason ?? undefined"
            data-testid="start-run-button"
            data-tour="start-run-button"
            @click="startRunOpen = true"
          >
            <Bot aria-hidden="true" />
            Hand to agent
          </Button>
        </div>
        <p
          v-if="startRun.visible && startRun.disabledReason"
          class="text-muted-foreground mt-2 text-right text-xs"
        >
          {{ startRun.disabledReason }}
        </p>
        <p v-if="runsQuery.isPending.value" class="text-muted-foreground mt-3 text-sm">
          Loading runs…
        </p>
        <p v-else-if="itemRuns.length === 0" class="text-muted-foreground mt-3 text-sm">
          No agent has worked on this item yet.
        </p>
        <div v-else class="mt-3 space-y-1">
          <div
            v-for="entry in itemRuns"
            :key="entry.id"
            class="hover:bg-muted flex w-full flex-wrap items-center gap-x-2.5 gap-y-0.5 rounded px-2 py-1.5 text-left text-sm"
          >
            <RunStatusBadge :status="entry.status" />
            <span>{{ entry.agentName ?? 'Agent' }}</span>
            <span class="text-muted-foreground truncate text-xs">
              {{ entry.playbookName ?? '' }}
            </span>
            <span v-if="runRequesterLabel(entry)" class="text-muted-foreground shrink-0 text-xs italic">
              {{ runRequesterLabel(entry) }}
            </span>
            <span class="ml-auto flex shrink-0 items-center gap-3">
              <a
                v-if="entry.pullRequestUrl"
                :href="entry.pullRequestUrl"
                target="_blank"
                rel="noreferrer"
                class="text-muted-foreground hover:text-foreground text-xs underline underline-offset-2"
                >Pull request</a
              >
              <span
                v-if="runDuration(entry)"
                class="text-muted-foreground text-xs"
                >{{ runDuration(entry) }}</span
              >
              <RouterLink
                v-if="mayOpenRunLog"
                :to="factoryRunPath(props.slug, entry.id)"
                class="text-primary text-xs underline underline-offset-2"
                >Open log</RouterLink
              >
            </span>
          </div>
        </div>
      </section>
      <nav class="mt-8 flex gap-2 border-b">
        <button
          v-for="entry in ['comments', 'activity', 'relations']"
          :key="entry"
          class="px-3 py-2 text-sm capitalize"
          :class="tab === entry && 'border-primary border-b-2'"
          @click="tab = entry as typeof tab"
        >
          {{ entry }}
        </button>
      </nav>
      <section v-if="tab === 'comments'" class="mt-4 space-y-4">
        <form class="space-y-2" @submit.prevent="addComment">
          <MarkdownEditor
            ref="commentEditor"
            v-model="comment"
            compact
            placeholder="Add a comment - paste or drop images"
            :upload="uploadToComment"
            :accept="acceptedFiles"
          />
          <div class="flex justify-end">
            <button
              class="bg-primary text-primary-foreground rounded px-3 py-1.5 text-sm disabled:opacity-50"
              :disabled="sending || commentEditor?.uploading || !comment.trim()"
            >
              {{ commentEditor?.uploading ? 'Uploading…' : 'Send' }}
            </button>
          </div>
        </form>
        <article v-for="entry in commentItems" :key="entry.id" class="border-border border-b py-3">
          <div class="flex items-baseline gap-2">
            <strong class="text-sm">{{ entry.author.displayName }}</strong>
            <time
              class="text-muted-foreground text-xs"
              :datetime="entry.createdAt"
              :title="new Date(entry.createdAt).toLocaleString()"
              >{{ since(entry.createdAt) }}</time
            >
            <span
              v-if="entry.editedAt"
              class="text-muted-foreground text-xs"
              :title="`Edited ${new Date(entry.editedAt).toLocaleString()}`"
              >(edited)</span
            >
          </div>
          <Markdown :source="entry.bodyMarkdown" class="mt-1" />
        </article>
      </section>
      <section v-else-if="tab === 'activity'" class="mt-4 space-y-3">
        <div v-for="entry in historyItems" :key="entry.eventId" class="text-sm">
          <strong>{{ entry.actor?.displayName ?? 'Unknown' }}</strong> changed
          {{ entry.changes.map((change) => change.field).join(', ') }}
          <time
            class="text-muted-foreground ml-1 text-xs"
            :datetime="entry.at"
            :title="new Date(entry.at).toLocaleString()"
            >{{ since(entry.at) }}</time
          >
        </div>
      </section>
      <section v-else class="mt-4 space-y-4">
        <div
          v-for="relation in relationItems"
          :key="`${relation.kind}-${relation.targetKey}`"
          class="text-sm"
        >
          {{ relation.direction }} <strong>{{ relation.targetKey }}</strong> -
          {{ relation.targetTitle }}
        </div>
        <a
          v-for="link in linkItems"
          :key="link.id"
          :href="link.url"
          class="block text-sm underline"
          >{{ link.title ?? link.url }}</a
        >
      </section>
    </section>
    <aside class="mt-8 space-y-5 lg:mt-0">
      <div class="hidden h-9 shrink-0 gap-2 lg:flex">
        <button
          class="border-input rounded border px-3"
          :disabled="saving || !isDirty"
          aria-label="Save item"
          title="Save"
          @click="save"
        >
          <Save class="size-4" /></button
        ><button
          class="bg-primary text-primary-foreground inline-flex items-center gap-1.5 rounded px-3 text-sm disabled:opacity-50"
          :disabled="saving"
          @click="saveAndClose"
        >
          <Save class="size-4" /> Save &amp; close
        </button>
        <span
          v-if="isDirty"
          class="text-muted-foreground self-center text-xs"
          title="There are unsaved changes"
          >Unsaved</span
        >
        <button
          v-if="modal"
          type="button"
          class="text-muted-foreground hover:text-foreground hover:bg-muted -mr-1 ml-auto inline-flex items-center justify-center rounded px-1.5"
          aria-label="Close item"
          title="Close (Esc)"
          @click="emit('close')"
        >
          <X class="size-5" />
        </button>
      </div>
      <div class="rounded-md border p-3">
        <p class="font-label">{{ currentItem.key }}</p>
        <p class="mt-1 text-sm capitalize">
          {{ currentItem.type }} · {{ currentItem.stateCategory }}
        </p>
      </div>
      <div class="rounded-md border p-3">
        <label for="item-status" class="font-label">
          {{ board.data.value ? 'Board column / status' : 'Status' }}
        </label>
        <select
          id="item-status"
          v-model="selectedStateId"
          class="border-input bg-background mt-2 w-full rounded border px-2 py-1.5 text-sm disabled:opacity-50"
          :disabled="changingStatus || boardIsLoading || !statusOptions.length"
          @change="changeStatus"
        >
          <option v-for="state in statusOptions" :key="state.id" :value="state.id">
            {{ state.column ? `${state.column} - ${state.name}` : state.name }}
          </option>
        </select>
        <p v-if="changingStatus" class="text-muted-foreground mt-1 text-xs">Moving…</p>
        <p v-else-if="statusError" class="text-destructive mt-1 text-xs">{{ statusError }}</p>
      </div>
      <div class="rounded-md border p-3">
        <p class="font-label">Assigned to</p>
        <div class="mt-2 flex min-w-0 items-center gap-2">
          <UserAvatar
            v-if="currentItem.assigneeId"
            :name="membersById.get(currentItem.assigneeId)?.displayName ?? currentItem.assigneeId"
            :is-agent="membersById.get(currentItem.assigneeId)?.isAgent"
            :src="
              avatarUrl(currentItem.assigneeId, membersById.get(currentItem.assigneeId)?.avatarKey)
            "
            size="sm"
          />
          <label class="sr-only" for="item-assignee">Assignee</label>
          <select
            id="item-assignee"
            :value="currentItem.assigneeId ?? ''"
            class="border-input bg-background min-w-0 flex-1 rounded border px-2 py-1.5 text-sm disabled:opacity-50"
            :disabled="changingAssignee || projectMembers.isPending.value"
            @change="changeAssignee"
          >
            <option value="">Unassigned</option>
            <option
              v-for="member in projectMembers.data.value ?? []"
              :key="member.userId"
              :value="member.userId"
            >
              {{ member.displayName }}
            </option>
          </select>
        </div>
        <p v-if="changingAssignee" class="text-muted-foreground mt-1 text-xs">
          Updating assignment…
        </p>
        <p v-else-if="assigneeError" class="text-destructive mt-1 text-xs">{{ assigneeError }}</p>
      </div>
      <div
        v-if="
          commitItems.length ||
          pullRequestItems.length ||
          branchItems.length ||
          githubBindings.data.value?.length
        "
        class="rounded-md border p-3"
      >
        <p class="font-label">Development</p>
        <div v-if="githubBindings.data.value?.length" class="mt-2 flex gap-1">
          <select
            v-model="branchRepoId"
            class="border-input bg-background min-w-0 flex-1 rounded border px-1 text-xs"
            aria-label="Repository for new branch"
          >
            <option
              v-for="binding in githubBindings.data.value"
              :key="binding.repoId"
              :value="binding.repoId"
            >
              {{ binding.fullName }}
            </option></select
          ><button
            class="border rounded px-2 text-xs"
            :disabled="creatingBranch || branchRepoId === null"
            @click="createBranch"
          >
            {{ creatingBranch ? 'Creating…' : 'Create branch' }}
          </button>
        </div>
        <a
          v-for="branch in branchItems"
          :key="branch.id"
          :href="branch.url"
          target="_blank"
          rel="noreferrer"
          class="mt-2 block text-sm"
          ><GitBranch class="mr-1 inline size-3" />{{ branch.title ?? branch.externalId }}</a
        ><a
          v-for="pullRequest in pullRequestItems"
          :key="pullRequest.id"
          :href="pullRequest.url"
          target="_blank"
          rel="noreferrer"
          class="mt-2 block text-sm"
          ><span
            class="rounded px-1 text-xs"
            :class="
              pullRequest.state === 'merged'
                ? 'bg-violet-100 text-violet-800'
                : pullRequest.state === 'closed'
                  ? 'bg-muted text-muted-foreground'
                  : pullRequest.state === 'draft'
                    ? 'bg-amber-100 text-amber-800'
                    : 'bg-emerald-100 text-emerald-800'
            "
            >{{ pullRequest.state ?? 'open' }}</span
          >
          {{ pullRequest.title ?? pullRequest.url }}</a
        ><a
          v-for="commit in commitItems"
          :key="commit.id"
          :href="commit.url"
          target="_blank"
          rel="noreferrer"
          class="mt-2 block text-sm"
          ><code>{{ commit.externalId.slice(0, 7) }}</code> {{ commit.title
          }}<span class="block text-muted-foreground text-xs"
            >{{ commit.authorName ?? 'GitHub'
            }}<template v-if="commit.branch"> · {{ commit.branch }}</template> ·
            {{ new Date(commit.createdAt).toLocaleString() }}</span
          ></a
        >
      </div>
      <div class="rounded-md border p-3">
        <div class="flex items-center justify-between">
          <p class="font-label">Watchers ({{ currentItem.watcherCount }})</p>
          <button class="text-xs underline" @click="toggleWatch">
            {{ currentItem.isWatching ? 'Unwatch' : 'Watch' }}
          </button>
        </div>
        <p v-for="watcher in watcherItems" :key="watcher.user.id" class="mt-1 text-sm">
          {{ watcher.user.displayName }}
        </p>
      </div>
      <div class="rounded-md border p-3">
        <p class="font-label">Planning</p>
        <label for="item-priority" class="text-muted-foreground mt-2 block text-xs">Priority</label>
        <div class="mt-1 flex items-center gap-2">
          <PriorityIcon :priority="currentItem.priority" />
          <select
            id="item-priority"
            :value="currentItem.priority"
            class="border-input bg-background min-w-0 flex-1 rounded border px-2 py-1.5 text-sm disabled:opacity-50"
            :disabled="savingPlanning"
            @change="changePriority"
          >
            <option v-for="entry in priorities" :key="entry.value" :value="entry.value">
              {{ entry.label }}
            </option>
          </select>
        </div>
        <template v-if="usesPoints">
          <label for="item-points" class="text-muted-foreground mt-3 block text-xs">
            Story points
          </label>
          <input
            id="item-points"
            v-model="drafts.points"
            type="number"
            min="0"
            step="0.5"
            inputmode="decimal"
            placeholder="-"
            class="border-input bg-background mt-1 w-full rounded border px-2 py-1.5 text-sm disabled:opacity-50"
            :disabled="savingPlanning"
            @change="commitNumber('points')"
            @keydown.enter="($event.target as HTMLInputElement).blur()"
          />
        </template>
        <template v-if="usesHours">
          <div class="mt-3 grid grid-cols-3 gap-2">
            <div
              v-for="field in [
                { key: 'estimateHours', label: 'Estimate (h)' },
                { key: 'remainingHours', label: 'Remaining (h)' },
                { key: 'completedHours', label: 'Completed (h)' },
              ] as const"
              :key="field.key"
            >
              <label :for="`item-${field.key}`" class="text-muted-foreground block text-xs">
                {{ field.label }}
              </label>
              <input
                :id="`item-${field.key}`"
                v-model="drafts[field.key]"
                type="number"
                min="0"
                max="9999.99"
                step="0.25"
                inputmode="decimal"
                placeholder="-"
                class="border-input bg-background mt-1 w-full rounded border px-2 py-1.5 text-sm disabled:opacity-50"
                :disabled="savingPlanning"
                @change="commitNumber(field.key)"
                @keydown.enter="($event.target as HTMLInputElement).blur()"
              />
            </div>
          </div>
          <TimeTrackingPopover
            class="mt-3"
            :slug="slug"
            :item-key="currentItem.key"
            :version="currentItem.version"
            :remaining-hours="currentItem.remainingHours"
            :completed-hours="currentItem.completedHours"
            :disabled="savingPlanning"
            @logged="logged"
          />
        </template>
        <div v-if="currentItem.type === 'story'" class="text-muted-foreground mt-3 text-xs">
          <p>Hours from child tasks</p>
          <div class="mt-1 grid grid-cols-3 gap-2">
            <p>
              Estimate<strong class="text-foreground block text-sm"
                >{{ childHours.estimate }}h</strong
              >
            </p>
            <p>
              Remaining<strong class="text-foreground block text-sm"
                >{{ childHours.remaining }}h</strong
              >
            </p>
            <p>
              Completed<strong class="text-foreground block text-sm"
                >{{ childHours.completed }}h</strong
              >
            </p>
          </div>
        </div>
        <div
          v-if="currentItem.type === 'epic' || currentItem.type === 'feature'"
          class="text-muted-foreground mt-3 space-y-0.5 text-xs"
        >
          <p>Estimated from child work</p>
          <p>
            Points
            <strong class="text-foreground"
              >{{ currentItem.rollup.pointsCompleted }} /
              {{ currentItem.rollup.pointsTotal }}</strong
            >
          </p>
          <p>
            Remaining
            <strong class="text-foreground">{{ currentItem.rollup.remainingHours }}h</strong>
          </p>
        </div>
        <p v-if="savingPlanning" class="text-muted-foreground mt-2 text-xs">Saving…</p>
        <p v-else-if="planningError" class="text-destructive mt-2 text-xs">{{ planningError }}</p>
      </div>
      <div class="rounded-md border p-3">
        <p class="font-label">Copy</p>
        <div class="mt-2 flex flex-wrap gap-2">
          <button class="border rounded p-1.5" @click="copy(currentItem.key)">
            <Copy class="size-4" /></button
          ><button
            class="border rounded p-1.5"
            @click="copy(branchName(currentItem.key, currentItem.title))"
          >
            <GitBranch class="size-4" /></button
          ><button class="border rounded p-1.5" @click="copy(`${currentItem.key}: `)">
            <Check class="size-4" />
          </button>
        </div>
      </div>
      <div v-if="mayDelete" class="border-destructive/30 rounded-md border p-3">
        <button
          type="button"
          class="text-destructive hover:bg-destructive/10 inline-flex w-full items-center gap-2 rounded px-1.5 py-1 text-sm"
          @click="openDelete"
        >
          <Trash2 class="size-4" aria-hidden="true" /> Delete item
        </button>
      </div>
    </aside>
    <StartRunDialog
      v-model:open="startRunOpen"
      :slug="slug"
      :project-key="projectKey"
      :item-key="itemKey"
      @dispatched="onDispatched"
    />
    <DeleteConfirmDialog
      v-model:open="deleteOpen"
      :name="currentItem.key"
      :summary="
        doomedChildren.length === 0
          ? `Deleting removes ${currentItem.key}, along with:`
          : `Deleting removes ${currentItem.key} and the ${plural(doomedChildren.length, 'item', 'items')} below it, along with their:`
      "
      :consequences="deleteConsequences"
      :confirm-label="
        doomedChildren.length === 0 ? 'Delete item' : `Delete ${doomedChildren.length + 1} items`
      "
      :pending="deleting || deletePreview.isPending.value"
      :error="deleteError"
      @confirm="destroy"
    >
      <template v-if="doomedChildren.length > 0" #details>
        <div>
          <p class="mb-1.5 text-sm font-medium">Items below it that will be deleted</p>
          <ul class="max-h-48 overflow-auto rounded-md border py-1 text-sm">
            <li class="text-muted-foreground truncate px-3 py-0.5">
              {{ currentItem.key }} · {{ currentItem.title }}
            </li>
            <li
              v-for="child in doomedChildren"
              :key="child.key"
              class="truncate py-0.5 pr-3"
              :style="{ paddingLeft: `${0.75 + child.depth * 1}rem` }"
            >
              <span class="text-muted-foreground font-mono text-xs">{{ child.key }}</span>
              {{ child.title }}
            </li>
          </ul>
        </div>
      </template>
    </DeleteConfirmDialog>
    <Dialog :open="leavePrompt !== null" @update:open="(open) => !open && answerLeave(false)">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Save changes to {{ currentItem.key }}?</DialogTitle>
          <DialogDescription>
            {{
              comment.trim() && title.trim() === currentItem.title
                ? 'Your comment has not been sent. Discarding throws it away.'
                : 'The title or description has changes that are not saved yet.'
            }}
          </DialogDescription>
        </DialogHeader>
        <p v-if="leaveError" class="text-destructive text-sm">{{ leaveError }}</p>
        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="saving" @click="answerLeave(false)">
            Keep editing
          </Button>
          <Button type="button" variant="outline" :disabled="saving" @click="discardAndLeave">
            Discard
          </Button>
          <Button type="button" :disabled="saving" @click="saveAndLeave">
            {{ saving ? 'Saving…' : 'Save' }}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
  <p v-else class="p-8">Loading item…</p>
</template>
