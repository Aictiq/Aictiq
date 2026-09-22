<script lang="ts">
/**
 * The preset swatches offered to an inline create. A label's `color` is shared by both
 * themes at once, so these are fixed hex values rather than a design token - chosen to
 * echo the accent hues in `src/assets/index.css` (the amber primary, the success /
 * info / warning / agent / destructive tones) without literally reading the same hue
 * that would only be correct in one theme.
 */
export const presetLabelColors: { name: string; hex: string }[] = [
  { name: 'Amber', hex: '#eda45c' },
  { name: 'Green', hex: '#5fb583' },
  { name: 'Blue', hex: '#6aa9d8' },
  { name: 'Purple', hex: '#a882d9' },
  { name: 'Red', hex: '#d97757' },
  { name: 'Slate', hex: '#8a8d94' },
]
</script>

<script setup lang="ts">
import { Check, ChevronsUpDown, Loader2, Plus, Search } from '@lucide/vue'
import { computed, nextTick, ref, watch } from 'vue'

import { createLabel, type Label } from '@/api/labels'
import LabelChip from '@/components/common/LabelChip.vue'
import { cn } from '@/lib/utils'
import { ApiError } from '@/utils/api'

/**
 * Picking a project's labels - and, when the caller may, minting a new one on the spot
 * from whatever was typed. Hand-rolled for the same reason `UserSelect` is: the filter
 * box owns the keyboard, and a component with no portal is one a test can drive directly.
 *
 * `canCreate` is a prop rather than a permission check in here on purpose - the picker
 * has no way to know whether the person using it is a project Guest, and guessing wrong
 * in either direction is worse than making the caller say so.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string[]
    labels: Label[]
    slug: string
    projectKey: string
    canCreate?: boolean
    disabled?: boolean
    placeholder?: string
    class?: string
  }>(),
  {
    canCreate: false,
    disabled: false,
    placeholder: 'Add labels…',
    class: undefined,
  },
)

const emit = defineEmits<{
  'update:modelValue': [string[]]
  /** A label minted inline, so a caller holding its own copy of the project's labels can append it. */
  created: [Label]
}>()

const open = ref(false)
const query = ref('')
const active = ref(0)
const creating = ref(false)
const createError = ref<string | null>(null)
const newColor = ref(presetLabelColors[0]!.hex)
const root = ref<HTMLElement | null>(null)
const search = ref<HTMLInputElement | null>(null)

const selected = computed(() =>
  props.modelValue
    .map((id) => props.labels.find((label) => label.id === id))
    .filter((label): label is Label => label !== undefined),
)

const matches = computed(() => {
  const needle = query.value.trim().toLowerCase()
  if (!needle) return props.labels
  return props.labels.filter(
    (label) =>
      label.name.toLowerCase().includes(needle) ||
      (label.group ?? '').toLowerCase().includes(needle),
  )
})

/** Nothing to create when the typed text already names a label, case-insensitively. */
const canOfferCreate = computed(() => {
  const trimmed = query.value.trim()
  if (!trimmed || !props.canCreate) return false
  return !props.labels.some((label) => label.name.toLowerCase() === trimmed.toLowerCase())
})

watch(matches, () => {
  active.value = 0
})

watch(open, async (isOpen) => {
  createError.value = null
  if (!isOpen) {
    query.value = ''
    return
  }
  active.value = 0
  await nextTick()
  search.value?.focus()
})

function toggle() {
  if (props.disabled) return
  open.value = !open.value
}

function isSelected(id: string) {
  return props.modelValue.includes(id)
}

function toggleLabel(label: Label) {
  const next = isSelected(label.id)
    ? props.modelValue.filter((id) => id !== label.id)
    : [...props.modelValue, label.id]
  emit('update:modelValue', next)
  // Stays open: picking several labels one at a time is the normal case, same as UserSelect.
}

function remove(id: string) {
  emit('update:modelValue', props.modelValue.filter((existing) => existing !== id))
}

async function create() {
  const name = query.value.trim()
  if (!name || creating.value) return

  creating.value = true
  createError.value = null
  try {
    const label = await createLabel(props.slug, props.projectKey, {
      name,
      color: newColor.value,
    })
    emit('created', label)
    emit('update:modelValue', [...props.modelValue, label.id])
    query.value = ''
    search.value?.focus()
  } catch (error) {
    createError.value = error instanceof ApiError ? error.title : 'Could not create the label.'
  } finally {
    creating.value = false
  }
}

