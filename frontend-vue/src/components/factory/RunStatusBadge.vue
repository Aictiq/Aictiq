<script setup lang="ts">
import { computed } from 'vue'

import type { RunStatus } from '@/api/runs'
import { isLiveRun, runStatusLabel, runStatusTone } from '@/lib/runs'

/**
 * The one pill every run surface shares — the item's history section, the Factory's
 * Runs tab and the run page itself — so "running" looks like running everywhere.
 */
const props = defineProps<{ status: RunStatus }>()

const tone = computed(() => runStatusTone[props.status])
</script>

<template>
  <span
    class="inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[11px] font-medium whitespace-nowrap"
    :class="tone"
  >
    <span
      v-if="isLiveRun(status) && status === 'running'"
      class="bg-current size-1.5 animate-pulse rounded-full"
      aria-hidden="true"
    />
    {{ runStatusLabel[status] }}
  </span>
</template>
