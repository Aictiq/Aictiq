<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { Loader2 } from '@lucide/vue'
import { listProjectItems, type WorkItem, type WorkItemType } from '@/api/items'
import ItemTypeIcon from '@/components/common/ItemTypeIcon.vue'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { parentTypes, requiresParent, typeLabels } from '@/lib/hierarchy'

/**
 * Picks a new parent for one or more existing items. Only types that may parent every one
 * of them are offered, searched across the whole project rather than one team's backlog,
 * so an Epic or Feature without a team can still be chosen. "No parent" is offered when
 * every item may stand alone. The server still checks the hierarchy on save.
 */
const props = withDefaults(
  defineProps<{
    slug: string
    projectKey: string
    open: boolean
    /** The items being moved: their types decide the choices, and none may be its own parent. */
    items: Pick<WorkItem, 'id' | 'key' | 'type' | 'parentId'>[]
    busy?: boolean
    error?: string | null
  }>(),
  { busy: false, error: null },
)
const emit = defineEmits<{ 'update:open': [open: boolean]; pick: [parent: WorkItem | null] }>()

const search = ref('')
const debounced = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = value.trim()), 200)
})
watch(
  () => props.open,
  (open) => {
    if (!open) return
    search.value = ''
    debounced.value = ''
  },
)

const types = computed<WorkItemType[]>(() => {
  const [first, ...rest] = props.items
  if (!first) return []
  return parentTypes(first.type).filter((type) =>
    rest.every((item) => parentTypes(item.type).includes(type)),
  )
})
const mayClear = computed(
  () => props.items.length > 0 && props.items.every((item) => !requiresParent(item.type)),
)
const excluded = computed(() => new Set(props.items.map((item) => item.id)))
const currentParentId = computed(() => {
  const ids = new Set(props.items.map((item) => item.parentId))
  return ids.size === 1 ? [...ids][0] : undefined
})

// The filter grammar takes one type at a time; at most three are ever allowed.
const candidates = useQuery({
  queryKey: computed(() => [
    props.slug,
    props.projectKey,
    'parent-candidates',
    types.value.join(','),
    debounced.value,
  ]),
  queryFn: async () => {
    const pages = await Promise.all(
      types.value.map((type) =>
        listProjectItems(props.slug, props.projectKey, {
          filter: `type:${type}`,
          q: debounced.value || undefined,
        }),
      ),
    )
    return pages.flatMap((page) => page.items)
  },
  enabled: computed(() => props.open && types.value.length > 0),
  placeholderData: keepPreviousData,
})
const options = computed(() =>
  (candidates.data.value ?? []).filter((item) => !excluded.value.has(item.id)),
)
const typeList = computed(() => types.value.map((type) => typeLabels[type]).join(', '))
const subject = computed(() =>
  props.items.length === 1 ? props.items[0]!.key : `${props.items.length} items`,
)
</script>

<template>
  <Dialog :open="open" @update:open="(value: boolean) => emit('update:open', value)">
    <DialogContent class="sm:max-w-lg" data-testid="parent-picker">
      <DialogHeader>
        <DialogTitle>Move {{ subject }} under…</DialogTitle>
        <DialogDescription v-if="types.length">
          Choose a parent. Allowed parent types: {{ typeList }}.
        </DialogDescription>
        <DialogDescription v-else>Epics sit at the top and cannot have a parent.</DialogDescription>
      </DialogHeader>
      <template v-if="types.length">
        <input
          v-model="search"
          class="border-input bg-background h-9 w-full rounded border px-2 text-sm"
          placeholder="Search by key or title"
          aria-label="Search parents"
          data-testid="parent-picker-search"
        />
        <ul class="-mx-1 max-h-80 overflow-y-auto" role="listbox" aria-label="Parents">
          <li v-if="mayClear">
            <button
              type="button"
              role="option"
              :aria-selected="currentParentId === null"
              class="hover:bg-accent text-muted-foreground flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm disabled:opacity-50"
              :disabled="busy || currentParentId === null"
              data-testid="parent-picker-none"
              @click="emit('pick', null)"
            >
              No parent
              <span v-if="currentParentId === null" class="ml-auto text-xs">Current</span>
            </button>
          </li>
          <li v-for="option in options" :key="option.id">
            <button
              type="button"
              role="option"
              :aria-selected="currentParentId === option.id"
              class="hover:bg-accent flex w-full min-w-0 items-center gap-2 rounded px-2 py-1.5 text-left text-sm disabled:opacity-50"
              :disabled="busy || currentParentId === option.id"
              @click="emit('pick', option)"
            >
              <ItemTypeIcon :type="option.type" />
              <span class="text-muted-foreground flex-none font-mono text-xs">{{
                option.key
              }}</span>
              <span class="min-w-0 truncate">{{ option.title }}</span>
              <span
                v-if="currentParentId === option.id"
                class="text-muted-foreground ml-auto flex-none text-xs"
              >
                Current
              </span>
            </button>
          </li>
          <li
            v-if="candidates.isPending.value && candidates.isFetching.value"
            class="text-muted-foreground flex items-center gap-2 px-2 py-1.5 text-sm"
          >
            <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading…
          </li>
          <li v-else-if="!options.length" class="text-muted-foreground px-2 py-1.5 text-sm">
            No {{ typeList }} {{ debounced ? `matches "${debounced}"` : 'in this project yet' }}.
          </li>
        </ul>
      </template>
      <p v-if="busy" class="text-muted-foreground text-xs">Saving…</p>
      <p v-else-if="error" class="text-destructive text-xs" role="alert">{{ error }}</p>
      <p v-else-if="candidates.isError.value" class="text-destructive text-xs" role="alert">
        Parents could not be loaded.
      </p>
    </DialogContent>
  </Dialog>
</template>
