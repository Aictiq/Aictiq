<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { CheckCircle2, Plus, Save } from '@lucide/vue'

import { boardMove, getBoard } from '@/api/boards'
import { createItem, editItem, getItem, type WorkItem } from '@/api/items'
import { listWorkflows } from '@/api/workflows'
import {
  completeSprint,
  getSprintCapacity,
  getSprintTaskboard,
  listSprints,
  updateSprintCapacity,
  type Sprint,
} from '@/api/sprints'
import AppShell from '@/components/shell/AppShell.vue'
import { useItemModal } from '@/composables/useItemModal'
import { useToast } from '@/composables/useToast'
import { moveTaskboardTask, taskboardColumns } from '@/lib/taskboard'
import ChartCard from '@/components/analytics/ChartCard.vue'
import { getBurndown, getSprintHealth } from '@/api/analytics'
import { burndownOption } from '@/lib/analytics'

const props = defineProps<{ slug: string; projectKey: string; teamId: string; sprintId: string }>()
const client = useQueryClient()
const toast = useToast()
const itemModal = useItemModal()
const tab = ref<'taskboard' | 'capacity' | 'insights'>('taskboard')
const burndownUnit = ref<'points' | 'hours'>('points')
const dragged = ref<WorkItem | null>(null)
const creatingFor = ref<string | null>(null)
const newTaskTitle = ref('')
const creating = ref(false)
const completing = ref(false)
const carryTo = ref('backlog')

const sprintQuery = useQuery({
  queryKey: computed(() => [props.slug, props.teamId, 'sprint', 'sprints']),
  queryFn: () => listSprints(props.slug, props.teamId),
})
const sprint = computed<Sprint | null>(
  () => sprintQuery.data.value?.find((entry) => entry.id === props.sprintId) ?? null,
)
const workflow = useQuery({
  queryKey: computed(() => ['workflow', props.slug, props.projectKey]),
  queryFn: async () =>
    (await listWorkflows(props.slug, props.projectKey)).find((entry) => entry.isDefault),
})
// Creating the team board here also ensures a task drag targets one of its valid states.
const boardQuery = useQuery({
  queryKey: computed(() => ['board', props.slug, props.teamId]),
  enabled: computed(() => Boolean(props.slug && props.teamId)),
  queryFn: () => getBoard(props.slug, props.teamId, { take: 0 }),
})
const taskboardKey = computed(() => ['taskboard', props.slug, props.sprintId])
const taskboard = useQuery({
  queryKey: taskboardKey,
  queryFn: () => getSprintTaskboard(props.slug, props.sprintId),
})
const capacityKey = computed(() => ['capacity', props.slug, props.sprintId])
const capacity = useQuery({
  queryKey: capacityKey,
  queryFn: () => getSprintCapacity(props.slug, props.sprintId),
})
const columns = computed(() => taskboardColumns(taskboard.data.value))
const taskboardRows = computed(() => taskboard.data.value?.rows ?? [])
const capacityData = computed(() => capacity.data.value)
const burndown = useQuery({ queryKey: computed(() => ['burndown', props.slug, props.sprintId, burndownUnit.value]), queryFn: () => getBurndown(props.slug, props.sprintId, burndownUnit.value) })
const health = useQuery({ queryKey: computed(() => ['sprint-health', props.slug, props.sprintId]), queryFn: () => getSprintHealth(props.slug, props.sprintId) })
const burndownData = computed(() => burndown.data.value?.days ?? [])
const healthData = computed(() => health.data.value)
const burndownLoading = computed(() => burndown.isLoading.value)
const openSprints = computed(() =>
  (sprintQuery.data.value ?? []).filter(
    (entry) => entry.id !== props.sprintId && entry.state !== 'completed',
  ),
)

