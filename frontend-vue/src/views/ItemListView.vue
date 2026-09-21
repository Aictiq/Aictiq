<script setup lang="ts">
import { computed, h, onBeforeUnmount, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'
import { Plus } from '@lucide/vue'
import type { SortingState } from '@tanstack/vue-table'
import { createItem, listProjectItems, type WorkItem } from '@/api/items'
import { createSavedView, listSavedViews } from '@/api/views'
import DataTable from '@/components/common/DataTable.vue'
import ClaimGlyph from '@/components/common/ClaimGlyph.vue'
import KeyChip from '@/components/common/KeyChip.vue'
import PriorityIcon from '@/components/common/PriorityIcon.vue'
import StateBadge from '@/components/common/StateBadge.vue'
import ItemFilterBar from '@/components/items/ItemFilterBar.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { useCommands } from '@/composables/useCommands'
import { useItemModal } from '@/composables/useItemModal'
import { itemQueryError, useItemQueryParams } from '@/composables/useItemQueryParams'
import { reduceListKeyboard } from '@/lib/item-list-keyboard'
import { useProjectRealtime } from '@/composables/useProjectRealtime'

const props = defineProps<{ slug: string; projectKey: string }>()
const route = useRoute()
const router = useRouter()
const client = useQueryClient()
const { filter, search, sort, update } = useItemQueryParams()
const page = computed(() => Math.max(1, Number(route.query.page ?? 1) || 1))
const queryKey = computed(() => [
  props.slug,
  props.projectKey,
  'items',
  filter.value,
  search.value,
  sort.value,
  page.value,
])
const items = useQuery({
  queryKey,
  queryFn: () =>
    listProjectItems(props.slug, props.projectKey, {
      filter: filter.value,
      q: search.value,
      sort: sort.value,
      page: page.value,
    }),
})
const queryError = computed(() => itemQueryError(items.error.value))

// Header clicks become the server's `sort`, so the order spans every page rather than
// the 50 rows on screen. Column ids are the accessor keys below.
const sortFields: Record<string, string> = {
  key: 'number',
  title: 'title',
  stateCategory: 'state',
  priority: 'priority',
  remainingHours: 'remaining',
}
const sorting = computed<SortingState>(() => {
  const [field, direction] = sort.value.split(':')
  const id = Object.keys(sortFields).find((column) => sortFields[column] === field)
  return id ? [{ id, desc: direction === 'desc' }] : []
})
function setSorting(next: SortingState) {
  const first = next[0]
  const field = first && sortFields[first.id]
  update({ s: field ? `${field}:${first.desc ? 'desc' : 'asc'}` : '' })
}
const views = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'views']),
  queryFn: () => listSavedViews(props.slug, props.projectKey),
})
const itemPage = computed(() => items.data.value ?? null)
const savedViews = computed(() => views.data.value ?? [])
const creating = ref(false)
const title = ref('')
const selected = ref(new Set<string>())
const current = ref(0)
useProjectRealtime(() => props.slug, () => props.projectKey)
const columns = [
  // The claim glyph rides with the key rather than taking a column of its own: most items
  // are not claimed, and an almost-always-empty column costs every row its width.
  {
    accessorKey: 'key',
    header: 'Key',
    cell: ({ row }: { row: { original: WorkItem } }) =>
      h('span', { class: 'inline-flex items-center gap-1.5' }, [
        h(KeyChip, { label: row.original.key }),
        h(ClaimGlyph, {
          claimedBy: row.original.claimedBy,
          claimHeartbeatAt: row.original.claimHeartbeatAt,
        }),
      ]),
  },
  { accessorKey: 'title', header: 'Title' },
  {
    accessorKey: 'stateCategory',
    header: 'State',
    cell: ({ row }: { row: { original: WorkItem } }) =>
      h(StateBadge, {
        name: row.original.stateCategory,
        category: row.original.stateCategory as
          'proposed' | 'active' | 'resolved' | 'completed' | 'removed',
      }),
  },
  {
    accessorKey: 'priority',
    header: 'Priority',
    // Most urgent first on the first click, like every tracker people already know.
    sortDescFirst: true,
    cell: ({ row }: { row: { original: WorkItem } }) =>
      h(PriorityIcon, { priority: row.original.priority }),
  },
  {
    accessorKey: 'remainingHours',
    header: 'Remaining',
    cell: ({ row }: { row: { original: WorkItem } }) =>
      row.original.remainingHours == null ? '—' : `${row.original.remainingHours}h`,
  },
]
function replaceQuery(next: Record<string, string | number | undefined>) {
  void router.replace({
    query: Object.fromEntries(
      Object.entries(next).filter(([, value]) => value !== undefined && value !== ''),
    ),
  })
}
const itemModal = useItemModal()
function open(item: WorkItem) {
  itemModal.open(item.key)
}
async function create() {
  if (!title.value.trim()) return
  await createItem(props.slug, props.projectKey, { type: 'bug', title: title.value.trim() })
  title.value = ''
  creating.value = false
  await client.invalidateQueries({ queryKey: [props.slug, props.projectKey, 'items'] })
}
async function saveView() {
  const name = window.prompt('Name this view')
  if (!name?.trim()) return
  await createSavedView(props.slug, props.projectKey, {
    name: name.trim(),
    filter: filter.value,
    sort: sort.value,
    columns: ['key', 'title', 'state', 'priority', 'remaining'],
    isShared: false,
  })
  await client.invalidateQueries({ queryKey: [props.slug, props.projectKey, 'views'] })
}
function applyView(id: string) {
  const view = views.data.value?.find((entry) => entry.id === id)
  if (view) update({ f: view.filter, s: view.sort, q: '' })
}
function onKeydown(event: KeyboardEvent) {
  if (
    event.target instanceof HTMLInputElement ||
    event.target instanceof HTMLTextAreaElement ||
    itemModal.openKey.value
  )
    return
  const listed = items.data.value?.items ?? []
  const keys = listed.map((item) => item.key)
  if (event.key === 'j' || event.key === 'k' || event.key === 'x') {
    event.preventDefault()
    const action = event.key === 'j' ? 'down' : event.key === 'k' ? 'up' : 'toggle'
    const next = reduceListKeyboard(
      { index: current.value, selected: selected.value },
      action,
      keys,
    )
    current.value = next.index
    selected.value = next.selected
  } else if (event.key === 'Enter' && listed[current.value]) open(listed[current.value]!)
  else if (event.key === ' ') {
    event.preventDefault()
    const focused = listed[current.value]
    if (focused) open(focused)
  } else if (event.key === 'c') {
    event.preventDefault()
    creating.value = true
  }
}
window.addEventListener('keydown', onKeydown)
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
useCommands(() => [
  {
    id: 'items.quick-create',
    label: 'Create item',
    group: 'Items',
    shortcut: 'c',
    run: () => {
      creating.value = true
    },
  },
])
</script>

