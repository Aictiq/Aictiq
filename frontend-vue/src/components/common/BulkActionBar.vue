<script setup lang="ts" generic="TItem">
import { computed, ref, shallowRef } from 'vue'

import type { BulkItemSet, BulkItemResult, WorkItemPriority } from '@/api/templates'
import { Button } from '@/components/ui/button'

const props = defineProps<{
  selected: Array<{ key: string; version: number }>
  apply: (set: BulkItemSet) => Promise<BulkItemResult<TItem>[]>
}>()

const emit = defineEmits<{ applied: [results: BulkItemResult<TItem>[]]; clear: [] }>()
const busy = ref(false)
const priority = ref<WorkItemPriority | ''>('')
const stateId = ref('')
const assigneeId = ref('')
const teamId = ref('')
const sprintId = ref('')
const feedback = shallowRef<BulkItemResult<TItem>[]>([])

const hasAction = computed(() => Boolean(priority.value || stateId.value || assigneeId.value || teamId.value || sprintId.value))

async function submit() {
  if (!hasAction.value || busy.value) return
  busy.value = true
  try {
    const set: BulkItemSet = {}
    if (priority.value) set.priority = priority.value
    if (stateId.value) set.stateId = stateId.value
    if (assigneeId.value) set.assigneeId = assigneeId.value
    if (teamId.value) set.teamId = teamId.value
    if (sprintId.value) set.sprintId = sprintId.value
    feedback.value = await props.apply(set)
    emit('applied', feedback.value)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="border-border bg-muted/40 sticky bottom-3 z-10 rounded-lg border p-3" aria-label="Bulk actions">
    <div class="flex flex-wrap items-center gap-2">
      <strong class="mr-1 text-sm">{{ selected.length }} selected</strong>
      <input v-model="stateId" class="border-input bg-background h-8 w-36 rounded border px-2 text-xs" placeholder="State ID" aria-label="State ID" />
      <input v-model="assigneeId" class="border-input bg-background h-8 w-36 rounded border px-2 text-xs" placeholder="Assignee ID" aria-label="Assignee ID" />
      <select v-model="priority" class="border-input bg-background h-8 rounded border px-2 text-xs" aria-label="Priority">
        <option value="">Priority</option><option value="none">None</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="urgent">Urgent</option>
      </select>
      <input v-model="teamId" class="border-input bg-background h-8 w-32 rounded border px-2 text-xs" placeholder="Team ID" aria-label="Team ID" />
      <input v-model="sprintId" class="border-input bg-background h-8 w-32 rounded border px-2 text-xs" placeholder="Sprint ID" aria-label="Sprint ID" />
      <Button size="sm" :disabled="!hasAction || busy" @click="submit">{{ busy ? 'Applying…' : 'Apply' }}</Button>
      <Button size="sm" variant="ghost" :disabled="busy" @click="emit('clear')">Clear</Button>
    </div>
    <p v-if="feedback.some((result) => result.status !== 200)" class="text-destructive mt-2 text-xs">
      {{ feedback.filter((result) => result.status !== 200).map((result) => `${result.key}: ${result.detail}`).join(' · ') }}
    </p>
  </section>
</template>
