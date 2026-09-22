<script setup lang="ts">
import { Search, X } from '@lucide/vue'
import { onBeforeUnmount, ref, watch } from 'vue'

import { serializeItemFilter } from '@/lib/item-filters'
import { cn } from '@/lib/utils'

/**
 * The filter grammar and the search box, as every item surface (list, backlog, board) shows
 * them. The two apply differently on purpose: search follows typing, after a short pause,
 * because every prefix of a word is a valid search; the filter waits for Enter, because
 * "state:" on its way to "state:active" is not a filter and the server would reject it
 * once per keystroke.
 */
const props = defineProps<{
  filter: string
  search: string
  /** The server's message for the applied filter, shown under the bar. */
  error?: string | null
  class?: string
}>()
const emit = defineEmits<{ 'update:filter': [value: string]; 'update:search': [value: string] }>()

const SEARCH_DELAY = 300
const filterDraft = ref(props.filter)
const searchDraft = ref(props.search)
let timer: ReturnType<typeof setTimeout> | undefined

// Back/forward, a saved view or a cleared URL change the applied values from outside.
watch(
  () => props.filter,
  (value) => {
    filterDraft.value = value
  },
)
watch(
  () => props.search,
  (value) => {
    if (value !== searchDraft.value.trim()) searchDraft.value = value
  },
)

function onSearchInput() {
  clearTimeout(timer)
  timer = setTimeout(() => {
    const value = searchDraft.value.trim()
    if (value !== props.search) emit('update:search', value)
  }, SEARCH_DELAY)
}
function apply() {
  clearTimeout(timer)
  const value = serializeItemFilter(filterDraft.value.split(/\s+/))
  if (value !== props.filter) emit('update:filter', value)
  const search = searchDraft.value.trim()
  if (search !== props.search) emit('update:search', search)
}
function clearSearch() {
  clearTimeout(timer)
  searchDraft.value = ''
  if (props.search) emit('update:search', '')
}
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div :class="cn('min-w-0', props.class)">
    <form class="flex flex-wrap items-center gap-2" role="search" @submit.prevent="apply">
      <input
        v-model="filterDraft"
        aria-label="Advanced filter"
        :aria-invalid="Boolean(error)"
        :aria-describedby="error ? 'item-filter-error' : undefined"
        class="border-input bg-background h-9 min-w-48 flex-1 rounded-md border px-3 text-sm aria-invalid:border-destructive"
        placeholder="Filter (Enter): state:active assignee:@me"
        title="Filter grammar, e.g. state:active assignee:@me label:ui - press Enter to apply"
        autocomplete="off"
        spellcheck="false"
      />
      <div class="relative">
        <Search
          class="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2"
          aria-hidden="true"
        />
        <input
          v-model="searchDraft"
          type="search"
          aria-label="Search items"
          class="border-input bg-background h-9 w-48 rounded-md border pr-8 pl-8 text-sm [&::-webkit-search-cancel-button]:hidden"
          placeholder="Search"
          autocomplete="off"
          @input="onSearchInput"
          @keydown.esc="clearSearch"
        />
        <button
          v-if="searchDraft"
          type="button"
          class="text-muted-foreground hover:text-foreground absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5"
          aria-label="Clear search"
          @click="clearSearch"
        >
          <X class="size-3.5" />
        </button>
      </div>
      <button type="submit" class="sr-only">Apply filters</button>
      <slot />
    </form>
    <p v-if="error" id="item-filter-error" role="alert" class="text-destructive mt-1.5 text-xs">
      {{ error }}
    </p>
  </div>
</template>