function percent(done: number, total: number) {
  return total ? Math.round((done / total) * 100) : 0
}
async function invalidate() {
  await Promise.all([
    client.invalidateQueries({ queryKey: taskboardKey.value }),
    client.invalidateQueries({ queryKey: capacityKey.value }),
    client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'sprint'] }),
  ])
}
function targetState(cell: { category: string | null }) {
  return workflow.data.value?.states.find((state) => state.category === cell.category)?.id ?? null
}
async function move(task: WorkItem, cell: { key: string; category: string | null }) {
  const stateId = targetState(cell)
  if (!stateId || task.stateId === stateId || !taskboard.data.value) return
  const snapshot = taskboard.data.value
  client.setQueryData(
    taskboardKey.value,
    moveTaskboardTask(snapshot, task, { cellKey: cell.key, stateId }),
  )
  try {
    if (!boardQuery.data.value) await boardQuery.refetch()
    await boardMove(props.slug, task.key, { toStateId: stateId, version: task.version })
    await invalidate()
  } catch (error) {
    client.setQueryData(taskboardKey.value, snapshot)
    toast.error(error, 'The task could not be moved.')
  }
}
async function saveHours(task: WorkItem, event: Event) {
  const hours = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(hours) || hours < 0 || hours === task.remainingHours) return
  try {
    await editItem(props.slug, task, { remainingHours: hours })
    await invalidate()
  } catch (error) {
    toast.error(error, 'Remaining hours could not be updated.')
  }
}
async function createTask(parentKey: string) {
  if (!newTaskTitle.value.trim()) return
  creating.value = true
  try {
    const parent = await getItem(props.slug, parentKey)
    const task = await createItem(props.slug, props.projectKey, {
      type: 'task',
      title: newTaskTitle.value.trim(),
      parentId: parent.id,
      teamId: props.teamId,
    })
    await import('@/api/items').then(({ moveItem }) =>
      moveItem(props.slug, task.key, { sprintId: props.sprintId, version: task.version }),
    )
    creatingFor.value = null
    newTaskTitle.value = ''
    await invalidate()
  } catch (error) {
    toast.error(error, 'The task could not be created.')
  } finally {
    creating.value = false
  }
}
async function saveCapacity() {
  const data = capacity.data.value
  if (!data) return
  try {
    await updateSprintCapacity(
      props.slug,
      props.sprintId,
      data.members.map((member) => ({
        userId: member.userId,
        hoursPerDay: Number(member.hoursPerDay),
        daysOff: Number(member.daysOff),
      })),
    )
    await capacity.refetch()
    toast.success('Capacity saved.')
  } catch (error) {
    toast.error(error, 'Capacity could not be saved.')
  }
}
async function complete() {
  try {
    await completeSprint(
      props.slug,
      props.sprintId,
      carryTo.value === 'backlog'
        ? { moveUnfinishedToBacklog: true }
        : { moveUnfinishedTo: carryTo.value },
    )
    completing.value = false
    await invalidate()
    toast.success('Sprint completed.')
  } catch (error) {
    toast.error(error, 'The sprint could not be completed.')
  }
}
</script>

