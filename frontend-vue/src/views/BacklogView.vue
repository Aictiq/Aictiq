<script setup lang="ts">
import { ChevronDown, ChevronRight, GripVertical, Plus, X } from '@lucide/vue'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, nextTick, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import { createItem, getTeamBacklog, moveItem, type BacklogSection, type WorkItem, type WorkItemType } from '@/api/items'
import { getSprintCapacity, getTeamVelocity, listSprints, type Sprint } from '@/api/sprints'
import ItemTypeIcon from '@/components/common/ItemTypeIcon.vue'
import ItemFilterBar from '@/components/items/ItemFilterBar.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { flattenBacklog, rankMoveForDrop } from '@/lib/backlog'
import { vNearEnd } from '@/lib/nearEnd'
import { allowsParent, childTypes, requiresParent, typeLabels } from '@/lib/hierarchy'
import { useItemModal } from '@/composables/useItemModal'
import { itemQueryError, useItemQueryParams } from '@/composables/useItemQueryParams'
import { useShortcut } from '@/composables/useShortcuts'
import { useToast } from '@/composables/useToast'
import { useProjectRealtime } from '@/composables/useProjectRealtime'
import { ConflictError } from '@/utils/api'

const props = defineProps<{ slug: string; projectKey: string; teamId: string }>()
const toast = useToast()
const client = useQueryClient()
useProjectRealtime(() => props.slug, () => props.projectKey)
const collapsed = ref(new Set<string>())
const selected = ref(new Set<string>())
const dragging = ref<Set<string> | null>(null)

const SECTION_PAGE = 50
// The server's per-section cap (MaxBacklogTake).
const SECTION_MAX = 2000
const { filter, search, update } = useItemQueryParams()
const filtered = computed(() => Boolean(filter.value || search.value))
const sectionTake = ref<Partial<Record<BacklogSection, number>>>({})
watch([() => props.teamId, filter, search], () => { sectionTake.value = {} })
const backlog = useQuery({
  queryKey: computed(() => [props.slug, props.teamId, 'items', 'backlog', filter.value, search.value, sectionTake.value]),
  queryFn: () => getTeamBacklog(props.slug, props.teamId, { filter: filter.value, q: search.value, take: SECTION_PAGE, expand: sectionTake.value }),
  // Growing a section swaps the query key; keep the current rows on screen meanwhile.
  placeholderData: keepPreviousData,
})
const queryError = computed(() => itemQueryError(backlog.error.value))
const sprints = useQuery({
  queryKey: computed(() => [props.slug, props.teamId, 'sprint', 'sprints']),
  queryFn: () => listSprints(props.slug, props.teamId),
})
const sprintList = computed(() => sprints.data.value ?? [])
const velocity = useQuery({
  queryKey: computed(() => [props.slug, props.teamId, 'velocity']),
  queryFn: () => getTeamVelocity(props.slug, props.teamId),
})
const velocityData = computed(() => velocity.data.value)
const activeSprint = computed(() => sprintList.value.find((s) => s.state === 'active') ?? null)
// The same choice the server makes for its "next sprint" section: the earliest planned one.
const nextSprint = computed(() =>
  sprintList.value.filter((s) => s.state === 'planned').sort((a, b) => a.startsOn.localeCompare(b.startsOn))[0] ?? null,
)
const capacity = useQuery({
  queryKey: computed(() => [props.slug, activeSprint.value?.id, 'capacity']),
  queryFn: () => getSprintCapacity(props.slug, activeSprint.value!.id),
  enabled: computed(() => activeSprint.value !== null),
})

