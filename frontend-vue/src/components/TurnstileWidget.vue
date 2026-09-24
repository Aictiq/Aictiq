<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef } from 'vue'

import {
  loadTurnstileScript,
  loadTurnstileSiteKey,
  turnstileSiteKey,
  type TurnstileApi,
} from '@/utils/turnstile'

/**
 * The Turnstile challenge for one form. Renders nothing on an instance without it.
 *
 * A token is good for one submission: the API spends it with Cloudflare whether the form
 * then succeeds or not. So the parent calls `reset()` after every attempt, and the widget
 * solves a fresh one - usually without the visitor noticing.
 */
const props = defineProps<{
  /** Must match what the API expects for the endpoint this form posts to. */
  action: string
}>()

const token = defineModel<string | null>('token', { default: null })

const container = useTemplateRef<HTMLElement>('container')
const failed = ref(false)

let api: TurnstileApi | null = null
let widgetId: string | null = null
let disposed = false

onMounted(async () => {
  const siteKey = await loadTurnstileSiteKey()
  if (!siteKey || disposed) return
  // The container is behind the same v-if the site key just satisfied.
  await nextTick()

  try {
    api = await loadTurnstileScript()
  } catch {
    failed.value = true
    return
  }
  if (disposed || !container.value) return

  widgetId = api.render(container.value, {
    sitekey: siteKey,
    action: props.action,
    theme: 'auto',
    size: 'flexible',
    callback: (solved) => {
      failed.value = false
      token.value = solved
    },
    'expired-callback': () => (token.value = null),
    'timeout-callback': () => (token.value = null),
    'error-callback': () => {
      token.value = null
      failed.value = true
    },
  })
})

onBeforeUnmount(() => {
  disposed = true
  if (api && widgetId) api.remove(widgetId)
})

function reset() {
  token.value = null
  if (api && widgetId) api.reset(widgetId)
}

defineExpose({ reset })
</script>

<template>
  <div v-if="turnstileSiteKey" class="space-y-1">
    <div ref="container" class="min-h-[65px]" />
    <p v-if="failed" role="alert" class="text-destructive text-xs">
      The verification check could not load. Check your connection or disable content blockers,
      then reload the page.
    </p>
  </div>
</template>
