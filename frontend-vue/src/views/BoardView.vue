<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  ArrowLeft,
  ArrowRight,
  ChevronDown,
  ChevronRight,
  Filter,
  GripVertical,
  Plus,
  Settings2,
  Trash2,
  TriangleAlert,
  X,
} from '@lucide/vue'

import { boardMove, getBoard, updateBoard, type Board, type BoardColumnConfig } from '@/api/boards'
import { listWorkflows } from '@/api/workflows'
import {
  createItem,
  editItem,
  listItemChildren,
  type WorkItem,
  type WorkItemType,
} from '@/api/items'
import { avatarUrl } from '@/api/profile'
import { listProjectMembers } from '@/api/projects'
import ItemTypeIcon from '@/components/common/ItemTypeIcon.vue'
import ClaimGlyph from '@/components/common/ClaimGlyph.vue'
import KeyChip from '@/components/common/KeyChip.vue'
import LabelChip from '@/components/common/LabelChip.vue'
import PriorityIcon from '@/components/common/PriorityIcon.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import UserSelect from '@/components/common/UserSelect.vue'
import ItemFilterBar from '@/components/items/ItemFilterBar.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { useItemModal } from '@/composables/useItemModal'
import { itemQueryError, useItemQueryParams } from '@/composables/useItemQueryParams'
import { useProjectRealtime } from '@/composables/useProjectRealtime'
import { useFocusTrap } from '@/composables/useFocusTrap'
import { useToast } from '@/composables/useToast'
import { vNearEnd } from '@/lib/nearEnd'
import { destinationIsAtWipLimit, moveBoardCard, unmappedStates } from '@/lib/board'
import { typeLabels } from '@/lib/hierarchy'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { ConflictError } from '@/utils/api'

const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const session = useSessionStore()
const teams = useTeamsStore()
const itemModal = useItemModal()
const client = useQueryClient()
const toast = useToast()
const { filter, search, update: updateQuery } = useItemQueryParams()
const assigneeFilterIds = ref<string[]>([])
const dragged = ref<WorkItem | null>(null)
const collapsed = ref(new Set<string>())
const dropTarget = ref<{ column: string; cardId?: string; placement?: 'before' | 'after' } | null>(
  null,
)
const moved = ref(new Set<string>())
const settingsOpen = ref(false)
const savingSettings = ref(false)
const pendingWip = ref<{
  card: WorkItem
  stateId: string
  columnId: string
  afterKey: string | null
} | null>(null)
const boardRoot = ref<HTMLElement | null>(null)
const settingsDialog = ref<HTMLElement | null>(null)
const focusedCardId = ref<string | null>(null)
const assigningCardIds = ref(new Set<string>())
const cardChildren = ref<Record<string, WorkItem[]>>({})
const expandedChildWork = ref(new Set<string>())
const loadingChildWork = ref(new Set<string>())
const subtaskDraft = ref<{ parent: WorkItem; title: string } | null>(null)
const creatingSubtask = ref(false)
const subtaskInput = ref<HTMLInputElement[]>([])

const slug = computed(() => organizations.currentSlug ?? '')
const projectKey = computed(() => projects.currentKey ?? '')
const teamId = computed(() => teams.currentId ?? '')
const enabled = computed(() => Boolean(slug.value && projectKey.value && teamId.value))
// Columns load a page of cards in rank order and grow as they are scrolled; the counts
// in each header stay the full totals the server computed.
const COLUMN_PAGE = 30
const columnTake = ref<Record<string, number>>({})
const key = computed(() => [
  'board',
  slug.value,
  projectKey.value,
  teamId.value,
  filter.value,
  search.value,
  [...assigneeFilterIds.value].sort().join(','),
  columnTake.value,
])
const boardQuery = useQuery({
  queryKey: key,
  enabled,
  queryFn: () =>
    getBoard(slug.value, teamId.value, {
      filter: filter.value,
      q: search.value,
      assigneeIds: assigneeFilterIds.value,
      take: COLUMN_PAGE,
      expand: columnTake.value,
    }),
  // Growing a column swaps the query key; keep the current cards on screen meanwhile.
  placeholderData: keepPreviousData,
})
const workflowQuery = useQuery({
  queryKey: computed(() => ['workflow', slug.value, projectKey.value]),
  enabled,
  queryFn: async () =>
    (await listWorkflows(slug.value, projectKey.value)).find((workflow) => workflow.isDefault),
})
const projectMembersQuery = useQuery({
  queryKey: computed(() => ['project-members', slug.value, projectKey.value]),
  enabled: computed(() => Boolean(slug.value && projectKey.value)),
  queryFn: () => listProjectMembers(slug.value, projectKey.value),
})
const board = computed(() => boardQuery.data.value)
const workflowStates = computed(() => workflowQuery.data.value?.states ?? [])
const projectMembers = computed(() => projectMembersQuery.data.value ?? [])
const membersById = computed(
  () => new Map(projectMembers.value.map((member) => [member.userId, member])),
)
// The assignee filter is applied by the server, so column counts and WIP stay exact.
const visibleBoard = board
const cards = computed(() => visibleBoard.value?.columns.flatMap((column) => column.cards) ?? [])
const generalStateCounts = computed(() => {
  const totals = { new: 0, doing: 0, done: 0 }
  for (const column of visibleBoard.value?.columns ?? []) totals[column.generalState] += column.count
  return totals
})

