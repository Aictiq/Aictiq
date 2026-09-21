<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import AppShell from '@/components/shell/AppShell.vue'
import ChartCard from '@/components/analytics/ChartCard.vue'
import { getCycleTime, getFlow } from '@/api/analytics'
import { cfdOption, cycleOption, throughputOption } from '@/lib/analytics'

const props = defineProps<{ slug: string; projectKey: string }>()
const end = new Date().toISOString().slice(0, 10)
const startDate = new Date(); startDate.setDate(startDate.getDate() - 29)
const from = ref(startDate.toISOString().slice(0, 10)); const to = ref(end); const types = ref('')
const query = computed(() => ({ from: from.value, to: to.value, ...(types.value ? { types: types.value } : {}) }))
const flow = useQuery({ queryKey: computed(() => ['flow', props.slug, props.projectKey, query.value]), queryFn: () => getFlow(props.slug, props.projectKey, query.value) })
const cycle = useQuery({ queryKey: computed(() => ['cycle', props.slug, props.projectKey, query.value]), queryFn: () => getCycleTime(props.slug, props.projectKey, query.value) })
const flowData = computed(() => flow.data.value?.days ?? [])
const cycleData = computed(() => cycle.data.value)
const flowLoading = computed(() => flow.isLoading.value)
const cycleLoading = computed(() => cycle.isLoading.value)
</script>
<template><AppShell><main class="w-full p-5 sm:p-8"><header><h1 class="text-xl font-semibold">Cycle insights</h1><p class="text-muted-foreground mt-1 text-sm">Flow, throughput, and delivery time for this project.</p></header><form class="mt-5 flex flex-wrap gap-2" @submit.prevent="flow.refetch(); cycle.refetch()"><label class="text-sm">From<input v-model="from" type="date" class="border-input ml-2 rounded border p-2" /></label><label class="text-sm">To<input v-model="to" type="date" class="border-input ml-2 rounded border p-2" /></label><label class="text-sm">Types<select v-model="types" class="border-input ml-2 rounded border p-2"><option value="">All</option><option value="story,bug">Stories & bugs</option><option value="task">Tasks</option></select></label><button class="bg-primary text-primary-foreground rounded px-3 text-sm">Apply</button></form><p v-if="flow.isError.value || cycle.isError.value" class="text-destructive mt-5">Analytics could not be loaded for this range.</p><div class="mt-5 grid gap-5 lg:grid-cols-2"><ChartCard title="Cumulative flow" :option="cfdOption(flowData)" :loading="flowLoading" :empty="!flowData.length" :csv-rows="[['Date', 'Counts'], ...flowData.map(day => [day.day, JSON.stringify(day.counts)])]" /><ChartCard title="Cycle-time scatter" :option="cycleOption(cycleData?.items ?? [])" :loading="cycleLoading" :empty="!(cycleData?.items.length)" :csv-rows="[['Item', 'Lead days', 'Cycle days', 'Type'], ...(cycleData?.items ?? []).map(item => [item.itemKey, item.leadDays, item.cycleDays, item.type])]" /><ChartCard title="Throughput" :option="throughputOption(cycleData?.throughput ?? [])" :loading="cycleLoading" :empty="!(cycleData?.throughput.length)" :csv-rows="[['Week', 'Total', 'Human', 'Agent'], ...(cycleData?.throughput ?? []).map(point => [point.weekOf, point.count, point.humanCount, point.agentCount])]" /><section class="border-border rounded-lg border p-4"><h2 class="font-medium">Percentiles</h2><div class="mt-5 grid grid-cols-3 gap-3 text-center text-sm"><div><strong class="block text-lg">{{ cycleData?.cycleTime.p50 ?? 0 }}d</strong><span class="text-muted-foreground">P50 cycle</span></div><div><strong class="block text-lg">{{ cycleData?.cycleTime.p85 ?? 0 }}d</strong><span class="text-muted-foreground">P85 cycle</span></div><div><strong class="block text-lg">{{ cycleData?.leadTime.p50 ?? 0 }}d</strong><span class="text-muted-foreground">P50 lead</span></div></div></section></div></main></AppShell></template>