interface Section { id: BacklogSection; title: string; hint: string; sprint: Sprint | null; items: WorkItem[]; count: number }
const sections = computed<Section[]>(() => {
  const page = backlog.data.value
  if (!page) return []
  const result: Section[] = []
  if (activeSprint.value) result.push({ id: 'current', title: activeSprint.value.name, hint: 'Current sprint', sprint: activeSprint.value, items: page.currentSprint, count: page.currentSprintCount })
  if (nextSprint.value) result.push({ id: 'next', title: nextSprint.value.name, hint: 'Next sprint', sprint: nextSprint.value, items: page.nextSprint, count: page.nextSprintCount })
  result.push({ id: 'backlog', title: 'Backlog', hint: 'Not planned into a sprint', sprint: null, items: page.backlog, count: page.backlogCount })
  return result
})
const allItems = computed(() => sections.value.flatMap((section) => section.items))
const capacityPercent = computed(() => {
  const data = capacity.data.value
  return data?.capacityHours ? Math.round((data.assignedRemainingHours / data.capacityHours) * 100) : 0
})

const hasMore = (section: Section) => section.items.length < Math.min(section.count, SECTION_MAX)
function loadMore(section: Section) {
  if (!hasMore(section) || backlog.isFetching.value) return
  sectionTake.value = { ...sectionTake.value, [section.id]: Math.min(section.items.length + SECTION_PAGE, SECTION_MAX) }
}

function invalidate() {
  return Promise.all([
    client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'items', 'backlog'] }),
    client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'sprint', 'sprints'] }),
    client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'velocity'] }),
  ])
}
function toggleCollapsed(id: string) {
  const next = new Set(collapsed.value)
  if (next.has(id)) next.delete(id); else next.add(id)
  collapsed.value = next
}
function toggleSelection(key: string, event: MouseEvent) {
  const next = event.metaKey || event.ctrlKey ? new Set(selected.value) : new Set<string>()
  if (next.has(key)) next.delete(key)
  else next.add(key)
  selected.value = next
}
function startDrag(item: WorkItem) {
  dragging.value = selected.value.has(item.key) ? new Set(selected.value) : new Set([item.key])
}
async function dropOn(target: WorkItem | null, section: Section, reparent = false) {
  const keys = dragging.value
  dragging.value = null
  if (!keys?.size) return
  const rank = rankMoveForDrop(section.items, keys, target?.key ?? null)
  try {
    // Move one at a time so each request carries the fresh version returned by the prior
    // one. The server's rank lock makes the sequence atomic with respect to other moves.
    for (const key of keys) {
      const item = allItems.value.find((entry) => entry.key === key)
      if (!item) continue
      await moveItem(props.slug, key, {
        ...rank, teamId: props.teamId, sprintId: section.sprint?.id, removeSprint: section.sprint === null && item.sprintId !== null,
        parentKey: reparent ? target?.key : undefined, version: item.version,
      })
    }
    selected.value = new Set()
  } catch (error) {
    if (error instanceof ConflictError) toast.info('The backlog changed. Your move was not applied; showing the latest order.')
    else toast.error(error, 'The backlog move could not be saved.')
  } finally {
    await invalidate()
  }
}
function keyboardReorder(event: KeyboardEvent, item: WorkItem, section: Section) {
  if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return
  event.preventDefault()
  const index = section.items.findIndex((entry) => entry.key === item.key)
  const offset = event.key === 'ArrowUp' ? -1 : 2
  const neighbour = section.items[index + offset] ?? null
  if (event.key === 'ArrowUp' && !neighbour) return
  dragging.value = new Set([item.key])
  void dropOn(neighbour, section)
}

// Quick add: always on screen, keeps its type and parent between entries so a run of
// stories under one epic is just typing and Enter.
const quickTypes: WorkItemType[] = ['story', 'bug', 'epic', 'feature']
const quickType = ref<WorkItemType>('story')
const quickTitle = ref('')
const quickParentId = ref('')
const quickInput = ref<HTMLInputElement | null>(null)
const saving = ref(false)
const quickParents = computed(() => allItems.value.filter((item) => allowsParent(item.type, quickType.value)))
watch(quickType, () => {
  if (!quickParents.value.some((item) => item.id === quickParentId.value)) quickParentId.value = requiresParent(quickType.value) ? (quickParents.value[0]?.id ?? '') : ''
})
useShortcut('c', () => { if (!itemModal.openKey.value) quickInput.value?.focus() })

