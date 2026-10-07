<script setup lang="ts">
import { TriangleAlert } from '@lucide/vue'
import { RouterLink } from 'vue-router'

import type { PlanLimitRefusal } from '@/lib/billing'
import { orgSettingsPath } from '@/router/paths'

/**
 * A 402 plan-limit refusal said inline, in the server's words. When a free-plan limit
 * caused it, paying for Hosted lifts it, so the way there sits next to the reason; a paid
 * organization refused for attachments gets the reason alone, because there is nothing
 * larger to buy.
 */
defineProps<{
  slug: string
  refusal: PlanLimitRefusal
}>()
</script>

<template>
  <p role="alert" class="text-destructive flex items-start gap-1.5 text-xs" data-testid="plan-limit">
    <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
    <span>
      {{ refusal.message }}
      <RouterLink
        v-if="refusal.upgrade"
        :to="orgSettingsPath(slug, 'billing')"
        class="font-medium underline underline-offset-2"
        data-testid="plan-limit-upgrade"
      >
        Upgrade to Hosted
      </RouterLink>
    </span>
  </p>
</template>
