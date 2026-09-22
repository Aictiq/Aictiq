<script lang="ts">
export interface UserOption {
  userId: string
  displayName: string
  /** Null when a guest is reading the roster: they see the team without the addresses. */
  email?: string | null
  avatarKey?: string | null
  isAgent?: boolean
  /** Already chosen elsewhere, or not eligible. Shown with `hint`, never silently missing. */
  disabled?: boolean
  hint?: string
}
</script>

<script setup lang="ts">
import { Check, ChevronsUpDown, Loader2, Search, X } from '@lucide/vue'
import { computed, nextTick, ref, watch } from 'vue'

import { avatarUrl } from '@/api/profile'
import UserAvatar from '@/components/common/UserAvatar.vue'
import { cn } from '@/lib/utils'

/**
 * Picking people - and agents, which is why this is not a plain `<select>`.
 *
 * An agent is an ordinary member of the roster (same table, same assignee columns), so
 * the *only* thing standing between "Alice" and "the bot Alice built" in a picker is the
 * badge `UserAvatar` draws. A picker that rendered names as text would lose that
 * distinction exactly where it matters most: at the moment someone hands work over.
 *
 * Hand-rolled rather than built on a menu primitive because the filter box has to own the
 * keyboard - a menu's own typeahead would fight every keystroke - and because a component
 * with no portal, no observers and no animation is one a test can actually drive.
 */

const props = withDefaults(
  defineProps<{
    /** A single id, or the list of them when `multiple`. */
    modelValue: string | string[] | null
    options: UserOption[]
    multiple?: boolean
    disabled?: boolean
    loading?: boolean
    placeholder?: string
    searchPlaceholder?: string
    emptyText?: string
    /** Labels the trigger for screen readers; the visible text is the selection. */
    label?: string
    class?: string
  }>(),
  {
    multiple: false,
    disabled: false,
    loading: false,
    placeholder: 'Select someone…',
    searchPlaceholder: 'Search people and agents…',
    emptyText: 'Nobody matches.',
    label: 'Select a person',
    class: undefined,
  },
)

const emit = defineEmits<{ 'update:modelValue': [string | string[] | null] }>()

const open = ref(false)
const query = ref('')
const active = ref(0)
const root = ref<HTMLElement | null>(null)
const search = ref<HTMLInputElement | null>(null)

const selectedIds = computed<string[]>(() => {
  if (props.modelValue === null) return []
  return Array.isArray(props.modelValue) ? props.modelValue : [props.modelValue]
})

const selected = computed(() =>
  selectedIds.value
    .map((id) => props.options.find((option) => option.userId === id))
    .filter((option): option is UserOption => option !== undefined),
)

/**
 * Matches the name *and* the address: people search for a colleague by whichever of the
 * two they happen to have in front of them, and an agent's synthesised address is often
 * the only thing that tells two similarly named bots apart.
 */
const matches = computed(() => {
  const needle = query.value.trim().toLowerCase()
  const found = needle
    ? props.options.filter(
        (option) =>
          option.displayName.toLowerCase().includes(needle) ||
          (option.email ?? '').toLowerCase().includes(needle),
      )
    : props.options
  // People first, then agents under their own heading. Handing work to an agent is a
  // different decision from handing it to a colleague, and a mixed alphabetical list makes
  // it one mis-click - the badge says what a row is, the grouping says it before you look.
  return [...found].sort((a, b) => Number(a.isAgent ?? false) - Number(b.isAgent ?? false))
})

/** Where the "Agents" heading goes; -1 when the list is all people (the common case). */
const firstAgent = computed(() => matches.value.findIndex((option) => option.isAgent === true))

/** Never lands on a disabled row: Enter on one would do nothing and look broken. */
function firstSelectable(from = 0, step = 1): number {
  for (let i = from; i >= 0 && i < matches.value.length; i += step) {
    if (!matches.value[i]?.disabled) return i
  }
  return -1
}

watch(matches, () => {
  active.value = Math.max(firstSelectable(), 0)
})

watch(open, async (isOpen) => {
  if (!isOpen) {
    query.value = ''
    return
  }
  active.value = Math.max(firstSelectable(), 0)
  await nextTick()
  search.value?.focus()
})

function toggle() {
  if (props.disabled) return
  open.value = !open.value
}

function choose(option: UserOption) {
  if (option.disabled) return

  if (!props.multiple) {
    emit('update:modelValue', option.userId)
    open.value = false
    return
  }

  const next = selectedIds.value.includes(option.userId)
    ? selectedIds.value.filter((id) => id !== option.userId)
    : [...selectedIds.value, option.userId]
  emit('update:modelValue', next)
  // The list stays open: picking several people one at a time is the whole point.
}

function clear() {
  emit('update:modelValue', props.multiple ? [] : null)
}

