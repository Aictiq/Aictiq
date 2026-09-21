<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'

import AppShell from '@/components/shell/AppShell.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import CreateOrganizationDialog from '@/components/shell/CreateOrganizationDialog.vue'
import { Button } from '@/components/ui/button'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { getOrganizationOverview } from '@/api/analytics'

/**
 * Also the landing place for someone who belongs to no organization at all. The settings
 * hubs are addressed by slug now, so there is no organization page to send a person to
 * before they have one — this is where the first one gets created, and where the legacy
 * `/settings/organization` link lands when there is nothing to redirect it to.
 */
const session = useSessionStore()
const organizations = useOrganizationsStore()
const router = useRouter()

const creating = ref(false)
const slug = computed(() => organizations.currentSlug)
const overview = useQuery({ queryKey: computed(() => ['organization-overview', slug.value]), enabled: computed(() => Boolean(slug.value)), queryFn: () => getOrganizationOverview(slug.value!) })
const overviewData = computed(() => overview.data.value)

onMounted(async () => {
  await organizations.load()
  if (organizations.isEmpty) await router.replace('/onboarding')
})
</script>

<template>
  <AppShell>
    <div class="border-border flex items-end gap-4 border-b px-6 py-5">
      <div class="min-w-0 flex-1">
        <h1 class="text-xl font-semibold tracking-tight">Welcome, {{ session.user?.firstName }}</h1>
        <p class="text-muted-foreground mt-1 text-[12.5px]">Your organization at a glance.</p>
      </div>
    </div>

    <EmptyState
      v-if="organizations.isEmpty"
      title="No organization yet"
      description="Everything in Aictiq lives inside one — its people, its projects and its agents. Create it and you are its owner."
      icon="◇"
    >
      <Button @click="creating = true">New organization</Button>
    </EmptyState>

    <template v-else>
      <p v-if="overview.isError.value" class="text-destructive px-6 py-8">Organization health could not be loaded.</p>
      <div v-else class="w-full p-5 sm:p-8">
        <div class="grid gap-3 sm:grid-cols-3"><article class="border-border rounded-lg border p-4"><p class="text-muted-foreground text-sm">Open bugs</p><strong class="mt-1 block text-2xl">{{ overviewData?.openBugs ?? 0 }}</strong></article><article class="border-border rounded-lg border p-4"><p class="text-muted-foreground text-sm">Blocked items</p><strong class="mt-1 block text-2xl">{{ overviewData?.blockedItems ?? 0 }}</strong></article><article class="border-border rounded-lg border p-4"><p class="text-muted-foreground text-sm">Agent actions (24h)</p><strong class="mt-1 block text-2xl">{{ overviewData?.agentActionsLast24Hours ?? 0 }}</strong></article></div>
        <section class="mt-6"><h2 class="font-medium">Projects</h2><div v-if="overviewData?.projects.length" class="mt-3 grid gap-3 md:grid-cols-2"><RouterLink v-for="project in overviewData.projects" :key="project.id" :to="`/o/${slug}/p/${project.key}/portfolio`" class="border-border rounded-lg border p-4 hover:bg-muted/50"><div class="flex justify-between gap-3"><div><strong>{{ project.name }}</strong><span class="text-muted-foreground ml-2 font-mono text-xs">{{ project.key }}</span></div><span class="text-sm">{{ project.openBugs }} bugs · {{ project.blockedItems }} blocked</span></div><div v-for="sprint in project.activeSprints" :key="sprint.id" class="mt-3"><div class="flex justify-between text-sm"><span>{{ sprint.name }}</span><span>{{ sprint.percentDone }}% · {{ sprint.blockedCount }} blocked</span></div><div class="mt-1 h-1.5 overflow-hidden rounded bg-muted"><div class="h-full bg-primary" :style="{ width: `${sprint.percentDone}%` }" /></div></div><p v-if="!project.activeSprints.length" class="text-muted-foreground mt-3 text-sm">No active sprint.</p></RouterLink></div><p v-else class="text-muted-foreground mt-3 text-sm">No visible projects yet.</p></section>
        <div class="mt-6 grid gap-5 lg:grid-cols-2"><section class="border-border rounded-lg border p-4"><h2 class="font-medium">Recent activity</h2><ul class="mt-3 space-y-3 text-sm"><li v-for="activity in overviewData?.recentActivity" :key="`${activity.itemKey}-${activity.at}`"><strong>{{ activity.itemKey }}</strong> {{ activity.field }} <span class="text-muted-foreground">{{ activity.actor?.displayName ?? 'Unknown' }}</span></li><li v-if="!overviewData?.recentActivity.length" class="text-muted-foreground">No recent activity.</li></ul></section><section class="border-border rounded-lg border p-4"><h2 class="font-medium">Agents at work</h2><div class="mt-3 flex flex-wrap gap-2"><span v-for="agent in overviewData?.agentsAtWork" :key="agent.id" class="rounded-full bg-muted px-3 py-1 text-sm">{{ agent.displayName }}</span><p v-if="!overviewData?.agentsAtWork.length" class="text-muted-foreground text-sm">No agent activity in the last day.</p></div></section></div>
      </div>
    </template>

    <CreateOrganizationDialog v-model:open="creating" />
  </AppShell>
</template>