useFocusTrap(settingsDialog, settingsOpen)

// A remote move receives a brief visual cue after the query refetches; the hub still only
// invalidates data, so no pushed payload is ever rendered as authoritative work-item data.
useProjectRealtime(slug, projectKey, undefined, (event) => {
  moved.value = new Set([...moved.value, event.key])
  window.setTimeout(() => {
    const next = new Set(moved.value)
    next.delete(event.key)
    moved.value = next
  }, 900)
})

const queryError = computed(() => itemQueryError(boardQuery.error.value))

watch([filter, search, assigneeFilterIds, teamId], () => {
  columnTake.value = {}
})
function loadMore(columnId: string) {
  const column = board.value?.columns.find((entry) => entry.id === columnId)
  if (!column || column.cards.length >= column.count || boardQuery.isFetching.value) return
  columnTake.value = { ...columnTake.value, [columnId]: column.cards.length + COLUMN_PAGE }
}

function toggleColumn(name: string) {
  const next = new Set(collapsed.value)
  if (next.has(name)) next.delete(name)
  else next.add(name)
  collapsed.value = next
}
/** A story's own hours are always empty — its tasks carry them — so the card shows their sum. */
function cardHours(card: WorkItem) {
  return card.remainingHours ?? (card.rollup.totalCount ? card.rollup.remainingHours : null)
}
function openCard(card: WorkItem) {
  itemModal.open(card.key)
}
function lane(card: WorkItem) {
  if (board.value?.swimlane === 'assignee') return card.assigneeId ?? 'Unassigned'
  if (board.value?.swimlane === 'priority') return card.priority
  return ''
}
function lanes(cards: WorkItem[]) {
  if (!board.value?.swimlane || board.value.swimlane === 'none') return [{ name: '', cards }]
  return Object.entries(
    cards.reduce<Record<string, WorkItem[]>>((groups, card) => {
      const name = lane(card)
      ;(groups[name] ??= []).push(card)
      return groups
    }, {}),
  ).map(([name, grouped]) => ({ name, cards: grouped }))
}
function assigneeName(assigneeId: string | null) {
  return assigneeId ? (membersById.value.get(assigneeId)?.displayName ?? assigneeId) : 'Unassigned'
}
const isOnlyMine = computed(
  () => assigneeFilterIds.value.length === 1 && assigneeFilterIds.value[0] === session.user?.id,
)
function setAssigneeFilter(value: string | string[] | null) {
  assigneeFilterIds.value = Array.isArray(value) ? value : value ? [value] : []
}
function toggleOnlyMine() {
  const myId = session.user?.id
  if (!myId) return
  assigneeFilterIds.value = isOnlyMine.value ? [] : [myId]
}
function canAddSubtask(card: WorkItem) {
  return card.type === 'story' || card.type === 'bug'
}
function cardChildItems(card: WorkItem) {
  return cardChildren.value[card.id] ?? []
}
function toggleChildWork(card: WorkItem) {
  const next = new Set(expandedChildWork.value)
  if (next.has(card.id)) next.delete(card.id)
  else {
    next.add(card.id)
    if (!cardChildren.value[card.id]) void loadChildren(card)
  }
  expandedChildWork.value = next
}
// Subtasks are fetched per card, only once someone opens that card's list.
async function loadChildren(card: WorkItem) {
  if (loadingChildWork.value.has(card.id)) return
  loadingChildWork.value = new Set([...loadingChildWork.value, card.id])
  try {
    const children = await listItemChildren(slug.value, card.key)
    cardChildren.value = { ...cardChildren.value, [card.id]: children }
  } catch (error) {
    toast.error(error, `Subtasks of ${card.key} could not be loaded.`)
  } finally {
    const next = new Set(loadingChildWork.value)
    next.delete(card.id)
    loadingChildWork.value = next
  }
}
function isChildComplete(child: WorkItem) {
  return child.stateCategory === 'completed'
}
function startSubtask(parent: WorkItem) {
  subtaskDraft.value = { parent, title: '' }
}
async function createSubtask() {
  const draft = subtaskDraft.value
  if (!draft?.title.trim() || creatingSubtask.value) return
  creatingSubtask.value = true
  try {
    const created = await createItem(slug.value, projectKey.value, {
      type: 'task',
      title: draft.title.trim(),
      parentId: draft.parent.id,
      teamId: draft.parent.teamId ?? teamId.value,
    })
    cardChildren.value = {
      ...cardChildren.value,
      [draft.parent.id]: [...cardChildItems(draft.parent), created],
    }
    // Keep the lightweight composer open for a run of related tasks: type, Enter, type.
    subtaskDraft.value = { parent: draft.parent, title: '' }
    await nextTick()
    subtaskInput.value.at(-1)?.focus()
    await client.invalidateQueries({ queryKey: ['board', slug.value, projectKey.value, teamId.value] })
  } catch (error) {
    toast.error(error, 'The subtask could not be created.')
  } finally {
    creatingSubtask.value = false
  }
}


