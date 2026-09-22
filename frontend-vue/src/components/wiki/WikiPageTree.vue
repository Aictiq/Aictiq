<script setup lang="ts">
import { computed } from 'vue'

import type { WikiTreePage } from '@/api/wiki'
import { wikiOutline } from '@/lib/wiki'

const props = withDefaults(
  defineProps<{
    pages: WikiTreePage[]
    selectedId?: string | null
    disabled?: boolean
  }>(),
  { selectedId: null, disabled: false },
)

const emit = defineEmits<{ select: [page: WikiTreePage] }>()

/** One shared tree ordering for the wiki sidebar and every page picker. */
const outline = computed(() => {
  return wikiOutline(props.pages)
})
</script>

<template>
  <div
    v-for="{ page, depth } in outline"
    :key="page.id"
    class="group flex items-center rounded hover:bg-muted"
    :class="{ 'bg-muted': selectedId === page.id }"
  >
    <button
      type="button"
      class="min-w-0 flex-1 truncate py-1 pr-1 text-left text-sm disabled:cursor-not-allowed disabled:opacity-50"
      :class="{ 'text-muted-foreground': depth > 1 }"
      :style="{ paddingLeft: `${0.5 + depth * 0.875}rem` }"
      :title="page.title"
      :disabled="disabled"
      @click="emit('select', page)"
    >
      {{ page.title }}
    </button>
    <slot name="actions" :page="page" :depth="depth" />
  </div>
</template>
