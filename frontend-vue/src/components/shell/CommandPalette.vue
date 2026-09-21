<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { FileText } from '@lucide/vue'

import { searchOrganization, type SearchResponse } from '@/api/search'
import KeyChip from '@/components/common/KeyChip.vue'
import SearchSnippet from '@/components/common/SearchSnippet.vue'
import { parsePaletteMode, useCommandStore, type Command } from '@/composables/useCommands'
import { useFocusTrap } from '@/composables/useFocusTrap'
import { useShortcut } from '@/composables/useShortcuts'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * ⌘K. Deliberately hand-built rather than shadcn's Command wrapper: the palette needs a
 * grouped, ranked list driven by the registry and full control of arrow-key semantics,
 * and wrapping a combobox to get that ends up being more code, not less.
 *
 * Accessibility is the part that is easy to skip and expensive to retrofit — the input
 * owns the listbox via `aria-activedescendant`, so focus never leaves the text field and
 * a screen reader announces the highlighted command as it changes.
 */
const store = useCommandStore()
const organizations = useOrganizationsStore()
const router = useRouter()

const input = ref<HTMLInputElement | null>(null)
const dialog = ref<HTMLElement | null>(null)
const activeIndex = ref(0)
const searchResults = ref<SearchResponse | null>(null)
const searchLoading = ref(false)
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchRequest = 0

const palette = computed(() => parsePaletteMode(store.query))
const commandMode = computed(() => palette.value.mode !== 'search')
const searchText = computed(() => palette.value.term)
const flat = computed(() => {
  if (!commandMode.value) return []
  const term = palette.value.term
  const mode = palette.value.mode
  return store.available.filter((command) => {
    if (mode === 'go') return command.group === 'Navigation'
    if (mode === 'assign') return command.id.includes('assign')
    if (mode === 'state') return command.id.includes('state')
    if (mode === 'label') return command.id.includes('label')
    return fuzzy(command, term)
  })
})
const activeCommand = computed(() => flat.value[activeIndex.value])
const paletteResults = computed(() => {
  const groups = new Map<string, Command[]>()
  for (const command of flat.value) groups.set(command.group, [...(groups.get(command.group) ?? []), command])
  return [...groups].map(([label, items]) => ({ label, items }))
})

function fuzzy(command: Command, term: string) {
  return !term || `${command.label} ${command.keywords ?? ''}`.toLowerCase().includes(term.toLowerCase())
}

// ⌘K works from anywhere, including a focused text field — that is what makes it feel
// like part of the browser rather than part of the page.
useShortcut('mod+k', () => store.toggle(), { allowInInput: true })
useShortcut('escape', () => store.hide(), { allowInInput: true, when: () => store.open })
useFocusTrap(dialog, computed(() => store.open))

watch(
  () => store.open,
  async (open) => {
    if (!open) return
    activeIndex.value = 0
    await nextTick()
    input.value?.focus()
  },
)

// Any change to the query invalidates the highlight position.
watch(
  () => store.query,
  () => (activeIndex.value = 0),
)

watch([searchText, () => organizations.currentSlug], () => {
  if (searchTimer) clearTimeout(searchTimer)
  const term = searchText.value
  const slug = organizations.currentSlug
  if (commandMode.value || !term || !slug) { searchResults.value = null; searchLoading.value = false; return }
  searchLoading.value = true
  const request = ++searchRequest
  searchTimer = setTimeout(async () => {
    try {
      const results = await searchOrganization(slug, term, { limit: 8 })
      if (request === searchRequest) searchResults.value = results
    } catch {
      if (request === searchRequest) searchResults.value = null
    } finally {
      if (request === searchRequest) searchLoading.value = false
    }
  }, 180)
})

onBeforeUnmount(() => { if (searchTimer) clearTimeout(searchTimer) })

function move(delta: number) {
  const count = flat.value.length
  if (count === 0) return
  // Wraps, so ↑ from the top lands on the last item rather than doing nothing.
  activeIndex.value = (activeIndex.value + delta + count) % count
}

async function runActive() {
  if (!commandMode.value && searchText.value) {
    await openSearch()
    return
  }
  const command = flat.value[activeIndex.value]
  if (command) await store.run(command)
}

async function openSearch() {
  const q = searchText.value
  store.hide()
  await router.push({ name: 'search', query: { q } })
}

function indexOf(command: Command) {
  return flat.value.indexOf(command)
}
</script>

