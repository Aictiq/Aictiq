<script setup lang="ts">
import { X } from '@lucide/vue'
import { computed } from 'vue'

import { cn } from '@/lib/utils'

/**
 * One label, everywhere it appears: the picker's selection, an item's label list, the
 * labels settings table.
 *
 * `color` is per-label user data — arbitrary, unlike everything else in the interface —
 * so it is the one place a literal colour is legitimate. It never becomes the text
 * colour or a solid fill, both of which can turn illegible depending on the theme; it
 * only tints the border and lights a small dot, the way `ProjectBadge` treats a
 * project's colour. Text stays on the design tokens, so the chip reads in both themes
 * whatever a person picked.
 */
const props = withDefaults(
  defineProps<{
    name: string
    color?: string | null
    group?: string | null
    removable?: boolean
    class?: string
  }>(),
  { color: null, group: null, removable: false, class: undefined },
)

const emit = defineEmits<{ remove: [] }>()

const borderStyle = computed(() =>
  props.color ? { borderColor: `${props.color}66` } : undefined,
)
const dotStyle = computed(() => (props.color ? { backgroundColor: props.color } : undefined))
</script>

<template>
  <span
    :class="
      cn(
        'text-foreground inline-flex max-w-full items-center gap-1.5 rounded-sm border px-1.5 py-0.5 text-[11px] leading-none',
        !color && 'border-border',
        props.class,
      )
    "
    :style="borderStyle"
  >
    <span
      :class="cn('size-1.5 flex-none rounded-full', !color && 'bg-muted-foreground/50')"
      :style="dotStyle"
      aria-hidden="true"
    />
    <span class="truncate">
      <span v-if="group" class="text-muted-foreground">{{ group }}: </span>{{ name }}
    </span>
    <button
      v-if="removable"
      type="button"
      class="text-muted-foreground hover:text-foreground -mr-0.5 flex-none"
      :aria-label="`Remove ${name}`"
      @click.stop="emit('remove')"
    >
      <X class="size-3" aria-hidden="true" />
    </button>
  </span>
</template>