// A board refresh can change an open card's subtasks (realtime, a move elsewhere): refresh
// only the lists someone has open, never every card's.
watch(board, () => {
  const expanded = expandedChildWork.value
  if (!expanded.size) return
  for (const card of cards.value) if (expanded.has(card.id)) void loadChildren(card)
})

async function changeAssignee(card: WorkItem, event: Event) {
  const assigneeId = (event.target as HTMLSelectElement).value || null
  if (card.assigneeId === assigneeId || assigningCardIds.value.has(card.id)) return
  assigningCardIds.value = new Set([...assigningCardIds.value, card.id])
  try {
    const updated = await editItem(slug.value, card, { assigneeId })
    client.setQueryData<Board>(
      key.value,
      (current) =>
        current && {
          ...current,
          columns: current.columns.map((column) => ({
            ...column,
            cards: column.cards.map((entry) => (entry.id === updated.id ? updated : entry)),
          })),
        },
    )
    await client.invalidateQueries({
      queryKey: ['board', slug.value, projectKey.value, teamId.value],
    })
  } catch (error) {
    toast.error(error, 'The assignment could not be changed.')
  } finally {
    const next = new Set(assigningCardIds.value)
    next.delete(card.id)
    assigningCardIds.value = next
  }
}

async function move(
  card: WorkItem,
  stateId: string,
  columnId: string,
  afterKey?: string | null,
  force = false,
) {
  const current = board.value
  if (
    !current ||
    (card.stateId === stateId && card.boardColumnId === columnId && afterKey === undefined)
  )
    return
  const target = current.columns.find((column) => column.id === columnId)
  if (!target) return
  pendingWip.value = null
  const previous = current
  const optimistic = moveBoardCard(current, card, stateId, columnId, afterKey)
  client.setQueryData<Board>(key.value, optimistic)
  try {
    await boardMove(slug.value, card.key, {
      toStateId: stateId,
      toColumnId: columnId,
      afterKey,
      version: card.version,
      force,
    })
    // A card dropped after the last loaded card ranks ahead of the unloaded tail, so the
    // destination's page grows by one; otherwise the refetch would page it out of sight.
    const shown = optimistic.columns.find((column) => column.id === columnId)?.cards.length ?? 0
    if (shown > (columnTake.value[columnId] ?? COLUMN_PAGE))
      columnTake.value = { ...columnTake.value, [columnId]: shown }
    else await client.invalidateQueries({ queryKey: key.value })
  } catch (error) {
    client.setQueryData(key.value, previous)
    if (error instanceof ConflictError && destinationIsAtWipLimit(target, card) && !force) {
      pendingWip.value = { card, stateId, columnId, afterKey: afterKey ?? null }
      toast.info(
        'This column has reached its WIP limit.',
        'Ask a team lead to force the move if this work must enter now.',
      )
    } else toast.error(error, 'The card could not be moved.')
  }
}
function startDrag(event: DragEvent, card: WorkItem) {
  dragged.value = card
  event.dataTransfer?.setData('text/plain', card.id)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
}
function clearDrag() {
  dragged.value = null
  dropTarget.value = null
}
function setColumnDrop(column: string) {
  if (dragged.value) dropTarget.value = { column }
}
function setCardDrop(event: DragEvent, column: string, card: WorkItem) {
  if (!dragged.value || dragged.value.id === card.id) return
  const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect()
  dropTarget.value = {
    column,
    cardId: card.id,
    placement: event.clientY - bounds.top < bounds.height / 2 ? 'before' : 'after',
  }
}
function appendAfterKey(column: { cards: WorkItem[] }, cardId: string) {
  return column.cards.filter((entry) => entry.id !== cardId).at(-1)?.key ?? null
}
function afterKeyForDrop(
  column: { cards: WorkItem[] },
  card: WorkItem,
  target?: WorkItem,
  placement?: 'before' | 'after',
) {
  const destination = column.cards.filter((entry) => entry.id !== card.id)
  if (!target) return destination.at(-1)?.key ?? null
  const index = destination.findIndex((entry) => entry.id === target.id)
  return placement === 'before' ? (destination[index - 1]?.key ?? null) : target.key
}
function drop(
  column: { id: string; name: string; stateIds: string[]; cards: WorkItem[] },
  target?: WorkItem,
  placement?: 'before' | 'after',
) {
  const card = dragged.value
  clearDrag()
  if (!card || target?.id === card.id) return
  const stateId = target?.stateId ?? column.stateIds[0]
  if (!stateId) return
  void move(card, stateId, column.id, afterKeyForDrop(column, card, target, placement))
}
function keyboardMove(event: KeyboardEvent, card: WorkItem) {
  if (event.key === ' ') {
    event.preventDefault()
    openCard(card)
    return
  }
  if (
    event.key === 'ArrowDown' ||
    event.key === 'ArrowUp' ||
    event.key === 'Home' ||
    event.key === 'End'
  ) {
    const currentIndex = cards.value.findIndex((candidate) => candidate.id === card.id)
    if (currentIndex < 0) return
    const targetIndex =
      event.key === 'ArrowDown'
        ? currentIndex + 1
        : event.key === 'ArrowUp'
          ? currentIndex - 1
          : event.key === 'Home'
            ? 0
            : cards.value.length - 1
    const target = cards.value[Math.max(0, Math.min(targetIndex, cards.value.length - 1))]
    if (!target) return
    event.preventDefault()
    focusedCardId.value = target.id
    const buttons = boardRoot.value?.querySelectorAll<HTMLElement>('[data-board-card]')
    ;[...(buttons ?? [])].find((button) => button.dataset.boardCard === target.id)?.focus()
    return
  }
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
  const columns = board.value?.columns ?? []
  const index = columns.findIndex((column) => column.stateIds.includes(card.stateId))
  const offset = event.key === 'ArrowLeft' ? -1 : 1
  const destination = columns[index + offset]
  if (destination?.stateIds[0]) {
    event.preventDefault()
    const stateId = destination.stateIds[0]
    void move(card, stateId, destination.id, appendAfterKey(destination, card.id))
  }
}