<template>
  <div
    v-if="store.open"
    class="fixed inset-0 z-60 flex justify-center bg-black/60 pt-[13vh] backdrop-blur-[3px]"
    @click="store.hide()"
  >
    <div
      ref="dialog"
      role="dialog"
      aria-modal="true"
      aria-label="Command palette"
      class="bg-popover border-border h-fit w-[560px] max-w-[92vw] overflow-hidden rounded-lg border shadow-2xl"
      @click.stop
    >
      <div class="border-border flex items-center gap-2 border-b px-3.5 py-3">
        <span class="text-primary" aria-hidden="true">⌕</span>
        <input
          ref="input"
          v-model="store.query"
          type="text"
          role="combobox"
          aria-expanded="true"
          aria-controls="command-list"
          :aria-activedescendant="commandMode && activeCommand ? `command-${activeCommand.id}` : undefined"
          :placeholder="commandMode ? 'Commands: > · go: g · assign: @ · state: # · label: ~' : 'Search items, comments and pages…'"
          class="flex-1 bg-transparent text-sm outline-none"
          @keydown.down.prevent="move(1)"
          @keydown.up.prevent="move(-1)"
          @keydown.enter.prevent="runActive"
        />
        <KeyChip label="ESC" />
      </div>

      <div id="command-list" role="listbox" class="max-h-[340px] overflow-y-auto p-1.5">
        <template v-if="commandMode">
          <template v-for="group in paletteResults" :key="group.label">
            <div class="font-label px-2 pt-2 pb-1">{{ group.label }}</div>
            <button
              v-for="command in group.items"
              :id="`command-${command.id}`"
              :key="command.id"
              type="button"
              role="option"
              :aria-selected="indexOf(command) === activeIndex"
              class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-[13px]"
              :class="indexOf(command) === activeIndex ? 'bg-accent' : 'hover:bg-accent/60'"
              @mousemove="activeIndex = indexOf(command)"
              @click="store.run(command)"
            >
              <span class="text-muted-foreground w-4 flex-none text-center" aria-hidden="true">
                {{ command.icon ?? '›' }}
              </span>
              <span class="flex-1 truncate">{{ command.label }}</span>
              <KeyChip v-if="command.shortcut" :binding="command.shortcut" />
            </button>
          </template>
        </template>

        <p v-if="commandMode && flat.length === 0" class="text-muted-foreground px-2 py-6 text-center text-xs">
          No matching commands.
        </p>
        <template v-else-if="searchText">
          <p v-if="searchLoading" class="text-muted-foreground px-2 py-5 text-center text-xs">Searching…</p>
          <template v-else-if="searchResults">
            <p class="font-label px-2 pt-2 pb-1">Items</p>
            <button
              v-for="item in searchResults.items"
              :key="item.id"
              type="button"
              class="hover:bg-accent/60 flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-[13px]"
              @click="openSearch"
            >
              <KeyChip :label="item.key" /><span class="flex-1 truncate">{{ item.title }}</span>
            </button>
            <p v-if="searchResults.comments.length" class="font-label px-2 pt-3 pb-1">Comments</p>
            <button
              v-for="comment in searchResults.comments"
              :key="comment.id"
              type="button"
              class="hover:bg-accent/60 flex w-full flex-col items-start gap-1 rounded px-2 py-1.5 text-left text-[13px]"
              @click="openSearch"
            >
              <span class="flex items-center gap-2"><KeyChip :label="comment.itemKey" /><span class="truncate">{{ comment.itemTitle }}</span></span>
              <span class="text-muted-foreground line-clamp-1 text-xs"><SearchSnippet :snippet="comment.snippet" /></span>
            </button>
            <p v-if="searchResults.pages.length" class="font-label px-2 pt-3 pb-1">Pages</p>
            <button
              v-for="page in searchResults.pages"
              :key="page.id"
              type="button"
              class="hover:bg-accent/60 flex w-full flex-col items-start gap-1 rounded px-2 py-1.5 text-left text-[13px]"
              @click="openSearch"
            >
              <span class="flex min-w-0 items-center gap-2"><FileText class="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" /><span class="truncate">{{ page.title }}</span></span>
              <span v-if="page.snippet" class="text-muted-foreground line-clamp-1 text-xs"><SearchSnippet :snippet="page.snippet" /></span>
            </button>
            <p v-if="searchResults.items.length + searchResults.comments.length + searchResults.pages.length === 0" class="text-muted-foreground px-2 py-5 text-center text-xs">No matches.</p>
            <button type="button" class="text-primary mt-2 w-full rounded px-2 py-1.5 text-left text-xs hover:underline" @click="openSearch">View all results</button>
          </template>
        </template>
        <p v-else class="text-muted-foreground px-2 py-6 text-center text-xs">Type to search, or start with <code>&gt;</code> for commands.</p>
      </div>
    </div>
  </div>
</template>