<template>
  <AppShell>
    <section class="w-full px-4 py-6 sm:px-5 sm:py-8">
      <div class="flex items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-semibold">Items</h1>
          <p class="text-muted-foreground text-sm">{{ itemPage?.totalCount ?? 0 }} work items</p>
        </div>
        <button
          class="bg-primary text-primary-foreground inline-flex items-center gap-2 rounded-md px-3 py-2 text-sm"
          @click="creating = true"
        >
          <Plus class="size-4" /> Create
        </button>
      </div>

      <ItemFilterBar
        class="mt-5"
        :filter="filter"
        :search="search"
        :error="queryError"
        @update:filter="update({ f: $event })"
        @update:search="update({ q: $event })"
      >
        <select
          aria-label="Saved views"
          class="border-input bg-background h-9 min-w-0 rounded-md border px-2 text-sm"
          @change="applyView(($event.target as HTMLSelectElement).value)"
        >
          <option value="">Saved views</option>
          <option v-for="view in savedViews" :key="view.id" :value="view.id">
            {{ view.name }}
          </option>
        </select>
        <button
          type="button"
          class="border-input h-9 rounded-md border px-3 text-sm whitespace-nowrap"
          @click="saveView"
        >
          Save view
        </button>
      </ItemFilterBar>

      <p class="text-muted-foreground mt-2 hidden text-xs sm:block">
        j/k move · x select · Enter or Space open · c create
      </p>
      <p v-if="items.isError.value && !queryError" class="text-destructive py-10">
        Items could not be loaded.
      </p>
      <DataTable
        v-else
        :data="itemPage?.items ?? []"
        :columns="columns"
        :sorting="sorting"
        class="mt-4"
        empty-title="No items match this view"
        @update:sorting="setSorting"
        @row-activate="open"
      />
      <div class="mt-4 flex justify-end gap-2">
        <button
          class="border-input rounded border px-3 py-1 text-sm disabled:opacity-50"
          :disabled="page <= 1"
          @click="replaceQuery({ ...route.query, page: page - 1 })"
        >
          Previous</button
        ><button
          class="border-input rounded border px-3 py-1 text-sm disabled:opacity-50"
          :disabled="!itemPage || page * 50 >= itemPage.totalCount"
          @click="replaceQuery({ ...route.query, page: page + 1 })"
        >
          Next
        </button>
      </div>
      <form
        v-if="creating"
        class="bg-background fixed inset-x-0 bottom-0 z-30 mx-auto grid max-w-lg grid-cols-[minmax(0,1fr)_auto] gap-2 border p-4 shadow-lg sm:flex"
        @submit.prevent="create"
      >
        <input
          v-model="title"
          autofocus
          class="border-input col-span-2 min-w-0 flex-1 rounded border px-3 py-2 sm:col-span-1"
          placeholder="Bug title"
        />
        <button class="bg-primary text-primary-foreground rounded px-3 py-2">Create</button
        ><button type="button" class="rounded border px-3 py-2" @click="creating = false">
          Cancel
        </button>
      </form>
    </section>
  </AppShell>
</template>