const settings = ref<{ columns: BoardColumnConfig[]; swimlane: string; cardFields: string[] }>({
  columns: [],
  swimlane: 'none',
  cardFields: [],
})
function editSettings() {
  const current = board.value
  if (!current) return
  settings.value = {
    columns: current.columns.map((column) => ({
      id: column.id,
      name: column.name,
      stateIds: [...column.stateIds],
      generalState: column.generalState,
      wipLimit: column.wipLimit,
    })),
    swimlane: current.swimlane,
    cardFields: [...current.cardFields],
  }
  settingsOpen.value = true
}
async function saveSettings() {
  const current = board.value
  if (!current) return
  savingSettings.value = true
  try {
    await updateBoard(slug.value, teamId.value, {
      ...settings.value,
      columns: settings.value.columns.map((column) => ({
        ...column,
        wipLimit: column.wipLimit || null,
      })),
      version: current.version,
    })
    settingsOpen.value = false
    await boardQuery.refetch()
    toast.success('Board settings saved.')
  } catch (error) {
    toast.error(error, 'Board settings could not be saved.')
  } finally {
    savingSettings.value = false
  }
}
function addColumn() {
  const state = unmappedStates(settings.value.columns, workflowStates.value)[0]
  settings.value.columns.push({
    id: crypto.randomUUID(),
    name: state?.name ?? 'New column',
    stateIds: state ? [state.id] : [],
    generalState: 'doing',
    wipLimit: null,
  })
}
function removeColumn(index: number) {
  settings.value.columns.splice(index, 1)
}
function shiftColumn(index: number, offset: -1 | 1) {
  const columns = settings.value.columns
  const target = index + offset
  if (target < 0 || target >= columns.length) return
  ;[columns[index], columns[target]] = [columns[target]!, columns[index]!]
}
const unmapped = computed(() => unmappedStates(settings.value.columns, workflowStates.value))
/** States can be shown by more than one column, so every column gets the full workflow picker. */
function statesFor(_column: BoardColumnConfig) {
  return workflowStates.value
}

const vFocus = { mounted: (el: HTMLElement) => el.focus() }
// Adding from a column creates the item already in that column's first state.
const draft = ref<{ column: string; type: WorkItemType; title: string } | null>(null)
const creatingCard = ref(false)
function startDraft(column: string) {
  draft.value = { column, type: board.value?.types[0] ?? 'story', title: '' }
  const next = new Set(collapsed.value)
  next.delete(column)
  collapsed.value = next
}
async function submitDraft(column: { name: string; stateIds: string[] }) {
  const current = draft.value
  if (!current?.title.trim() || creatingCard.value) return
  creatingCard.value = true
  try {
    const created = await createItem(slug.value, projectKey.value, {
      type: current.type,
      title: current.title.trim(),
      teamId: teamId.value,
      stateId: column.stateIds[0],
    })
    current.title = ''
    toast.success(`${created.key} created.`)
    await client.invalidateQueries({
      queryKey: ['board', slug.value, projectKey.value, teamId.value],
    })
  } catch (error) {
    toast.error(error, 'The item could not be created.')
  } finally {
    creatingCard.value = false
  }
}

watch(
  [slug, projectKey],
  () => {
    if (projects.status === 'unknown') void projects.load()
    if (teams.status === 'unknown') void teams.load()
  },
  { immediate: true },
)
</script>

