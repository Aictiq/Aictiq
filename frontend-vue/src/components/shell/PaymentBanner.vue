<script setup lang="ts">
import { TriangleAlert } from '@lucide/vue'
import { computed } from 'vue'
import { RouterLink } from 'vue-router'

import type { Subscription } from '@/api/billing'
import { shellBanner } from '@/lib/billing'
import { orgSettingsPath } from '@/router/paths'
import { cn } from '@/lib/utils'

/**
 * "A payment failed" - or "your evaluation ends in five days" - across the
 * top of the app, for the organization it concerns. Every member sees it, not only the
 * Owner: they are the ones who will find their writes refused, and a 409 out of nowhere is
 * worse than a warning a fortnight ahead. Owners get the way to fix it; everyone else is
 * told whom to ask.
 *
 * Both causes end in the same `readOnly`, so `shellBanner` names the one that actually
 * happened: an organization whose card was declined is never told its evaluation ran out.
 */
const props = defineProps<{
  slug: string
  subscription:
    | Pick<Subscription, 'enabled' | 'readOnly' | 'graceEndsAt' | 'paymentFailedAt' | 'evaluation'>
    | null
    | undefined
  isOwner: boolean
}>()

const banner = computed(() => shellBanner(props.subscription, props.isOwner))
</script>

<template>
  <div
    v-if="banner"
    role="alert"
    :class="
      cn(
        'flex flex-wrap items-center gap-2 border-b px-4 py-2 text-sm',
        banner.tone === 'danger'
          ? 'border-destructive/40 bg-destructive/10 text-destructive'
          : 'border-warning/40 bg-warning/10',
      )
    "
  >
    <TriangleAlert class="size-4 shrink-0" aria-hidden="true" />
    <span class="min-w-0 flex-1">{{ banner.message }}</span>
    <RouterLink
      v-if="banner.action === 'manage' || banner.action === 'plan'"
      :to="orgSettingsPath(slug, 'billing')"
      class="font-medium underline underline-offset-2"
    >
      {{ banner.action === 'plan' ? 'View plans' : 'Update payment method' }}
    </RouterLink>
  </div>
</template>