<template>
  <AppShell>
    <main class="w-full p-5 sm:p-8">
      <template v-if="sprint"
        ><header class="flex flex-wrap items-start justify-between gap-4">
          <div>
            <RouterLink
              class="text-muted-foreground text-sm hover:underline"
              :to="`/o/${slug}/p/${projectKey}/teams/${teamId}/sprints`"
              >Sprints</RouterLink
            >
            <h1 class="mt-1 text-xl font-semibold">{{ sprint.name }}</h1>
            <p class="text-muted-foreground mt-1 text-sm">
              {{ sprint.goal || 'No sprint goal set.' }}
            </p>
          </div>
          <div class="text-right text-sm">
            <strong>{{ sprint.daysLeft }} days left</strong>
            <p class="text-muted-foreground">
              {{ sprint.progress.remainingHours }}h remaining · {{ sprint.progress.pointsDone }}/{{
                sprint.progress.pointsTotal
              }}
              points
            </p>
            <button
              v-if="sprint.state === 'active'"
              class="border-input mt-2 inline-flex items-center gap-1 rounded border px-2.5 py-1.5"
              @click="completing = true"
            >
              <CheckCircle2 class="size-4" /> Complete sprint
            </button>
          </div>
        </header>
        <div class="bg-muted mt-5 h-2 overflow-hidden rounded">
          <div
            class="bg-primary h-full"
            :style="{
              width: `${percent(sprint.progress.completedItems, sprint.progress.totalItems)}%`,
            }"
          />
        </div>
        <p class="text-muted-foreground mt-1 text-right text-xs">
          {{ sprint.progress.completedItems }}/{{ sprint.progress.totalItems }} items complete
        </p>
        <nav class="border-border mt-6 flex gap-4 border-b" aria-label="Sprint detail tabs">
          <button
            v-for="entry in [
              ['taskboard', 'Taskboard'],
              ['capacity', 'Capacity'],
              ['insights', 'Insights'],
            ] as const"
            :key="entry[0]"
            class="border-b-2 px-1 py-2 text-sm"
            :class="
              tab === entry[0]
                ? 'border-primary text-foreground'
                : 'border-transparent text-muted-foreground'
            "
            @click="tab = entry[0]"
          >
            {{ entry[1] }}
          </button>
        </nav>
        <section v-if="tab === 'taskboard'" class="mt-5 overflow-x-auto">
          <p v-if="taskboard.isError.value" class="text-destructive">
            The taskboard could not be loaded.
          </p>
          <table v-else class="border-border w-full min-w-[52rem] border-collapse border text-sm">
            <thead>
              <tr class="bg-muted/50">
                <th class="border-border w-64 border p-3 text-left font-medium">Story</th>
                <th
                  v-for="column in columns"
                  :key="column.key"
                  class="border-border min-w-52 border p-3 text-left font-medium"
                >
                  {{ column.name }}
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in taskboardRows" :key="row.parentKey ?? row.name">
                <th class="border-border align-top p-3 text-left font-medium">
                  <button
                    v-if="row.parentKey"
                    type="button"
                    class="text-left hover:underline"
                    @click="itemModal.open(row.parentKey)"
                  >
                    {{ row.name }}</button
                  ><span v-else>{{ row.name }}</span
                  ><span class="text-muted-foreground ml-1 text-xs">{{ row.remainingHours }}h</span
                  ><button
                    v-if="row.parentKey"
                    class="text-primary mt-3 flex items-center gap-1 text-xs"
                    @click="creatingFor = row.parentKey"
                  >
                    <Plus class="size-3" /> Add task
                  </button>
                  <form
                    v-if="creatingFor === row.parentKey"
                    class="mt-2 flex gap-1"
                    @submit.prevent="row.parentKey && createTask(row.parentKey)"
                  >
                    <input
                      v-model="newTaskTitle"
                      class="border-input min-w-0 rounded border px-2 py-1 text-xs"
                      placeholder="Task title"
                      autofocus
                    /><button
                      class="bg-primary text-primary-foreground rounded px-2 text-xs"
                      :disabled="creating"
                    >
                      Add
                    </button>
                  </form>
                </th>
                <td
                  v-for="cell in row.cells"
                  :key="cell.key"
                  class="border-border min-h-32 align-top border p-2"
                  @dragover.prevent
                  @drop.prevent="dragged && move(dragged, cell)"
                >
                  <div
                    v-for="task in cell.tasks"
                    :key="task.id"
                    draggable="true"
                    class="bg-background border-border mb-2 rounded border p-2 shadow-sm"
                    @dragstart="dragged = task"
                    @dragend="dragged = null"
                  >
                    <button
                      type="button"
                      class="block w-full text-left"
                      @click="itemModal.open(task.key)"
                    >
                      <span class="block font-medium hover:underline">{{ task.title }}</span>
                      <span class="text-muted-foreground mt-1 block text-xs">{{ task.key }}</span>
                    </button>
                    <label class="text-muted-foreground mt-2 flex items-center gap-1 text-xs"
                      >Remaining
                      <input
                        :value="task.remainingHours ?? 0"
                        type="number"
                        min="0"
                        step="0.25"
                        class="border-input w-16 rounded border px-1 py-0.5"
                        @change="saveHours(task, $event)"
                      />
                      h</label
                    >
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
          <p
            v-if="!taskboard.isPending.value && !taskboardRows.length"
            class="text-muted-foreground py-12 text-center"
          >
            No stories or tasks are in this sprint yet.
          </p>
        </section>
        <section v-else-if="tab === 'capacity'" class="mt-5">
          <div class="mb-4 flex items-end justify-between">
            <div>
              <h2 class="font-medium">Team capacity</h2>
              <p class="text-muted-foreground text-sm">
                {{ capacityData?.workingDays ?? 0 }} working days ·
                {{ capacityData?.assignedRemainingHours ?? 0 }}h assigned of
                {{ capacityData?.capacityHours ?? 0 }}h
              </p>
            </div>
            <button
              class="bg-primary text-primary-foreground inline-flex items-center gap-2 rounded px-3 py-2 text-sm"
              @click="saveCapacity"
            >
              <Save class="size-4" /> Save capacity
            </button>
          </div>
          <div class="border-border overflow-hidden rounded-lg border">
            <div
              v-for="member in capacityData?.members"
              :key="member.userId"
              class="border-border grid gap-3 border-b p-4 last:border-b-0 md:grid-cols-[minmax(10rem,1fr)_7rem_7rem_minmax(12rem,1fr)] md:items-center"
            >
              <div>
                <strong>{{ member.displayName }}</strong
                ><span v-if="member.isAgent" class="text-muted-foreground ml-1 text-xs">Agent</span>
              </div>
              <label class="text-sm"
                >Hours/day<input
                  v-model.number="member.hoursPerDay"
                  type="number"
                  min="0"
                  max="24"
                  step="0.25"
                  class="border-input mt-1 w-full rounded border px-2 py-1" /></label
              ><label class="text-sm"
                >Days off<input
                  v-model.number="member.daysOff"
                  type="number"
                  min="0"
                  :max="member.workingDays"
                  class="border-input mt-1 w-full rounded border px-2 py-1"
              /></label>
              <div>
                <div class="text-muted-foreground flex justify-between text-xs">
                  <span>{{ member.assignedRemainingHours }}h assigned</span
                  ><span>{{ member.capacityHours }}h capacity</span>
                </div>
                <div class="bg-muted mt-1 h-2 overflow-hidden rounded">
                  <div
                    class="h-full"
                    :class="member.utilizationPercent > 100 ? 'bg-destructive' : 'bg-primary'"
                    :style="{ width: `${Math.min(member.utilizationPercent, 100)}%` }"
                  />
                </div>
              </div>
            </div>
          </div>
        </section>
        <section v-else class="mt-5 grid gap-5 lg:grid-cols-[minmax(0,1fr)_18rem]">
          <div><div class="mb-3 flex justify-end gap-1"><button v-for="unit in ['points', 'hours'] as const" :key="unit" class="rounded px-3 py-1 text-sm capitalize" :class="burndownUnit === unit ? 'bg-primary text-primary-foreground' : 'bg-muted'" @click="burndownUnit = unit">{{ unit }}</button></div><ChartCard title="Burndown" :option="burndownOption(burndownData)" :loading="burndownLoading" :empty="!burndownData.length" :csv-rows="[['Date', 'Scope', 'Remaining', 'Completed', 'Ideal'], ...burndownData.map(day => [day.day, day.scope, day.remaining, day.completed, day.idealRemaining])]" /></div>
          <aside class="border-border rounded-lg border p-4"><h2 class="font-medium">Sprint health</h2><dl class="mt-4 space-y-3 text-sm"><div class="flex justify-between"><dt>Done</dt><dd>{{ healthData?.percentDone ?? 0 }}%</dd></div><div class="flex justify-between"><dt>Blocked</dt><dd>{{ healthData?.blockedCount ?? 0 }}</dd></div><div class="flex justify-between"><dt>Unestimated</dt><dd>{{ healthData?.unestimatedCount ?? 0 }}</dd></div><div class="flex justify-between"><dt>Unassigned</dt><dd>{{ healthData?.unassignedCount ?? 0 }}</dd></div><div class="flex justify-between"><dt>Agent share</dt><dd>{{ healthData?.agentSharePercent ?? 0 }}%</dd></div><div v-if="healthData?.projectedCompletionOn" class="border-border border-t pt-3"><dt class="text-muted-foreground">Projected completion</dt><dd>{{ healthData.projectedCompletionOn }}</dd></div></dl></aside>
        </section></template
      >
      <p v-else-if="sprintQuery.isError.value" class="text-destructive">
        This sprint could not be loaded.
      </p>
      <p v-else class="text-muted-foreground py-16 text-center">Loading sprint…</p>
    </main>
    <div v-if="completing" class="bg-background/80 fixed inset-0 z-50 grid place-items-center p-4">
      <form
        class="bg-background border-border w-full max-w-md rounded-lg border p-5 shadow-xl"
        @submit.prevent="complete"
      >
        <h2 class="font-semibold">Complete sprint</h2>
        <p class="text-muted-foreground mt-1 text-sm">Choose where unfinished work should go.</p>
        <label class="mt-4 block text-sm font-medium"
          >Carry unfinished work to<select
            v-model="carryTo"
            class="border-input mt-1 w-full rounded border px-3 py-2"
          >
            <option value="backlog">Backlog</option>
            <option v-for="target in openSprints" :key="target.id" :value="target.id">
              {{ target.name }}
            </option>
          </select></label
        >
        <div class="mt-5 flex justify-end gap-2">
          <button
            type="button"
            class="border-input rounded border px-3 py-2 text-sm"
            @click="completing = false"
          >
            Cancel</button
          ><button class="bg-primary text-primary-foreground rounded px-3 py-2 text-sm">
            Complete sprint
          </button>
        </div>
      </form>
    </div>
  </AppShell>
</template>
