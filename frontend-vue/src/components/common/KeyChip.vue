<script setup lang="ts">
import { computed } from 'vue'

import { formatShortcut } from '@/composables/useShortcuts'
import { cn } from '@/lib/utils'

/**
 * A small mono chip. Two jobs, because they look identical in the design: a keyboard hint
 * ("⌘K", "ESC") and an item key ("ACME-123"). `binding` renders a shortcut through the
 * platform formatter; `label` renders text as-is.
 */
const props = defineProps<{ binding?: string; label?: string; class?: string }>()

const text = computed(() => (props.binding ? formatShortcut(props.binding) : (props.label ?? '')))
</script>

<template>
  <span
    :class="
      cn(
        'border-border text-muted-foreground inline-flex items-center rounded-sm border px-1 py-px font-mono text-[10px] leading-none',
        props.class,
      )
    "
  >
    {{ text }}
  </span>
</template>