// Adding beneath a row opens a draft directly under it.
const childDraft = ref<{ parent: WorkItem; type: WorkItemType; title: string } | null>(null)
const childInput = ref<HTMLInputElement[] | null>(null)
async function startChild(parent: WorkItem) {
  const types = childTypes(parent.type)
  if (!types[0]) return
  childDraft.value = { parent, type: types[0], title: '' }
  if (collapsed.value.has(parent.id)) toggleCollapsed(parent.id)
  await nextTick()
  childInput.value?.[0]?.focus()
}

async function submit(type: WorkItemType, title: string, parentId: string | null) {
  if (!title.trim() || saving.value) return false
  saving.value = true
  try {
    const created = await createItem(props.slug, props.projectKey, { type, title: title.trim(), teamId: props.teamId, parentId })
    toast.success(`${created.key} created.`)
    await invalidate()
    return true
  } catch (error) {
    toast.error(error, 'The item could not be created.')
    return false
  } finally {
    saving.value = false
  }
}
async function submitQuick() {
  if (requiresParent(quickType.value) && !quickParentId.value) {
    toast.info(`A ${typeLabels[quickType.value].toLowerCase()} needs a parent.`, 'Pick one, or add it from the parent row’s + button.')
    return
  }
  if (await submit(quickType.value, quickTitle.value, quickParentId.value || null)) {
    quickTitle.value = ''
    quickInput.value?.focus()
  }
}
async function submitChild() {
  const draft = childDraft.value
  if (!draft) return
  if (await submit(draft.type, draft.title, draft.parent.id)) {
    draft.title = ''
    childInput.value?.[0]?.focus()
  }
}

// A real link, so middle-click and copy-link still give the item's own page; a plain click opens
// it over the backlog instead.
const itemLink = (item: WorkItem) => `/o/${props.slug}/p/${props.projectKey}/items/${item.key}`
const itemModal = useItemModal()
function openItem(event: MouseEvent, item: WorkItem) {
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return
  event.preventDefault()
  itemModal.open(item.key)
}
const estimate = (item: WorkItem) => item.points ?? (item.rollup.totalCount ? item.rollup.pointsTotal : null)
const remaining = (item: WorkItem) => item.remainingHours ?? (item.rollup.totalCount ? item.rollup.remainingHours : null)
</script>