function move(step: 1 | -1) {
  const next = firstSelectable(active.value + step, step)
  if (next !== -1) active.value = next
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    move(1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    move(-1)
  } else if (event.key === 'Enter') {
    event.preventDefault()
    const option = matches.value[active.value]
    if (option) choose(option)
  } else if (event.key === 'Escape') {
    event.preventDefault()
    open.value = false
  }
}

/** Focusout rather than a document listener: it closes on tab-away as well as on a click. */
function onFocusout(event: FocusEvent) {
  const next = event.relatedTarget
  if (next instanceof Node && root.value?.contains(next)) return
  open.value = false
}

function pictureOf(option: UserOption) {
  return option.avatarKey ? avatarUrl(option.userId, option.avatarKey) : null
}
</script>

<template>
  <div ref="root" :class="cn('relative', props.class)" @focusout="onFocusout">
    <button
      type="button"
      class="border-border bg-background focus-visible:ring-ring flex h-8 w-full items-center gap-2 rounded-lg border px-2.5 text-left text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
      :disabled="disabled"
      :aria-label="label"
      :aria-expanded="open"
      aria-haspopup="listbox"
      @click="toggle"
    >
      <template v-if="selected.length === 0">
        <span class="text-muted-foreground flex-1 truncate">{{ placeholder }}</span>
      </template>
      <template v-else-if="multiple">
        <span class="flex flex-1 items-center gap-1 truncate">
          <UserAvatar
            v-for="option in selected.slice(0, 4)"
            :key="option.userId"
            size="sm"
            :name="option.displayName"
            :is-agent="option.isAgent ?? false"
            :src="pictureOf(option)"
          />
          <span class="truncate">
            {{
              selected.length === 1
                ? selected[0]!.displayName
                : `${selected.length} selected`
            }}
          </span>
        </span>
      </template>
      <template v-else>
        <UserAvatar
          size="sm"
          :name="selected[0]!.displayName"
          :is-agent="selected[0]!.isAgent ?? false"
          :src="pictureOf(selected[0]!)"
        />
        <span class="flex-1 truncate">{{ selected[0]!.displayName }}</span>
      </template>

      <X
        v-if="selected.length > 0 && !disabled"
        class="text-muted-foreground hover:text-foreground size-3.5 flex-none"
        role="button"
        aria-label="Clear selection"
        @click.stop="clear"
      />
      <ChevronsUpDown class="text-muted-foreground size-3 flex-none" aria-hidden="true" />
    </button>

    <div
      v-if="open"
      class="bg-popover border-border absolute z-50 mt-1 w-full overflow-hidden rounded-lg border shadow-md"
    >
      <div class="border-border flex items-center gap-2 border-b px-2.5">
        <Search class="text-muted-foreground size-3.5 flex-none" aria-hidden="true" />
        <input
          ref="search"
          v-model="query"
          type="text"
          class="h-8 w-full bg-transparent text-sm outline-none"
          :placeholder="searchPlaceholder"
          :aria-label="searchPlaceholder"
          @keydown="onKeydown"
        />
      </div>

      <div v-if="loading" class="text-muted-foreground flex items-center gap-2 px-2.5 py-3 text-xs">
        <Loader2 class="size-3.5 animate-spin" aria-hidden="true" />
        Loading…
      </div>

      <p v-else-if="matches.length === 0" class="text-muted-foreground px-2.5 py-3 text-xs">
        {{ emptyText }}
      </p>

      <ul v-else role="listbox" class="max-h-64 overflow-y-auto py-1">
        <li v-for="(option, index) in matches" :key="option.userId">
          <p
            v-if="index === firstAgent"
            class="text-muted-foreground px-2.5 pt-2 pb-1 text-[10px] font-medium tracking-wide uppercase"
          >
            Agents
          </p>
          <p
            v-else-if="index === 0 && firstAgent !== 0"
            class="text-muted-foreground px-2.5 pt-2 pb-1 text-[10px] font-medium tracking-wide uppercase"
          >
            People
          </p>
          <button
            type="button"
            role="option"
            :aria-selected="selectedIds.includes(option.userId)"
            :disabled="option.disabled"
            class="flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-sm disabled:opacity-50"
            :class="index === active && !option.disabled && 'bg-accent text-accent-foreground'"
            @click="choose(option)"
            @mousemove="active = index"
          >
            <UserAvatar
              size="sm"
              :name="option.displayName"
              :is-agent="option.isAgent ?? false"
              :src="pictureOf(option)"
            />
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ option.displayName }}</span>
              <span v-if="option.email" class="text-muted-foreground block truncate text-[10.5px]">
                {{ option.email }}
              </span>
            </span>
            <span v-if="option.hint" class="text-muted-foreground flex-none text-[10.5px]">
              {{ option.hint }}
            </span>
            <Check
              v-if="selectedIds.includes(option.userId)"
              class="size-3.5 flex-none"
              aria-hidden="true"
            />
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>
