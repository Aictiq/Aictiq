<script setup lang="ts">
import { CopyPlus, Link } from '@lucide/vue'

import { cn } from '@/lib/utils'

/** A list row's copy-link and duplicate buttons. Like the row selects, it keeps its clicks
 * and keys to itself so using a button never opens the row's item as well. */
const props = withDefaults(
  defineProps<{ itemKey: string; busy?: boolean; canDuplicate?: boolean; class?: string }>(),
  { busy: false, canDuplicate: true, class: undefined },
)
const emit = defineEmits<{ copyLink: []; duplicate: [] }>()
</script>

<template>
  <span
    :class="cn('inline-flex items-center gap-0.5', props.class)"
    @click.stop
    @mousedown.stop
    @keydown.stop
  >
    <button
      type="button"
      class="text-muted-foreground hover:text-foreground hover:bg-accent rounded p-1"
      :aria-label="`Copy link to ${itemKey}`"
      title="Copy link"
      @click="emit('copyLink')"
    >
      <Link class="size-3.5" aria-hidden="true" />
    </button>
    <button
      v-if="canDuplicate"
      type="button"
      class="text-muted-foreground hover:text-foreground hover:bg-accent rounded p-1 disabled:opacity-50"
      :aria-label="`Duplicate ${itemKey}`"
      title="Duplicate"
      :disabled="busy"
      @click="emit('duplicate')"
    >
      <CopyPlus class="size-3.5" aria-hidden="true" />
    </button>
  </span>
</template>
