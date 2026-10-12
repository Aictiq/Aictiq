<script setup lang="ts">
import { CircleArrowUp, CodeXml, ExternalLink, Globe } from '@lucide/vue'

import AppShell from '@/components/shell/AppShell.vue'
import { useRelease } from '@/composables/useRelease'

/**
 * What used to be the application footer: the running version, a pending update, and
 * where the project lives. It is a page of its own so the frame gives that line of
 * vertical space back to every other page.
 */
const { version, update } = useRelease()

const links = [
  { href: 'https://aictiq.com/', label: 'Website', detail: 'aictiq.com', icon: Globe },
  {
    href: 'https://github.com/Aictiq/Aictiq',
    label: 'Source code',
    detail: 'github.com/Aictiq/Aictiq',
    icon: CodeXml,
  },
]
</script>

<template>
  <AppShell>
    <div class="w-full max-w-2xl px-6 py-6">
      <header class="border-border border-b pb-3">
        <h1 class="text-xl font-semibold tracking-tight">About Aictiq</h1>
        <p class="text-muted-foreground mt-1 text-[12.5px]">
          Work tracking where people and coding agents share the same items.
        </p>
      </header>

      <dl class="border-border mt-4 divide-y rounded-md border text-[13px]">
        <div class="flex items-center justify-between gap-4 px-4 py-3">
          <dt class="text-muted-foreground">Version</dt>
          <dd class="font-mono" data-testid="about-version">
            {{ version ?? 'Unknown' }}
          </dd>
        </div>
        <div
          v-if="update"
          class="flex items-center justify-between gap-4 px-4 py-3"
        >
          <dt class="text-muted-foreground">Update</dt>
          <dd>
            <a
              :href="update.url"
              class="text-primary inline-flex items-center gap-1.5 underline underline-offset-2"
              target="_blank"
              rel="noopener noreferrer"
            >
              <CircleArrowUp class="size-3.5" aria-hidden="true" />
              {{ update.version }} is available
            </a>
          </dd>
        </div>
      </dl>

      <ul class="mt-4 grid gap-3 sm:grid-cols-2">
        <li v-for="link in links" :key="link.href">
          <a
            :href="link.href"
            target="_blank"
            rel="noopener noreferrer"
            class="border-border hover:bg-accent flex items-center gap-3 rounded-md border px-4 py-3 transition-colors"
          >
            <component :is="link.icon" class="text-muted-foreground size-4" aria-hidden="true" />
            <span class="min-w-0 flex-1">
              <span class="block text-[13px] font-medium">{{ link.label }}</span>
              <span class="text-muted-foreground block truncate text-xs">{{ link.detail }}</span>
            </span>
            <ExternalLink class="text-muted-foreground size-3.5" aria-hidden="true" />
          </a>
        </li>
      </ul>
    </div>
  </AppShell>
</template>
