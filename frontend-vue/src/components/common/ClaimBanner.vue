<script setup lang="ts">
import { Bot, TriangleAlert } from '@lucide/vue'
import { computed } from 'vue'

import type { ActivityActor } from '@/api/agent-activity'
import { avatarUrl } from '@/api/profile'
import UserAvatar from '@/components/common/UserAvatar.vue'
import { Button } from '@/components/ui/button'
import { canRelease, claimStatus, since, staleAfterMinutes } from '@/lib/claims'
import { cn } from '@/lib/utils'

/**
 * "Claimed by 🤖 claude-dev · heartbeat 2 min ago".
 *
 * The banner exists because a claim is invisible in every other column: the assignee looks
 * the same whether an agent is mid-run or gave up an hour ago. A stale claim is called out
 * rather than quietly shown, because the useful action then is to take the item back.
 *
 * When the claim belongs to a live run, the run is the truth: a runner heartbeats its run
 * row, not the item's claim, so the claim's own heartbeat would look stale no matter how
 * healthy the run is. The banner then says whose machine has been on it since when, and
 * the way in is the log — cancelling, not releasing, is how a run ends.
 */
const props = withDefaults(
  defineProps<{
    claimedBy: string | null
    claimedAt?: string | null
    claimHeartbeatAt?: string | null
    /** Resolved through the directory; null while it loads or if the account is gone. */
    holder?: ActivityActor | null
    currentUserId?: string | null
    projectRole?: string | null
    releasing?: boolean
    /** The live run holding this claim, when there is one. */
    liveRun?: { status?: string; runnerName: string | null; startedAt: string | null } | null
    /** Where "View log" goes — offered to factory operators only. */
    runLogTo?: string | null
    class?: string
  }>(),
  {
    claimedAt: null,
    claimHeartbeatAt: null,
    holder: null,
    currentUserId: null,
    projectRole: null,
    releasing: false,
    liveRun: null,
    runLogTo: null,
    class: undefined,
  },
)

const emit = defineEmits<{ release: [] }>()

const status = computed(() => claimStatus(props))
const name = computed(() => props.holder?.displayName ?? 'someone')
const isAgent = computed(() => props.holder?.isAgent ?? false)
const heartbeat = computed(() => since(props.claimHeartbeatAt ?? props.claimedAt))
const mayRelease = computed(
  () =>
    !props.liveRun &&
    canRelease(status.value, {
      claimedBy: props.claimedBy,
      currentUserId: props.currentUserId,
      projectRole: props.projectRole,
    }),
)
</script>

<template>
  <div
    v-if="status !== 'none'"
    role="status"
    :class="
      cn(
        'flex flex-wrap items-center gap-2 rounded-md border px-3 py-2 text-sm',
        status === 'stale' && !liveRun
          ? 'border-warning/40 bg-warning/10'
          : 'border-primary/35 bg-primary/5',
        props.class,
      )
    "
  >
    <UserAvatar
      :name="name"
      :is-agent="isAgent"
      :src="holder ? avatarUrl(holder.id, holder.avatarKey) : null"
      size="sm"
    />
    <span class="flex items-center gap-1">
      <Bot v-if="isAgent" class="size-3.5" aria-hidden="true" />
      <strong class="font-medium">{{ name }}</strong>
    </span>

    <template v-if="liveRun">
      <span v-if="liveRun.status === 'queued'" class="text-muted-foreground">
        has a run queued · waiting for a runner
      </span>
      <span v-else class="text-muted-foreground">
        is running<template v-if="liveRun.runnerName"> on {{ liveRun.runnerName }}</template>
        <template v-if="liveRun.startedAt"> · started {{ since(liveRun.startedAt) }}</template>
      </span>
      <RouterLink
        v-if="runLogTo"
        :to="runLogTo"
        class="text-primary ml-auto text-sm underline underline-offset-2"
        data-testid="claim-banner-log-link"
        >View log</RouterLink
      >
    </template>
    <template v-else>
      <span v-if="status === 'live'" class="text-muted-foreground">
        is working on this · heartbeat {{ heartbeat }}
      </span>
      <span v-else class="text-warning flex items-center gap-1">
        <TriangleAlert class="size-3.5" aria-hidden="true" />
        claimed it, but has not checked in for {{ heartbeat.replace(' ago', '') }} — anyone may
        claim it again after {{ staleAfterMinutes }} minutes.
      </span>

      <Button
        v-if="mayRelease"
        variant="ghost"
        size="sm"
        class="ml-auto"
        :disabled="releasing"
        @click="emit('release')"
      >
        Release
      </Button>
    </template>
  </div>
</template>
