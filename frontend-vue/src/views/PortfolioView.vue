<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { Download } from '@lucide/vue'

import { getOrganizationPortfolio, getProjectPortfolio, type PortfolioItem } from '@/api/portfolio'
import AppShell from '@/components/shell/AppShell.vue'
import { useItemModal } from '@/composables/useItemModal'

const props = defineProps<{ slug: string; projectKey?: string }>()
const itemModal = useItemModal()
const team = ref('')
const label = ref('')
const portfolio = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey ?? 'organization', 'portfolio']),
  queryFn: () => props.projectKey
    ? getProjectPortfolio(props.slug, props.projectKey)
    : getOrganizationPortfolio(props.slug),
})
const all = computed(() => portfolio.data.value ?? [])
const teams = computed(() => [...new Map(all.value
  .filter((item): item is PortfolioItem & { teamId: string } => item.teamId !== null)
  .map(item => [item.teamId, item.teamName ?? item.teamId])).entries()].sort(([, a], [, b]) => a.localeCompare(b)))
const labels = computed(() => [...new Set(all.value.flatMap(item => item.labels.map(entry => entry.name)))].sort((a, b) => a.localeCompare(b)))
const filtered = computed(() => all.value.filter(item =>
  (!team.value || item.teamId === team.value) && (!label.value || item.labels.some(entry => entry.name === label.value)),
))
const grouped = computed(() => {
  const groups = new Map<string, PortfolioItem[]>()
  for (const item of filtered.value) {
    const key = props.projectKey ? '' : item.projectKey
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  return [...groups.entries()]
})
const timelineRange = computed(() => {
  const starts = filtered.value.map(item => new Date(item.createdAt).getTime())
  const ends = filtered.value.map(item => item.dueDate ? new Date(`${item.dueDate}T00:00:00`).getTime() : Date.now())
  const start = Math.min(...starts, Date.now())
  const end = Math.max(...ends, start + 86_400_000)
  return { start, end: Math.max(end, start + 86_400_000) }
})
function timeline(item: PortfolioItem) {
  const range = timelineRange.value
  const start = new Date(item.createdAt).getTime()
  const end = item.dueDate ? new Date(`${item.dueDate}T00:00:00`).getTime() : Date.now()
  const left = Math.max(0, Math.min(100, ((start - range.start) / (range.end - range.start)) * 100))
  const width = Math.max(3, Math.min(100 - left, ((Math.max(end, start + 86_400_000) - start) / (range.end - range.start)) * 100))
  return { left: `${left}%`, width: `${width}%` }
}
function percent(item: PortfolioItem) { return item.rollup.percentDoneByPoints || item.rollup.percentDoneByCount }
function csv(value: string | number | null | undefined) { return `"${String(value ?? '').replaceAll('"', '""')}"` }
function exportCsv() {
  const lines = [
    ['Project', 'Key', 'Type', 'Title', 'State', 'Owner', 'Team', 'Due date', 'Done items', 'Total items', 'Done points', 'Total points'],
    ...filtered.value.map(item => [item.projectKey, item.key, item.type, item.title, item.stateCategory, item.owner?.displayName, item.teamId, item.dueDate, item.rollup.completedCount, item.rollup.totalCount, item.rollup.pointsCompleted, item.rollup.pointsTotal]),
  ].map(row => row.map(csv).join(','))
  const link = document.createElement('a')
  link.href = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv;charset=utf-8' }))
  link.download = 'portfolio.csv'; link.click(); URL.revokeObjectURL(link.href)
}
</script>

<template>
  <AppShell>
    <section class="w-full px-5 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div><h1 class="text-xl font-semibold">Portfolio</h1><p class="text-muted-foreground text-sm">Epic and feature progress across your visible work.</p></div>
        <button class="border-input inline-flex items-center gap-2 rounded-md border px-3 py-2 text-sm" @click="exportCsv"><Download class="size-4" /> Export CSV</button>
      </div>
      <div class="mt-5 flex flex-wrap gap-2">
        <select v-model="team" class="border-input rounded-md border px-2 py-2 text-sm"><option value="">All teams</option><option v-for="[id, name] in teams" :key="id" :value="id">{{ name }}</option></select>
        <select v-model="label" class="border-input rounded-md border px-2 py-2 text-sm"><option value="">All labels</option><option v-for="name in labels" :key="name" :value="name">{{ name }}</option></select>
      </div>
      <p v-if="portfolio.isError.value" class="text-destructive py-10">Portfolio data could not be loaded.</p>
      <div v-else-if="!portfolio.isLoading.value && filtered.length === 0" class="text-muted-foreground py-12 text-center text-sm">No epics or features match these filters.</div>
      <div v-for="[project, items] in grouped" :key="project" class="mt-6">
        <h2 v-if="!projectKey" class="mb-2 text-sm font-medium">{{ items[0]?.projectName ?? project }}</h2>
        <div class="border-border overflow-x-auto rounded-lg border">
          <div class="grid min-w-[760px] grid-cols-[minmax(15rem,1fr)_7rem_8rem_minmax(12rem,1fr)] gap-3 border-b px-4 py-2 text-xs font-medium text-muted-foreground"><span>Item</span><span>Progress</span><span>Due</span><span>Timeline</span></div>
          <div v-for="item in items" :key="item.id" class="grid min-w-[760px] grid-cols-[minmax(15rem,1fr)_7rem_8rem_minmax(12rem,1fr)] items-center gap-3 border-b px-4 py-3 last:border-b-0">
            <button type="button" class="min-w-0 text-left" @click="itemModal.open(item.key)"><div class="flex gap-2"><span class="font-mono text-xs text-muted-foreground">{{ item.key }}</span><span class="text-xs capitalize text-muted-foreground">{{ item.type }}</span></div><p class="truncate text-sm font-medium hover:underline">{{ item.title }}</p><p class="truncate text-xs text-muted-foreground">{{ item.owner?.displayName ?? 'Unassigned' }} · {{ item.stateCategory }}</p></button>
            <div><span class="text-sm">{{ percent(item) }}%</span><div class="mt-1 h-1.5 overflow-hidden rounded bg-muted"><div class="h-full bg-primary" :style="{ width: `${Math.min(100, percent(item))}%` }"></div></div><span class="text-xs text-muted-foreground">{{ item.rollup.completedCount }}/{{ item.rollup.totalCount }}</span></div>
            <span class="text-sm text-muted-foreground">{{ item.dueDate ?? '—' }}</span>
            <div class="relative h-5 rounded bg-muted"><div class="absolute top-0 h-full rounded bg-primary/30" :style="timeline(item)"><div class="h-full rounded bg-primary" :style="{ width: `${Math.min(100, percent(item))}%` }"></div></div></div>
          </div>
        </div>
      </div>
    </section>
  </AppShell>
</template>
