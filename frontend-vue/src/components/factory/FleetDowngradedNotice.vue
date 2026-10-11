<script setup lang="ts">
import { TriangleAlert } from '@lucide/vue'
import { RouterLink } from 'vue-router'

import { orgSettingsPath } from '@/router/paths'

/**
 * Fleet mode is still switched on, but the plan no longer includes it. Nothing is lost - the
 * tags and settings stay - but dispatch ignores them, which is worth saying wherever someone
 * would otherwise expect tags to decide where a run goes.
 */
defineProps<{ slug: string }>()
</script>

<template>
  <p
    role="status"
    class="flex items-start gap-1.5 rounded border border-amber-400/50 bg-amber-400/10 px-3 py-2 text-xs"
    data-testid="fleet-downgraded"
  >
    <TriangleAlert class="mt-0.5 size-3.5 shrink-0 text-amber-600" aria-hidden="true" />
    <span>
      Fleet mode is on, but this organization's plan no longer includes it. Runs dispatch as if
      fleet mode were off - runner tags and required tags are kept, and ignored - until the
      organization is on a paid plan again.
      <RouterLink
        :to="orgSettingsPath(slug, 'billing')"
        class="font-medium underline underline-offset-2"
      >
        See billing
      </RouterLink>
    </span>
  </p>
</template>
