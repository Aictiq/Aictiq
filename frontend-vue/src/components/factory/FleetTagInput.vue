<script setup lang="ts">
import { TriangleAlert, X } from '@lucide/vue'
import { computed, ref } from 'vue'

import {
  isValidFleetTag,
  maxFleetTags,
  normalizeFleetTag,
  unknownFleetTags,
  unknownFleetTagWarning,
} from '@/lib/fleet'

/**
 * Fleet mode's tag field: the tags as chips, and a text input that adds one on Enter or a
 * comma. Backspace in the empty input takes the last chip back. Suggestions come from the
 * tags runners have actually had, so the common case is picking rather than spelling.
 *
 * `locked` tags are shown first and cannot be removed here - Start run uses them for the
 * playbook's own required tags, which the run requires whatever is added on top. With
 * `warnUnknown`, a tag no runner has ever had gets a warning under the field; it never
 * blocks anything, because an ephemeral runner with that tag may be about to start.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string[]
    /** Every tag a runner of the organization has had. */
    suggestions?: string[]
    locked?: string[]
    id?: string
    placeholder?: string
    /** The prefix of every `data-testid` this field renders. */
    testid?: string
    warnUnknown?: boolean
    disabled?: boolean
    label?: string
  }>(),
  {
    suggestions: () => [],
    locked: () => [],
    id: undefined,
    placeholder: 'Add a tag',
    testid: 'fleet-tags',
    warnUnknown: false,
    disabled: false,
    label: 'Tags',
  },
)

const emit = defineEmits<{ 'update:modelValue': [tags: string[]] }>()

const draft = ref('')
const problem = ref<string | null>(null)

const taken = computed(() => new Set([...props.locked, ...props.modelValue]))

const matches = computed(() => {
  const query = normalizeFleetTag(draft.value)
  return props.suggestions
    .filter((tag) => !taken.value.has(tag) && (!query || tag.includes(query)))
    .slice(0, 8)
})

const unknown = computed(() =>
  props.warnUnknown
    ? unknownFleetTags([...props.locked, ...props.modelValue], props.suggestions)
    : [],
)

function add(raw: string) {
  const tag = normalizeFleetTag(raw)
  if (!tag) return
  if (!isValidFleetTag(tag)) {
    problem.value =
      'A tag is up to 50 lower case letters, digits and . _ : - characters, starting with a letter or digit.'
    return
  }
  problem.value = null
  draft.value = ''
  if (taken.value.has(tag)) return
  if (props.modelValue.length >= maxFleetTags) {
    problem.value = `At most ${maxFleetTags} tags.`
    return
  }
  emit('update:modelValue', [...props.modelValue, tag])
}

function remove(tag: string) {
  emit(
    'update:modelValue',
    props.modelValue.filter((entry) => entry !== tag),
  )
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' || event.key === ',') {
    // Enter adds a tag here; it must not submit the dialog's form underneath.
    event.preventDefault()
    add(draft.value)
  } else if (event.key === 'Backspace' && draft.value === '' && props.modelValue.length > 0) {
    event.preventDefault()
    remove(props.modelValue[props.modelValue.length - 1]!)
  }
}

/** A pasted "gpu, linux" adds both. */
function onInput() {
  if (!draft.value.includes(',')) return
  const parts = draft.value.split(',')
  draft.value = parts.pop() ?? ''
  for (const part of parts) add(part)
}

function onBlur() {
  // A tag typed and left behind is the one most likely to be forgotten, so it is kept.
  if (draft.value.trim()) add(draft.value)
}
</script>

<template>
  <div class="space-y-1" :data-testid="testid">
    <div
      class="border-input focus-within:border-ring focus-within:ring-ring/50 flex min-h-8 flex-wrap items-center gap-1 rounded-lg border px-1.5 py-1 focus-within:ring-3"
      :class="disabled ? 'bg-input/50 opacity-50' : ''"
    >
      <span
        v-for="tag in locked"
        :key="`locked-${tag}`"
        class="bg-muted text-muted-foreground inline-flex h-5 items-center rounded-4xl px-2 font-mono text-[10px]"
        :title="`${tag} is already required`"
        :data-testid="`${testid}-locked`"
      >
        {{ tag }}
      </span>
      <span
        v-for="tag in modelValue"
        :key="tag"
        class="bg-secondary text-secondary-foreground inline-flex h-5 items-center gap-0.5 rounded-4xl pr-0.5 pl-2 font-mono text-[10px]"
        :data-testid="`${testid}-chip`"
      >
        {{ tag }}
        <button
          type="button"
          class="hover:bg-muted rounded-full p-0.5"
          :disabled="disabled"
          :aria-label="`Remove ${tag}`"
          :data-testid="`${testid}-remove`"
          @click="remove(tag)"
        >
          <X class="size-3" aria-hidden="true" />
        </button>
      </span>
      <input
        :id="id"
        v-model="draft"
        type="text"
        autocomplete="off"
        autocapitalize="off"
        spellcheck="false"
        class="placeholder:text-muted-foreground min-w-24 flex-1 bg-transparent px-1 font-mono text-xs outline-none"
        :placeholder="modelValue.length || locked.length ? '' : placeholder"
        :aria-label="id ? undefined : label"
        :disabled="disabled"
        :data-testid="`${testid}-input`"
        @keydown="onKeydown"
        @input="onInput"
        @blur="onBlur"
      />
    </div>
    <!-- Shown whether or not the input has focus: hiding it on blur moved whatever sat below it
         between a click's mousedown and mouseup, so the dialog's Save swallowed the first click. -->
    <div
      v-if="!disabled && matches.length"
      class="flex flex-wrap items-center gap-1 text-[11px]"
      :data-testid="`${testid}-suggestions`"
    >
      <span class="text-muted-foreground">Known:</span>
      <button
        v-for="tag in matches"
        :key="tag"
        type="button"
        class="border-border hover:bg-muted rounded-4xl border px-1.5 font-mono text-[10px]"
        @mousedown.prevent="add(tag)"
      >
        {{ tag }}
      </button>
    </div>
    <p v-if="problem" class="text-destructive text-xs" :data-testid="`${testid}-error`">
      {{ problem }}
    </p>
    <p
      v-for="tag in unknown"
      :key="`unknown-${tag}`"
      class="flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-400"
      :data-testid="`${testid}-unknown`"
    >
      <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      {{ unknownFleetTagWarning(tag) }}
    </p>
  </div>
</template>
