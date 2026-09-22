<script setup lang="ts">
import { computed, ref } from 'vue'
import VChart from 'vue-echarts'
import type { EChartsOption } from 'echarts'
import { Download, ImageDown } from '@lucide/vue'

const props = defineProps<{ title: string; option: EChartsOption; loading?: boolean; empty?: boolean; csvRows?: (string | number | null | undefined)[][] }>()
const chart = ref<InstanceType<typeof VChart> | null>(null)
const noData = computed(() => props.empty && !props.loading)
function png() { const instance = chart.value?.chart; if (!instance) return; const link = document.createElement('a'); link.href = instance.getDataURL({ pixelRatio: 2, backgroundColor: '#fff' }); link.download = `${props.title.toLowerCase().replaceAll(' ', '-')}.png`; link.click() }
function csv() { if (!props.csvRows) return; const escape = (v: string | number | null | undefined) => `"${String(v ?? '').replaceAll('"', '""')}"`; const link = document.createElement('a'); link.href = URL.createObjectURL(new Blob([props.csvRows.map(row => row.map(escape).join(',')).join('\n')], { type: 'text/csv;charset=utf-8' })); link.download = `${props.title.toLowerCase().replaceAll(' ', '-')}.csv`; link.click(); URL.revokeObjectURL(link.href) }
</script>
<template><section class="border-border rounded-lg border bg-card p-4"><div class="mb-2 flex items-center justify-between gap-2"><h2 class="font-medium">{{ title }}</h2><div class="flex gap-1"><button v-if="csvRows" class="rounded p-1.5 hover:bg-muted" :aria-label="`Export ${title} CSV`" @click="csv"><Download class="size-4" /></button><button class="rounded p-1.5 hover:bg-muted" :aria-label="`Export ${title} PNG`" @click="png"><ImageDown class="size-4" /></button></div></div><div v-if="loading" class="text-muted-foreground grid h-64 place-items-center text-sm">Loading chart…</div><div v-else-if="noData" class="text-muted-foreground grid h-64 place-items-center text-sm">No data for this selection.</div><VChart v-else ref="chart" class="h-64 w-full" :option="option" autoresize /></section></template>
