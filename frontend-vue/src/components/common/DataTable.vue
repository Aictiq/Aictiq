<script setup lang="ts" generic="TData extends RowData">
import {
  FlexRender,
  getCoreRowModel,
  getSortedRowModel,
  useVueTable,
  type RowData,
  type SortingState,
} from '@tanstack/vue-table'
import { computed, ref, watch } from 'vue'

import EmptyState from '@/components/common/EmptyState.vue'
import type { AictiqColumnDef } from '@/lib/table'
import { cn } from '@/lib/utils'

/**
 * The dense list every domain screen is built on.
 *
 * Rows are keyboard-navigable by design, not as a nicety: this is the surface people
 * spend the day in, and reaching for the mouse to open the next item is the difference
 * between the app feeling fast and feeling like a form. ↑/↓ move, Enter opens, and the
 * focused row is a real `tabindex` target so screen readers follow along.
 *
 */

const props = withDefaults(
  defineProps<{
    data: TData[]
    columns: AictiqColumnDef<TData>[]
    emptyTitle?: string
    emptyDescription?: string
    rowKey?: (row: TData, index: number) => string | number
    class?: string
    /**
     * Bind with `v-model:sorting` when the rows are one page of a server-side result: the
     * table then only reports header clicks and leaves the order to the server. Sorting a
     * page in the browser would order 50 rows out of thousands and call it sorted.
     */
    sorting?: SortingState
  }>(),
  {
    emptyTitle: 'Nothing here yet',
    emptyDescription: undefined,
    rowKey: undefined,
    class: undefined,
    sorting: undefined,
  },
)

const emit = defineEmits<{ rowActivate: [row: TData]; 'update:sorting': [value: SortingState] }>()

const localSorting = ref<SortingState>([])
const manualSorting = props.sorting !== undefined
const sorting = computed(() => (manualSorting ? (props.sorting ?? []) : localSorting.value))

const table = useVueTable({
  get data() {
    return props.data
  },
  get columns() {
    return props.columns
  },
  state: {
    get sorting() {
      return sorting.value
    },
  },
  onSortingChange: (updater: SortingState | ((old: SortingState) => SortingState)) => {
    const next = typeof updater === 'function' ? updater(sorting.value) : updater
    if (manualSorting) emit('update:sorting', next)
    else localSorting.value = next
  },
  manualSorting,
  getCoreRowModel: getCoreRowModel(),
  getSortedRowModel: getSortedRowModel(),
})

// A table is one stop in the tab order. Arrow keys then move its active row, which
// avoids making a 200-row result page require 200 Tab presses.
const focusedIndex = ref(0)
const container = ref<HTMLElement | null>(null)

watch(
  () => table.getRowModel().rows.length,
  (length) => {
    if (length === 0) focusedIndex.value = -1
    else if (focusedIndex.value < 0 || focusedIndex.value >= length) focusedIndex.value = 0
  },
  { immediate: true },
)

function focusRow(index: number) {
  const rows = table.getRowModel().rows
  if (rows.length === 0) return

  focusedIndex.value = Math.max(0, Math.min(index, rows.length - 1))
  const element = container.value?.querySelector<HTMLElement>(
    `[data-row-index="${focusedIndex.value}"]`,
  )
  element?.focus()
  element?.scrollIntoView({ block: 'nearest' })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    focusRow(focusedIndex.value + 1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    focusRow(focusedIndex.value - 1)
  } else if (event.key === 'Enter' && focusedIndex.value >= 0) {
    event.preventDefault()
    const row = table.getRowModel().rows[focusedIndex.value]
    if (row) emit('rowActivate', row.original)
  }
}
</script>

<template>
  <div ref="container" :class="cn('w-full', props.class)" @keydown="onKeydown">
    <!-- Wide tables scroll inside their own box; the page itself never scrolls sideways. -->
    <div class="overflow-x-auto">
      <table class="w-full min-w-[40rem] border-collapse text-sm sm:min-w-0">
        <thead>
          <tr
            v-for="headerGroup in table.getHeaderGroups()"
            :key="headerGroup.id"
            class="border-border border-b"
          >
            <th
              v-for="header in headerGroup.headers"
              :key="header.id"
              scope="col"
              class="font-label px-3 py-2 text-left"
              :aria-sort="
                header.column.getIsSorted() === 'asc'
                  ? 'ascending'
                  : header.column.getIsSorted() === 'desc'
                    ? 'descending'
                    : 'none'
              "
            >
              <button
                v-if="header.column.getCanSort()"
                type="button"
                class="hover:text-foreground inline-flex items-center gap-1"
                @click="header.column.toggleSorting()"
              >
                <FlexRender :render="header.column.columnDef.header" :props="header.getContext()" />
                <span aria-hidden="true">{{
                  header.column.getIsSorted() === 'asc'
                    ? '↑'
                    : header.column.getIsSorted() === 'desc'
                      ? '↓'
                      : ''
                }}</span>
              </button>
              <FlexRender
                v-else
                :render="header.column.columnDef.header"
                :props="header.getContext()"
              />
            </th>
          </tr>
        </thead>

        <tbody>
          <tr
            v-for="(row, index) in table.getRowModel().rows"
            :key="rowKey ? rowKey(row.original, index) : row.id"
            :data-row-index="index"
            :tabindex="index === focusedIndex ? 0 : -1"
            class="border-border/60 hover:bg-accent/60 focus-visible:bg-accent border-b last:border-b-0"
            :style="{ height: 'var(--row-height)' }"
            @focus="focusedIndex = index"
            @click="emit('rowActivate', row.original)"
          >
            <td
              v-for="cell in row.getVisibleCells()"
              :key="cell.id"
              class="px-3"
              :style="{ paddingBlock: 'var(--row-padding-y)' }"
            >
              <FlexRender :render="cell.column.columnDef.cell" :props="cell.getContext()" />
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <EmptyState
      v-if="table.getRowModel().rows.length === 0"
      :title="emptyTitle"
      :description="emptyDescription"
    >
      <slot name="empty" />
    </EmptyState>
  </div>
</template>
