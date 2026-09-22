<script setup lang="ts">
import { Toaster } from 'vue-sonner'

import UiPageState from '@/components/UiPageState.vue'
import { useSessionStore } from '@/stores/session'

// The router guard resolves the session before the first navigation completes, so this
// only covers the frame before that: without it the app flashes the login page at
// someone who is, in fact, signed in.
const session = useSessionStore()
</script>

<template>
  <UiPageState v-if="!session.isResolved" state="loading" />
  <RouterView v-else />
  <Toaster rich-colors close-button />
</template>