<template>
  <AppShell>
    <main class="flex h-full min-h-0 flex-col gap-4 p-5">
      <header data-tour="board-filter" class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold">{{ teams.current?.name ?? 'Board' }}</h1>
          <p class="text-muted-foreground text-sm">
            {{ projects.current?.name ?? 'Select a project and team to view its board.' }}
          </p>
        </div>
        <div class="flex flex-wrap items-center justify-end gap-2">
          <div
            v-if="board"
            class="text-muted-foreground flex items-center gap-1.5 text-xs"
            aria-label="Work by general state"
          >
            <span
              v-for="(count, state) in generalStateCounts"
              :key="state"
              class="border-border rounded border px-2 py-1 capitalize"
              >{{ state }} {{ count }}</span
            >
          </div>
          <ItemFilterBar
            class="w-full sm:w-auto sm:min-w-[28rem]"
            :filter="filter"
            :search="search"
            :error="queryError"
            @update:filter="updateQuery({ f: $event })"
            @update:search="updateQuery({ q: $event })"
          />
          <div class="flex items-center gap-1.5">
            <Filter class="text-muted-foreground size-4" aria-hidden="true" />
            <button
              type="button"
              class="border-input bg-background h-8 rounded-lg border px-2.5 text-sm whitespace-nowrap"
              :class="isOnlyMine && 'bg-primary text-primary-foreground border-primary'"
              :disabled="!session.user"
              :aria-pressed="isOnlyMine"
              @click="toggleOnlyMine"
            >
              Only mine
            </button>
            <UserSelect
              :model-value="assigneeFilterIds"
              :options="projectMembers"
              multiple
              class="w-52"
              label="Filter by assignee"
              placeholder="All assignees"
              search-placeholder="Find assignees…"
              :loading="projectMembersQuery.isPending.value"
              @update:model-value="setAssigneeFilter"
            />
          </div>
          <button
            class="border-input inline-flex items-center gap-2 rounded border px-3 text-sm"
            :disabled="!board"
            @click="editSettings"
          >
            <Settings2 class="size-4" /> Board settings
          </button>
        </div>
      </header>
      <p v-if="boardQuery.isError.value && !queryError" class="text-destructive">
        The board could not be loaded.
      </p>
      <div
        v-else-if="board"
        ref="boardRoot"
        class="flex min-h-0 flex-1 gap-3 overflow-x-auto pb-2"
        aria-label="Kanban board"
        role="region"
      >
        <section
          v-for="column in visibleBoard?.columns ?? []"
          :key="column.name"
          :aria-label="column.name"
          class="bg-muted/30 border-border flex flex-none flex-col rounded-lg border transition-colors"
          :class="[
            collapsed.has(column.name) ? 'w-12' : 'w-72',
            dropTarget?.column === column.name &&
              'border-primary bg-primary/5 ring-primary/30 ring-2',
          ]"
          @dragover.prevent="setColumnDrop(column.name)"
          @drop.prevent="drop(column)"
        >
          <header
            class="border-border flex items-center gap-2 border-b px-3 py-2"
            :class="collapsed.has(column.name) && 'flex-col border-b-0 px-1'"
          >
            <button
              class="text-muted-foreground"
              :aria-label="`Toggle ${column.name}`"
              @click="toggleColumn(column.name)"
            >
              <ChevronRight v-if="collapsed.has(column.name)" class="size-4" /><ChevronDown
                v-else
                class="size-4"
              />
            </button>
            <strong
              class="truncate text-sm"
              :class="collapsed.has(column.name) && '[writing-mode:vertical-rl]'"
              >{{ column.name }}</strong
            >
            <span
              v-if="!collapsed.has(column.name)"
              class="text-muted-foreground rounded bg-background/60 px-1.5 py-0.5 text-[10px] font-medium uppercase"
              >{{ column.generalState }}</span
            >
            <span
              class="text-muted-foreground text-xs"
              :class="!collapsed.has(column.name) && 'ml-auto'"
              >{{ column.count
              }}<template v-if="column.wipLimit">/{{ column.wipLimit }}</template></span
            >
            <TriangleAlert
              v-if="column.wipExceeded"
              class="text-warning size-4"
              aria-label="WIP limit exceeded"
            />
            <button
              v-if="!collapsed.has(column.name)"
              class="text-muted-foreground hover:text-foreground hover:bg-background rounded p-0.5"
              :aria-label="`Add item to ${column.name}`"
              @click="startDraft(column.name)"
            >
              <Plus class="size-4" />
            </button>
          </header>
          <div
            v-if="!collapsed.has(column.name)"
            class="min-h-24 flex-1 space-y-3 overflow-y-auto p-2"
          >
            <form
              v-if="draft?.column === column.name"
              class="bg-background border-primary/50 space-y-2 rounded-md border p-2 shadow-sm"
              :aria-label="`New item in ${column.name}`"
              @submit.prevent="submitDraft(column)"
              @keydown.esc="draft = null"
            >
              <textarea
                v-model="draft.title"
                v-focus
                rows="2"
                maxlength="500"
                class="border-input bg-background w-full resize-none rounded border px-2 py-1.5 text-sm"
                placeholder="What needs doing?"
                @keydown.enter.exact.prevent="submitDraft(column)"
              />
              <div class="flex items-center gap-2">
                <select
                  v-model="draft.type"
                  class="border-input bg-background h-8 rounded border px-2 text-sm"
                  aria-label="Item type"
                >
                  <option v-for="type in board.types" :key="type" :value="type">
                    {{ typeLabels[type] }}
                  </option>
                </select>
                <button
                  class="bg-primary text-primary-foreground ml-auto h-8 rounded px-3 text-sm disabled:opacity-50"
                  :disabled="creatingCard || !draft.title.trim()"
                >
                  Add
                </button>
                <button
                  type="button"
                  class="text-muted-foreground hover:text-foreground rounded p-1"
                  aria-label="Cancel"
                  @click="draft = null"
                >
                  <X class="size-4" />
                </button>
              </div>
            </form>
            <section
              v-for="group in lanes(column.cards)"
              :key="group.name || 'all'"
              class="space-y-2"
            >
              <p
                v-if="group.name"
                class="text-muted-foreground px-1 text-xs font-medium capitalize"
              >
                {{ group.name }}
              </p>
              <article
                v-for="card in group.cards"
                :key="card.id"
                :data-board-card="card.id"
                :tabindex="(focusedCardId ?? cards[0]?.id) === card.id ? 0 : -1"
                draggable="true"
                role="button"
                class="bg-background border-border hover:border-primary/50 w-full rounded-md border p-3 text-left shadow-sm transition"
                :class="[
                  card.type === 'bug'
                    ? 'border-l-4 border-l-red-500'
                    : card.type === 'story'
                      ? 'border-l-4 border-l-success'
                      : '',
                  moved.has(card.key) && 'ring-primary animate-pulse ring-1',
                  dropTarget?.cardId === card.id &&
                    (dropTarget.placement === 'before'
                      ? 'border-t-primary ring-primary/50 -translate-y-0.5 ring-2'
                      : 'border-b-primary translate-y-0.5 ring-primary/50 ring-2'),
                ]"
                @dragstart="startDrag($event, card)"
                @dragend="clearDrag"
                @dragover.stop.prevent="setCardDrop($event, column.name, card)"
                @drop.stop.prevent="
                  drop(
                    column,
                    card,
                    dropTarget?.cardId === card.id ? dropTarget.placement : undefined,
                  )
                "
                @focus="focusedCardId = card.id"
                @click="openCard(card)"
                @keydown.self="keyboardMove($event, card)"
              >
                <div class="flex items-start gap-2">
                  <GripVertical class="text-muted-foreground mt-0.5 size-4" />
                  <div class="min-w-0 flex-1">
                    <div class="flex items-center gap-2">
                      <ItemTypeIcon :type="card.type" /><KeyChip :label="card.key" /><PriorityIcon
                        :priority="card.priority"
                      /><span v-if="card.blocked" class="text-destructive text-xs">Blocked</span
                      ><ClaimGlyph
                        class="ml-auto"
                        :claimed-by="card.claimedBy"
                        :claim-heartbeat-at="card.claimHeartbeatAt"
                      />
                    </div>
                    <p class="mt-2 text-sm font-medium">{{ card.title }}</p>
                    <div class="mt-2 flex flex-wrap items-center gap-1.5">
                      <LabelChip
                        v-for="label in card.labels"
                        :key="label.id"
                        :name="label.name"
                        :color="label.color"
                        :group="label.group"
                      /><span
                        v-if="board.cardFields.includes('points') && card.points != null"
                        class="text-muted-foreground text-xs"
                        >{{ card.points }} pt</span
                      ><span
                        v-if="board.cardFields.includes('hours') && cardHours(card) != null"
                        class="text-muted-foreground text-xs"
                        >{{ cardHours(card) }}h</span
                      >
                      <div class="ml-auto flex min-w-0 items-center gap-1">
                        <UserAvatar
                          v-if="card.assigneeId"
                          :name="assigneeName(card.assigneeId)"
                          :is-agent="membersById.get(card.assigneeId)?.isAgent"
                          :src="
                            avatarUrl(card.assigneeId, membersById.get(card.assigneeId)?.avatarKey)
                          "
                          size="sm"
                        /><label class="sr-only" :for="`assignee-${card.id}`"
                          >Assignee for {{ card.key }}</label
                        ><select
                          :id="`assignee-${card.id}`"
                          :value="card.assigneeId ?? ''"
                          class="border-input bg-background max-w-32 rounded border px-1 py-0.5 text-xs"
                          :disabled="
                            assigningCardIds.has(card.id) || projectMembersQuery.isPending.value
                          "
                          @click.stop
                          @mousedown.stop
                          @keydown.stop
                          @change.stop="changeAssignee(card, $event)"
                        >
                          <option value="">Unassigned</option>
                          <option
                            v-for="member in projectMembers"
                            :key="member.userId"
                            :value="member.userId"
                          >
                            {{ member.displayName }}
                          </option>
                        </select>
                      </div>
                    </div>
                    <div
                      v-if="canAddSubtask(card)"
                      class="border-border mt-3 border-t pt-2"
                      @click.stop
                    >
                      <div class="flex items-center gap-1">
                        <button
                          type="button"
                          class="text-muted-foreground hover:text-foreground flex min-w-0 flex-1 items-center gap-1 text-xs font-medium"
                          :aria-expanded="expandedChildWork.has(card.id)"
                          :aria-controls="`subtasks-${card.id}`"
                          @click.stop="toggleChildWork(card)"
                        >
                          <ChevronRight v-if="!expandedChildWork.has(card.id)" class="size-3" />
                          <ChevronDown v-else class="size-3" />
                          Subtasks {{ card.rollup.completedCount }}/{{ card.rollup.totalCount }}
                        </button>
                        <button
                          v-if="expandedChildWork.has(card.id)"
                          type="button"
                          class="text-muted-foreground hover:text-foreground hover:bg-muted rounded p-0.5"
                          :aria-label="`Add subtask to ${card.key}`"
                          title="Add subtask"
                          @click.stop="startSubtask(card)"
                        >
                          <Plus class="size-3" />
                        </button>
                      </div>
                      <ul
                        v-if="expandedChildWork.has(card.id)"
                        :id="`subtasks-${card.id}`"
                        class="mt-1 space-y-0.5 pl-4"
                      >
                        <li
                          v-if="loadingChildWork.has(card.id) && !cardChildren[card.id]"
                          class="text-muted-foreground px-1 text-xs"
                        >
                          Loading…
                        </li>
                        <li v-for="child in cardChildItems(card)" :key="child.id">
                          <button
                            type="button"
                            class="hover:bg-muted flex w-full rounded px-1 py-0.5 text-left text-xs"
                            :class="isChildComplete(child) && 'text-muted-foreground line-through'"
                            @click.stop="openCard(child)"
                          >
                            {{ child.title }}
                          </button>
                        </li>
                      </ul>
                      <form
                        v-if="expandedChildWork.has(card.id) && subtaskDraft?.parent.id === card.id"
                        class="mt-2 flex gap-1"
                        @submit.stop.prevent="createSubtask"
                      >
                        <label class="sr-only" :for="`subtask-title-${card.id}`">Subtask title</label>
                        <input
                          :id="`subtask-title-${card.id}`"
                          ref="subtaskInput"
                          v-model="subtaskDraft.title"
                          autofocus
                          maxlength="500"
                          class="border-input bg-background min-w-0 flex-1 rounded border px-2 py-1 text-xs"
                          placeholder="Subtask title"
                          @keydown.stop
                        />
                        <button
                          class="bg-primary text-primary-foreground rounded px-2 text-xs disabled:opacity-50"
                          :disabled="creatingSubtask || !subtaskDraft.title.trim()"
                        >
                          Add
                        </button>
                        <button
                          type="button"
                          class="text-muted-foreground hover:text-foreground px-1 text-xs"
                          aria-label="Cancel subtask"
                          @click="subtaskDraft = null"
                        >
                          <X class="size-3" />
                        </button>
                      </form>
                    </div>
                  </div>
                </div>
              </article>
            </section>
            <div
              v-if="column.cards.length < column.count"
              :key="`more-${column.id}-${column.cards.length}`"
              v-near-end="() => loadMore(column.id)"
              class="text-muted-foreground px-2 py-1.5 text-center text-xs"
            >
              <button
                type="button"
                class="hover:text-foreground"
                :disabled="boardQuery.isFetching.value"
                @click="loadMore(column.id)"
              >
                {{
                  boardQuery.isFetching.value
                    ? 'Loading…'
                    : `Show more (${column.count - column.cards.length} more)`
                }}
              </button>
            </div>
            <button
              v-if="draft?.column !== column.name"
              class="text-muted-foreground hover:text-foreground hover:bg-background/60 flex w-full items-center gap-1.5 rounded px-2 py-1.5 text-sm"
              @click="startDraft(column.name)"
            >
              <Plus class="size-4" /> Add item
            </button>
          </div>
        </section>
        <button
          class="border-border text-muted-foreground hover:text-foreground hover:bg-muted/30 flex w-12 flex-none items-start justify-center rounded-lg border border-dashed pt-3"
          aria-label="Add column"
          title="Add column"
          @click="editSettings(); addColumn()"
        >
          <Plus class="size-4" />
        </button>
      </div>
      <p v-else class="text-muted-foreground py-12 text-center">Loading board…</p>
    </main>

    <aside
      v-if="pendingWip"
      class="bg-background border-warning fixed right-5 bottom-5 z-50 max-w-sm rounded-lg border p-4 shadow-xl"
    >
      <button class="absolute right-2 top-2" @click="pendingWip = null">
        <X class="size-4" />
      </button>
      <p class="font-medium">WIP limit reached</p>
      <p class="text-muted-foreground mt-1 text-sm">
        {{ pendingWip.card.key }} was returned to its original column.
      </p>
      <button
        class="bg-primary text-primary-foreground mt-3 rounded px-3 py-2 text-sm"
        @click="
          move(
            pendingWip!.card,
            pendingWip!.stateId,
            pendingWip!.columnId,
            pendingWip!.afterKey,
            true,
          )
        "
      >
        Force move
      </button>
    </aside>

    <div
      v-if="settingsOpen"
      class="bg-background/80 fixed inset-0 z-50 grid place-items-center p-4"
    >
      <form
        ref="settingsDialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="board-settings-title"
        tabindex="-1"
        class="bg-background border-border max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-lg border p-5 shadow-xl"
        @keydown.esc.prevent="settingsOpen = false"
        @submit.prevent="saveSettings"
      >
        <div class="flex items-center justify-between">
          <h2 id="board-settings-title" class="text-lg font-semibold">Board settings</h2>
          <button
            data-autofocus
            type="button"
            aria-label="Close board settings"
            @click="settingsOpen = false"
          >
            <X class="size-5" aria-hidden="true" />
          </button>
        </div>
        <label class="mt-4 block text-sm"
          >Swimlanes<select
            v-model="settings.swimlane"
            class="border-input mt-1 block w-full rounded border p-2"
          >
            <option value="none">None</option>
            <option value="assignee">Assignee</option>
            <option value="priority">Priority</option>
            <option value="epic">Epic</option>
            <option value="feature">Feature</option>
          </select></label
        >
        <fieldset class="mt-4">
          <legend class="text-sm">Card fields</legend>
          <label
            v-for="field in ['priority', 'labels', 'points', 'hours']"
            :key="field"
            class="mr-4 inline-flex gap-1 text-sm"
            ><input v-model="settings.cardFields" type="checkbox" :value="field" />{{
              field
            }}</label
          >
        </fieldset>
        <div class="mt-5 flex items-center justify-between">
          <h3 class="text-sm font-medium">Columns</h3>
          <button
            type="button"
            class="border-input inline-flex items-center gap-1.5 rounded border px-2 py-1 text-sm"
            @click="addColumn"
          >
            <Plus class="size-4" /> Add column
          </button>
        </div>
        <p v-if="unmapped.length" class="text-warning mt-2 text-xs">
          Not on the board: {{ unmapped.map((state) => state.name).join(', ') }}. Cards in these
          states stay hidden until a column shows them.
        </p>
        <p class="text-muted-foreground mt-1 text-xs">
          Each column shows one or more workflow states. Need a new state? Add it in project
          settings → Workflow.
        </p>
        <section
          v-for="(column, index) in settings.columns"
          :key="index"
          class="border-border mt-3 rounded border p-3"
          :aria-label="`Column ${index + 1}`"
        >
          <div class="flex gap-2">
            <input
              v-model="column.name"
              class="border-input flex-1 rounded border p-2 text-sm"
              placeholder="Column name"
              aria-label="Column name"
            />
            <select
              v-model="column.generalState"
              class="border-input rounded border p-2 text-sm"
              aria-label="General state"
            >
              <option value="new">New</option>
              <option value="doing">Doing</option>
              <option value="done">Done</option>
            </select>
            <input
              v-model.number="column.wipLimit"
              type="number"
              min="1"
              class="border-input w-20 rounded border p-2 text-sm"
              placeholder="WIP"
              aria-label="WIP limit"
            />
            <button
              type="button"
              class="text-muted-foreground hover:text-foreground rounded p-2 disabled:opacity-30"
              :disabled="index === 0"
              aria-label="Move column left"
              @click="shiftColumn(index, -1)"
            >
              <ArrowLeft class="size-4" />
            </button>
            <button
              type="button"
              class="text-muted-foreground hover:text-foreground rounded p-2 disabled:opacity-30"
              :disabled="index === settings.columns.length - 1"
              aria-label="Move column right"
              @click="shiftColumn(index, 1)"
            >
              <ArrowRight class="size-4" />
            </button>
            <button
              type="button"
              class="text-muted-foreground hover:text-destructive rounded p-2 disabled:opacity-30"
              :disabled="settings.columns.length === 1"
              aria-label="Remove column"
              @click="removeColumn(index)"
            >
              <Trash2 class="size-4" />
            </button>
          </div>
          <fieldset class="mt-2 flex flex-wrap gap-x-4 gap-y-1">
            <legend class="sr-only">States</legend>
            <label
              v-for="state in statesFor(column)"
              :key="state.id"
              class="inline-flex items-center gap-1 text-sm"
              ><input v-model="column.stateIds" type="checkbox" :value="state.id" />{{
                state.name
              }}</label
            >
          </fieldset>
          <p v-if="!column.stateIds.length" class="text-destructive mt-1 text-xs">
            Pick at least one state.
          </p>
        </section>
        <button
          class="bg-primary text-primary-foreground mt-5 rounded px-4 py-2 text-sm disabled:opacity-50"
          :disabled="
            savingSettings ||
            settings.columns.some((column) => !column.name.trim() || !column.stateIds.length)
          "
        >
          Save board
        </button>
      </form>
    </div>
  </AppShell>
</template>
