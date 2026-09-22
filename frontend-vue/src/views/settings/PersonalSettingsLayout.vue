<script setup lang="ts">
import { RouterView } from 'vue-router'

import SettingsShell from '@/components/settings/SettingsShell.vue'
import { personalSettingsLinks } from '@/router/paths'
import { useSessionStore } from '@/stores/session'

/**
 * The account's own settings. Deliberately not organization-scoped: a password, an
 * avatar and a personal access token follow the person across every organization they
 * are in, so putting a slug in these URLs would suggest four different profiles.
 */
const session = useSessionStore()
</script>

<template>
  <SettingsShell
    eyebrow="Account"
    :title="session.user?.fullName ?? 'Settings'"
    description="Your details, how you sign in, and the tokens that let your tools act as you."
    :links="personalSettingsLinks"
  >
    <RouterView />
  </SettingsShell>
</template>
