<script setup lang="ts">
import { Bot, TriangleAlert } from '@lucide/vue'
import { computed } from 'vue'

import { claimStatus, since } from '@/lib/claims'
import { cn } from '@/lib/utils'

/**
 * The one-glyph version of `ClaimBanner`, for a row or a card.
 *
 * A claimed item looks identical to an unclaimed one in every column a list already has,
 * so someone scanning a board cannot tell which items are being worked on this minute.
 * The title carries the detail; the glyph carries the fact.
 */
const props = withDefaults(
  defineProps<{
    claimedBy: string | null
    claimHeartbeatAt?: string | null
    /** Resolved name, when the surface has one. The glyph works without it. */
    holderName?: string | null
    class?: string
  }>(),
  { claimHeartbeatAt: null, holderName: null, class: undefined },
)

const status = computed(() => claimStatus(props))
const label = computed(() => {
  const who = props.holderName ?? 'Someone'
  return status.value === 'stale'
    ? `${who} claimed this but has not checked in (${since(props.claimHeartbeatAt)})`
    : `${who} is working on this · heartbeat ${since(props.claimHeartbeatAt)}`
})
</script>

<template>
  <span
    v-if="status !== 'none'"
    :class="cn('inline-flex', status === 'stale' ? 'text-warning' : 'text-primary', props.class)"
    :title="label"
    :aria-label="label"
  >
    <TriangleAlert v-if="status === 'stale'" class="size-3.5" aria-hidden="true" />
    <Bot v-else class="size-3.5" aria-hidden="true" />
  </span>
</template>