function move(step: 1 | -1) {
  const count = matches.value.length + (canOfferCreate.value ? 1 : 0)
  if (count === 0) return
  active.value = (active.value + step + count) % count
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
    if (active.value < matches.value.length) {
      const label = matches.value[active.value]
      if (label) toggleLabel(label)
    } else if (canOfferCreate.value) {
      create()
    }
  } else if (event.key === 'Escape') {
    event.preventDefault()
    open.value = false
  }
}

function onFocusout(event: FocusEvent) {
  const next = event.relatedTarget
  if (next instanceof Node && root.value?.contains(next)) return
  open.value = false
}
</script>

<template>
  <div ref="root" :class="cn('relative', props.class)" @focusout="onFocusout">
    <!--
      A `div`, not a `button`: the selected chips it contains carry their own remove
      buttons, and a button cannot legally nest one. Role and keydown make it behave
      like one anyway.
    -->
    <div
      role="button"
      tabindex="0"
      class="border-border bg-background focus-visible:ring-ring flex min-h-8 w-full flex-wrap items-center gap-1.5 rounded-lg border px-2.5 py-1 text-left text-sm focus-visible:ring-2 focus-visible:outline-none aria-disabled:opacity-50"
      :aria-disabled="disabled"
      aria-label="Select labels"
      :aria-expanded="open"
      aria-haspopup="listbox"
      @click="toggle"
      @keydown.enter.prevent="toggle"
      @keydown.space.prevent="toggle"
    >
      <template v-if="selected.length === 0">
        <span class="text-muted-foreground py-0.5">{{ placeholder }}</span>
      </template>
      <template v-else>
        <LabelChip
          v-for="label in selected"
          :key="label.id"
          :name="label.name"
          :color="label.color"
          :group="label.group"
          removable
          @remove="remove(label.id)"
          @click.stop
        />
      </template>
      <ChevronsUpDown class="text-muted-foreground ml-auto size-3 flex-none" aria-hidden="true" />
    </div>

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
          placeholder="Search labels…"
          aria-label="Search labels"
          @keydown="onKeydown"
        />
      </div>

      <p v-if="labels.length === 0 && !canOfferCreate" class="text-muted-foreground px-2.5 py-3 text-xs">
        No labels yet.
      </p>
      <p v-else-if="matches.length === 0 && !canOfferCreate" class="text-muted-foreground px-2.5 py-3 text-xs">
        No labels match.
      </p>

      <ul v-if="matches.length > 0" role="listbox" class="max-h-56 overflow-y-auto py-1">
        <li v-for="(label, index) in matches" :key="label.id">
          <button
            type="button"
            role="option"
            :aria-selected="isSelected(label.id)"
            class="flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-sm"
            :class="index === active && 'bg-accent text-accent-foreground'"
            @click="toggleLabel(label)"
            @mousemove="active = index"
          >
            <LabelChip :name="label.name" :color="label.color" :group="label.group" class="min-w-0 flex-1" />
            <Check v-if="isSelected(label.id)" class="size-3.5 flex-none" aria-hidden="true" />
          </button>
        </li>
      </ul>

      <div v-if="canOfferCreate" class="border-border space-y-2 border-t p-2.5">
        <button
          type="button"
          class="hover:bg-accent hover:text-accent-foreground flex w-full items-center gap-2 rounded-md px-1.5 py-1 text-left text-sm"
          :class="active === matches.length && 'bg-accent text-accent-foreground'"
          :disabled="creating"
          @click="create"
          @mousemove="active = matches.length"
        >
          <Loader2 v-if="creating" class="size-3.5 flex-none animate-spin" aria-hidden="true" />
          <Plus v-else class="size-3.5 flex-none" aria-hidden="true" />
          <span class="min-w-0 flex-1 truncate">Create "{{ query.trim() }}"</span>
        </button>
        <div class="flex items-center gap-1.5 pl-1.5" role="radiogroup" aria-label="Colour">
          <button
            v-for="preset in presetLabelColors"
            :key="preset.hex"
            type="button"
            class="size-4 flex-none rounded-full ring-offset-1 outline-none"
            :class="newColor === preset.hex && 'ring-ring ring-2'"
            :style="{ backgroundColor: preset.hex }"
            :aria-label="preset.name"
            role="radio"
            :aria-checked="newColor === preset.hex"
            @click="newColor = preset.hex"
          />
        </div>
        <p v-if="createError" class="text-destructive text-xs">{{ createError }}</p>
      </div>
    </div>
  </div>
</template>
