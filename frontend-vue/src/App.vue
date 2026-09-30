<script setup lang="ts">
import UiPageState from '@/components/UiPageState.vue'
// The wrapper, not vue-sonner's own Toaster: it dresses the toast in the app's popover
// tokens and its icon set, so a toast belongs to the same screen it appears over.
import { Toaster } from '@/components/ui/sonner'
import { activityToasterId } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'
import { useUiStore } from '@/stores/ui'

// The router guard resolves the session before the first navigation completes, so this
// only covers the frame before that: without it the app flashes the login page at
// someone who is, in fact, signed in.
const session = useSessionStore()
// A toast is a card over the page, so it follows the page's theme rather than sonner's
// own default of light.
const ui = useUiStore()
</script>

<template>
  <UiPageState v-if="!session.isResolved" state="loading" />
  <RouterView v-else />
  <Toaster :theme="ui.resolvedTheme" position="bottom-right" rich-colors close-button />
  <Toaster :id="activityToasterId" :theme="ui.resolvedTheme" position="top-right" rich-colors close-button />
</template>
