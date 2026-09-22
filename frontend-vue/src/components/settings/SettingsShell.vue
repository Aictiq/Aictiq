<script setup lang="ts">
import AppShell from '@/components/shell/AppShell.vue'
import SettingsTabs from '@/components/settings/SettingsTabs.vue'
import type { SettingsLink } from '@/router/paths'

/**
 * One frame for every settings hub: personal, organization and project. The eyebrow says
 * *whose* settings these are, which is the question a shared link has to answer before
 * anything else on the page means something.
 *
 * The container is wide because the roster tabs need it; forms narrow themselves with
 * `SettingsSection`, so a text field is never a 900px line to read back.
 */
defineProps<{
  eyebrow: string
  title: string
  description?: string
  links: SettingsLink[]
}>()
</script>

<template>
  <AppShell>
    <div class="w-full min-w-0 px-4 py-5 sm:px-6 sm:py-6">
      <header class="border-border border-b pb-4">
        <div class="min-w-0">
          <p class="font-label">{{ eyebrow }}</p>
          <h1 class="truncate text-xl font-semibold tracking-tight">{{ title }}</h1>
          <p v-if="description" class="text-muted-foreground mt-1 text-[12.5px]">
            {{ description }}
          </p>
        </div>
      </header>

      <SettingsTabs :links="links" />

      <div class="py-6"><slot /></div>
    </div>
  </AppShell>
</template>
