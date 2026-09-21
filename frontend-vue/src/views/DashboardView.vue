<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { GripVertical, Plus, Settings2, Trash2 } from '@lucide/vue'
import { GridItem, GridLayout } from 'grid-layout-plus'
import AppShell from '@/components/shell/AppShell.vue'
import DashboardWidgetRenderer from '@/components/dashboard/DashboardWidgetRenderer.vue'
import { createDashboard, deleteDashboard, getDashboardData, listDashboards, updateDashboard, type DashboardWidget } from '@/api/analytics'
import { useToast } from '@/composables/useToast'

const props = defineProps<{ slug: string; projectKey: string }>(); const client = useQueryClient(); const toast = useToast(); const edit = ref(false); const selected = ref('')
const dashboards = useQuery({ queryKey: computed(() => ['dashboards', props.slug, props.projectKey]), queryFn: () => listDashboards(props.slug, props.projectKey) })
const dashboardList = computed(() => dashboards.data.value ?? [])
const dashboard = computed(() => dashboards.data.value?.find(row => row.id === selected.value) ?? dashboards.data.value?.find(row => row.isDefault) ?? dashboards.data.value?.[0])
const data = useQuery({ queryKey: computed(() => ['dashboard-data', props.slug, props.projectKey, dashboard.value?.id]), enabled: computed(() => Boolean(dashboard.value)), queryFn: () => getDashboardData(props.slug, props.projectKey, dashboard.value!.id) })
const widgetData = computed(() => data.data.value?.widgets ?? [])
// vue-query hands back a readonly proxy of the server's state, so the grid's own
// mutations — drag, resize, add, remove — silently no-op against it and warn once per
// key. The editor therefore works on a plain-object draft, re-seeded from the server
// whenever the selected dashboard changes (but never while someone is mid-edit, which
// would discard their unsaved layout on any refetch); Save is what sends it back.
const draft = ref<DashboardWidget[]>([])
watch(
  dashboard,
  (value, previous) => {
    if (!value) { draft.value = []; return }
    if (value.id !== previous?.id || !edit.value) {
      draft.value = value.layout.map(widget => ({ ...widget, config: { ...widget.config } }))
    }
  },
  { immediate: true },
)
const saving = computed(() => save.isPending.value)
const catalog = ['velocity', 'burndown', 'cfd', 'cycle-time', 'sprint-health', 'items-by-state', 'items-by-assignee', 'recent-activity', 'saved-view-count', 'markdown']
const save = useMutation({ mutationFn: () => updateDashboard(props.slug, props.projectKey, dashboard.value!.id, { layout: draft.value, version: dashboard.value!.version }), onSuccess: async () => { await client.invalidateQueries({ queryKey: ['dashboards', props.slug, props.projectKey] }); toast.success('Dashboard saved.') }, onError: error => toast.error(error, 'Dashboard could not be saved.') })
async function add() { const layout: DashboardWidget[] = [{ id: crypto.randomUUID(), type: 'velocity', x: 0, y: 0, w: 6, h: 3, config: {} }]; const result = await createDashboard(props.slug, props.projectKey, { name: 'My dashboard', layout }); selected.value = result.id; await client.invalidateQueries({ queryKey: ['dashboards', props.slug, props.projectKey] }); edit.value = true }
function addWidget(type: string) { if (!dashboard.value || draft.value.length >= 25) return; draft.value.push({ id: crypto.randomUUID(), type, x: (draft.value.length % 2) * 6, y: Math.floor(draft.value.length / 2) * 3, w: 6, h: 3, config: type === 'markdown' ? { markdown: 'Add a note' } : {} }) }
function remove(id: string) { draft.value = draft.value.filter(widget => widget.id !== id) }
function positioned(layout: { i: string; x: number; y: number; w: number; h: number }[]) { for (const position of layout) { const widget = draft.value.find(item => item.id === position.i); if (widget) Object.assign(widget, { x: position.x, y: position.y, w: position.w, h: position.h }) } }
async function destroy() { if (!dashboard.value || dashboard.value.isDefault) return; await deleteDashboard(props.slug, props.projectKey, dashboard.value.id); selected.value = ''; await client.invalidateQueries({ queryKey: ['dashboards', props.slug, props.projectKey] }) }
</script>
<template>
  <AppShell>
    <main class="w-full p-5 sm:p-8">
      <header class="flex flex-wrap items-start justify-between gap-3">
        <div><p class="font-label">Project overview</p><h1 class="mt-1 text-xl font-semibold tracking-tight">Dashboard</h1><p class="text-muted-foreground mt-1 text-sm">A calm, live view of delivery health.</p></div>
        <div class="flex flex-wrap gap-2"><select v-if="dashboardList.length" v-model="selected" class="border-input bg-card rounded-md border px-3 text-sm"><option v-for="entry in dashboardList" :key="entry.id" :value="entry.id">{{ entry.name }}{{ entry.isDefault ? ' (default)' : '' }}</option></select><button class="border-input bg-card rounded-md border px-3 py-2 text-sm transition-colors hover:bg-muted" @click="edit = !edit"><Settings2 class="mr-1.5 inline size-4" />{{ edit ? 'Done' : 'Customize' }}</button><button class="bg-primary text-primary-foreground rounded-md px-3 py-2 text-sm font-medium transition-opacity hover:opacity-90" @click="add"><Plus class="mr-1.5 inline size-4" />Dashboard</button></div>
      </header>
      <div v-if="edit" class="border-border bg-card mt-5 rounded-lg border p-3 shadow-sm"><div class="flex flex-wrap items-center gap-2"><span class="font-label mr-1">Add widget</span><button v-for="type in catalog" :key="type" class="rounded-md bg-muted px-2.5 py-1.5 text-xs capitalize transition-colors hover:bg-accent" @click="addWidget(type)">{{ type.replaceAll('-', ' ') }}</button><span class="flex-1" /><button class="bg-primary text-primary-foreground rounded-md px-3 py-1.5 text-sm disabled:opacity-50" :disabled="saving" @click="save.mutate()">{{ saving ? 'Saving…' : 'Save layout' }}</button><button v-if="!dashboard?.isDefault" class="text-destructive rounded-md px-2 py-1.5 text-sm hover:bg-destructive/10" @click="destroy">Delete</button></div><p class="text-muted-foreground mt-2 text-xs">Drag cards to rearrange them and use their lower-right corner to resize.</p></div>
      <p v-if="dashboards.isError.value" class="text-destructive py-12">Dashboards could not be loaded.</p>
      <GridLayout v-else-if="dashboard" class="dashboard-grid mt-5" :layout="draft.map(widget => ({ ...widget, i: widget.id }))" :col-num="12" :row-height="72" :is-draggable="edit" :is-resizable="edit" :vertical-compact="true" @layout-updated="positioned">
        <GridItem v-for="widget in draft" :key="widget.id" :i="widget.id" :x="widget.x" :y="widget.y" :w="widget.w" :h="widget.h">
          <article class="border-border bg-card h-full overflow-hidden rounded-lg border p-4 shadow-sm transition-shadow hover:shadow-md">
            <div v-if="edit" class="dashboard-drag-handle"><GripVertical class="size-3.5" /><span>Drag</span><button class="text-muted-foreground ml-auto rounded hover:text-destructive" :aria-label="`Remove ${widget.type}`" @click="remove(widget.id)"><Trash2 class="size-4" /></button></div>
            <p v-if="widgetData.find(row => row.id === widget.id)?.error" class="text-destructive grid h-full place-items-center text-sm">Widget data is unavailable.</p>
            <DashboardWidgetRenderer v-else :type="widget.type" :data="widgetData.find(row => row.id === widget.id)?.data" :config="widget.config" />
          </article>
        </GridItem>
      </GridLayout>
      <p v-else class="text-muted-foreground py-12 text-center">Loading dashboard…</p>
    </main>
  </AppShell>
</template>

<style scoped>
.dashboard-grid :deep(.vgl-item--placeholder) { background: color-mix(in oklab, var(--primary) 18%, transparent); border: 1px dashed var(--primary); border-radius: .5rem; }
.dashboard-drag-handle { align-items: center; color: var(--muted-foreground); cursor: move; display: flex; font-family: 'JetBrains Mono Variable', monospace; font-size: .58rem; gap: .2rem; letter-spacing: .05em; margin: -.15rem 0 .4rem; text-transform: uppercase; }
</style>