<template>
  <AppShell>
    <section class="grid w-full gap-6 px-5 py-8 lg:grid-cols-[minmax(0,1fr)_18rem]">
      <div class="min-w-0">
        <div>
          <h1 class="text-xl font-semibold">Backlog</h1>
          <p class="text-muted-foreground text-sm">Plan, rank and shape your team’s upcoming work.</p>
        </div>

        <form class="border-border bg-card mt-5 flex flex-wrap items-center gap-2 rounded-lg border p-2" aria-label="Quick add" @submit.prevent="submitQuick">
          <label class="sr-only" for="quick-type">Type</label>
          <select id="quick-type" v-model="quickType" class="border-input bg-background h-9 rounded-md border px-2 text-sm">
            <option v-for="type in quickTypes" :key="type" :value="type">{{ typeLabels[type] }}</option>
          </select>
          <label class="sr-only" for="quick-title">Title</label>
          <input
            id="quick-title" ref="quickInput" v-model="quickTitle" class="border-input bg-background h-9 min-w-48 flex-1 rounded-md border px-3 text-sm"
            :placeholder="`Add a ${typeLabels[quickType].toLowerCase()}… (press c)`" autocomplete="off" maxlength="500"
          >
          <label class="sr-only" for="quick-parent">Parent</label>
          <select id="quick-parent" v-model="quickParentId" class="border-input bg-background h-9 max-w-56 rounded-md border px-2 text-sm" :disabled="quickType === 'epic'">
            <option v-if="!requiresParent(quickType)" value="">No parent</option>
            <option v-for="parent in quickParents" :key="parent.id" :value="parent.id">{{ parent.key }} · {{ parent.title }}</option>
          </select>
          <button class="bg-primary text-primary-foreground inline-flex h-9 items-center gap-1.5 rounded-md px-3 text-sm disabled:opacity-50" :disabled="saving || !quickTitle.trim()">
            <Plus class="size-4" /> Add
          </button>
        </form>

        <ItemFilterBar
          class="mt-3"
          :filter="filter"
          :search="search"
          :error="queryError"
          @update:filter="update({ f: $event })"
          @update:search="update({ q: $event })"
        />

        <p v-if="backlog.isError.value && !queryError" class="text-destructive mt-6 text-sm">The backlog could not be loaded.</p>
        <p v-else-if="backlog.isPending.value" class="text-muted-foreground mt-6 text-sm">Loading backlog…</p>

        <section v-for="section in sections" :key="section.id" class="border-border mt-5 overflow-hidden rounded-lg border" :aria-label="section.title">
          <header class="bg-muted/40 text-muted-foreground grid grid-cols-[minmax(0,1fr)_4.5rem_5rem] gap-2 border-b px-3 py-2 text-xs font-medium">
            <span class="text-foreground flex items-baseline gap-2"><strong class="text-sm">{{ section.title }}</strong><span class="text-muted-foreground font-normal">{{ section.hint }} · {{ section.count }}</span></span>
            <span>Points</span><span>Remaining</span>
          </header>
          <template v-for="row in flattenBacklog(section.items, collapsed)" :key="row.item.id">
            <div
              class="hover:bg-accent/60 group grid cursor-grab grid-cols-[minmax(0,1fr)_4.5rem_5rem] items-center gap-2 border-b px-3 py-1.5 text-sm"
              :class="selected.has(row.item.key) && 'bg-accent'" :data-backlog-row="row.item.key" draggable="true" tabindex="0"
              @click="toggleSelection(row.item.key, $event)" @keydown="keyboardReorder($event, row.item, section)"
              @dragstart="startDrag(row.item)" @dragover.prevent @drop.prevent="dropOn(row.item, section, $event.altKey)"
            >
              <div class="flex min-w-0 items-center gap-1.5" :style="{ paddingLeft: `${row.depth * 22}px` }">
                <GripVertical class="text-muted-foreground/60 size-4 flex-none" />
                <button v-if="row.hasChildren" class="text-muted-foreground rounded p-0.5" :aria-label="collapsed.has(row.item.id) ? `Expand ${row.item.key}` : `Collapse ${row.item.key}`" @click.stop="toggleCollapsed(row.item.id)">
                  <ChevronRight v-if="collapsed.has(row.item.id)" class="size-4" /><ChevronDown v-else class="size-4" />
                </button>
                <span v-else class="w-5 flex-none" />
                <ItemTypeIcon :type="row.item.type" />
                <RouterLink :to="itemLink(row.item)" class="text-muted-foreground hover:text-foreground flex-none font-mono text-xs" @click.stop="openItem($event, row.item)">{{ row.item.key }}</RouterLink>
                <RouterLink :to="itemLink(row.item)" class="truncate font-medium hover:underline" @click.stop="openItem($event, row.item)">{{ row.item.title }}</RouterLink>
                <button
                  v-if="childTypes(row.item.type).length" class="text-muted-foreground hover:text-foreground hover:bg-background ml-auto flex-none rounded p-1 opacity-60 group-hover:opacity-100"
                  :title="`Add ${childTypes(row.item.type).map((type) => typeLabels[type].toLowerCase()).join(' / ')} to ${row.item.key}`"
                  :aria-label="`Add child to ${row.item.key}`" @click.stop="startChild(row.item)"
                ><Plus class="size-3.5" /></button>
              </div>
              <span class="text-muted-foreground">{{ estimate(row.item) ?? '—' }}</span>
              <span class="text-muted-foreground">{{ remaining(row.item) ?? '—' }}</span>
            </div>
            <form
              v-if="childDraft?.parent.id === row.item.id" class="bg-muted/30 flex items-center gap-2 border-b px-3 py-2"
              :style="{ paddingLeft: `${(row.depth + 1) * 22 + 36}px` }" :aria-label="`Add child to ${row.item.key}`"
              @submit.prevent="submitChild" @keydown.esc="childDraft = null"
            >
              <select v-model="childDraft.type" class="border-input bg-background h-8 rounded-md border px-2 text-sm" aria-label="Child type">
                <option v-for="type in childTypes(row.item.type)" :key="type" :value="type">{{ typeLabels[type] }}</option>
              </select>
              <input ref="childInput" v-model="childDraft.title" class="border-input bg-background h-8 min-w-0 flex-1 rounded-md border px-3 text-sm" :placeholder="`New ${typeLabels[childDraft.type].toLowerCase()} in ${row.item.key}`" maxlength="500" autocomplete="off">
              <button class="bg-primary text-primary-foreground h-8 rounded-md px-3 text-sm disabled:opacity-50" :disabled="saving || !childDraft.title.trim()">Add</button>
              <button type="button" class="text-muted-foreground hover:text-foreground rounded p-1" aria-label="Cancel" @click="childDraft = null"><X class="size-4" /></button>
            </form>
          </template>
          <div
            v-if="hasMore(section)" :key="`more-${section.id}-${section.items.length}`" v-near-end="() => loadMore(section)"
            class="text-muted-foreground border-b px-3 py-2 text-center text-xs"
          >
            <button type="button" class="hover:text-foreground" :disabled="backlog.isFetching.value" @click="loadMore(section)">
              {{ backlog.isFetching.value ? 'Loading…' : `Show more (${section.count - section.items.length} more)` }}
            </button>
          </div>
          <div class="text-muted-foreground px-3 py-3 text-center text-xs" @dragover.prevent @drop.prevent="dropOn(null, section)">
            <template v-if="section.items.length">Drop here to move to the end of {{ section.title }}</template>
            <template v-else-if="filtered">Nothing in {{ section.title }} matches the filter.</template>
            <template v-else-if="section.sprint">Nothing planned yet. Drag items here to plan them into {{ section.title }}.</template>
            <template v-else>The backlog is empty. Use the bar above to add the first item.</template>
          </div>
        </section>
        <p v-if="sections.length" class="text-muted-foreground mt-3 text-xs">+ on a row adds a child · Alt while dropping re-parents · Alt+↑/↓ reorders the focused item · Ctrl-click selects several</p>
      </div>
      <aside class="space-y-4">
        <div class="border-border rounded-lg border p-4">
          <h2 class="font-medium">Sprint planning</h2>
          <p class="text-muted-foreground mt-1 text-sm">{{ activeSprint?.name ?? 'No active sprint' }}</p>
          <div v-if="activeSprint" class="mt-4 space-y-2 text-sm">
            <div class="flex justify-between"><span>Committed</span><strong>{{ activeSprint.progress.pointsDone }}/{{ activeSprint.progress.pointsTotal }} pts</strong></div>
            <div class="flex justify-between"><span>Capacity</span><strong>{{ capacityPercent }}%</strong></div>
            <div class="bg-muted h-2 overflow-hidden rounded"><div class="bg-primary h-full" :style="{ width: `${Math.min(capacityPercent, 100)}%` }" /></div>
          </div>
          <p v-else class="text-muted-foreground mt-3 text-sm">Start a sprint to see capacity.</p>
        </div>
        <div class="border-border rounded-lg border p-4">
          <h2 class="font-medium">Velocity forecast</h2>
          <p class="text-muted-foreground mt-2 text-sm">Average {{ velocityData?.averageVelocity ?? 0 }} points/sprint</p>
          <p v-if="velocityData?.forecast" class="text-muted-foreground text-sm">Next scope {{ velocityData.forecast.scopePoints }} pts · forecast {{ velocityData.forecast.velocity }} pts</p>
        </div>
      </aside>
    </section>
  </AppShell>
</template>
