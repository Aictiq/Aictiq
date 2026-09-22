<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { CalendarPlus, Pencil, Play } from '@lucide/vue'

import { createSprint, listSprints, startSprint, updateSprint, type Sprint } from '@/api/sprints'
import { getTeam, type Team } from '@/api/teams'
import AppShell from '@/components/shell/AppShell.vue'
import { useToast } from '@/composables/useToast'

const props = defineProps<{ slug: string; projectKey: string; teamId: string }>()
const client = useQueryClient()
const toast = useToast()
const dialogOpen = ref(false)
const saving = ref(false)
const editing = ref<Sprint | null>(null)
const form = ref({ name: '', goal: '', startsOn: '', endsOn: '', autoCreateNext: true })

const team = useQuery<Team>({
  queryKey: computed(() => ['team', props.slug, props.projectKey, props.teamId]),
  queryFn: () => getTeam(props.slug, props.projectKey, props.teamId),
})
const sprints = useQuery({
  queryKey: computed(() => [props.slug, props.teamId, 'sprint', 'sprints']),
  queryFn: () => listSprints(props.slug, props.teamId),
})
const sprintList = computed(() => sprints.data.value ?? [])
const teamName = computed(() => team.data.value?.name ?? 'Team')

function asDate(date: Date) {
  return date.toISOString().slice(0, 10)
}
function newForm() {
  const starts = new Date()
  const ends = new Date(starts)
  ends.setDate(ends.getDate() + (team.data.value?.sprintLengthDays ?? 14))
  form.value = {
    name: '',
    goal: '',
    startsOn: asDate(starts),
    endsOn: asDate(ends),
    autoCreateNext: true,
  }
  editing.value = null
  dialogOpen.value = true
}
function edit(sprint: Sprint) {
  editing.value = sprint
  form.value = {
    name: sprint.name,
    goal: sprint.goal,
    startsOn: sprint.startsOn,
    endsOn: sprint.endsOn,
    autoCreateNext: sprint.autoCreateNext,
  }
  dialogOpen.value = true
}
async function save() {
  if (!form.value.name.trim()) return
  saving.value = true
  try {
    if (editing.value)
      await updateSprint(props.slug, editing.value.id, {
        ...form.value,
        version: editing.value.version,
      })
    else await createSprint(props.slug, props.teamId, form.value)
    dialogOpen.value = false
    await client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'sprint'] })
    toast.success(editing.value ? 'Sprint updated.' : 'Sprint created.')
  } catch (error) {
    toast.error(error, 'The sprint could not be saved.')
  } finally {
    saving.value = false
  }
}
async function start(sprint: Sprint) {
  try {
    await startSprint(props.slug, sprint.id)
    await client.invalidateQueries({ queryKey: [props.slug, props.teamId, 'sprint'] })
    toast.success(`${sprint.name} started.`)
  } catch (error) {
    toast.error(error, 'The sprint could not be started.')
  }
}
function progress(sprint: Sprint) {
  return sprint.progress.totalItems
    ? Math.round((sprint.progress.completedItems / sprint.progress.totalItems) * 100)
    : 0
}
</script>

<template>
  <AppShell>
    <main class="w-full p-5 sm:p-8">
      <header class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-xl font-semibold">Sprints</h1>
          <p class="text-muted-foreground text-sm">
            {{ teamName }}’s planned and active iterations.
          </p>
        </div>
        <button
          class="bg-primary text-primary-foreground inline-flex items-center gap-2 rounded-md px-3 py-2 text-sm"
          @click="newForm"
        >
          <CalendarPlus class="size-4" /> New sprint
        </button>
      </header>
      <p v-if="sprints.isError.value" class="text-destructive mt-6">Sprints could not be loaded.</p>
      <div v-else class="mt-6 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <article
          v-for="sprint in sprintList"
          :key="sprint.id"
          class="border-border rounded-lg border p-4"
        >
          <div class="flex items-start gap-3">
            <RouterLink
              class="min-w-0 flex-1"
              :to="`/o/${slug}/p/${projectKey}/teams/${teamId}/sprints/${sprint.id}`"
              ><h2 class="font-medium hover:underline">{{ sprint.name }}</h2>
              <p class="text-muted-foreground mt-1 line-clamp-2 min-h-10 text-sm">
                {{ sprint.goal || 'No sprint goal set.' }}
              </p></RouterLink
            ><span class="bg-muted rounded px-2 py-0.5 text-xs capitalize">{{ sprint.state }}</span>
          </div>
          <div class="mt-4">
            <div class="text-muted-foreground flex justify-between text-xs">
              <span>{{ sprint.progress.completedItems }}/{{ sprint.progress.totalItems }} done</span
              ><span>{{ progress(sprint) }}%</span>
            </div>
            <div class="bg-muted mt-1.5 h-2 overflow-hidden rounded">
              <div class="bg-primary h-full" :style="{ width: `${progress(sprint)}%` }" />
            </div>
          </div>
          <p class="text-muted-foreground mt-3 text-xs">
            {{ sprint.startsOn }} – {{ sprint.endsOn }} · {{ sprint.progress.remainingHours }}h
            remaining
          </p>
          <div class="mt-4 flex gap-2">
            <RouterLink
              class="border-input rounded border px-2.5 py-1.5 text-sm"
              :to="`/o/${slug}/p/${projectKey}/teams/${teamId}/sprints/${sprint.id}`"
              >Open</RouterLink
            ><button
              v-if="sprint.state === 'planned'"
              class="border-input inline-flex items-center gap-1 rounded border px-2.5 py-1.5 text-sm"
              @click="start(sprint)"
            >
              <Play class="size-3.5" /> Start</button
            ><button
              v-if="sprint.state === 'planned'"
              class="text-muted-foreground ml-auto rounded p-1.5"
              :aria-label="`Edit ${sprint.name}`"
              @click="edit(sprint)"
            >
              <Pencil class="size-4" />
            </button>
          </div>
        </article>
        <p
          v-if="!sprints.isPending.value && !sprintList.length"
          class="text-muted-foreground py-12 text-center md:col-span-2 xl:col-span-3"
        >
          Create your first sprint to begin planning.
        </p>
      </div>
    </main>
    <div v-if="dialogOpen" class="bg-background/80 fixed inset-0 z-50 grid place-items-center p-4">
      <form
        class="bg-background border-border w-full max-w-lg rounded-lg border p-5 shadow-xl"
        @submit.prevent="save"
      >
        <div class="flex items-start justify-between gap-3">
          <div>
            <h2 class="font-semibold">{{ editing ? 'Edit sprint' : 'New sprint' }}</h2>
            <p class="text-muted-foreground text-sm">Dates default to this team’s sprint length.</p>
          </div>
          <button
            type="button"
            class="text-muted-foreground"
            aria-label="Close"
            @click="dialogOpen = false"
          >
            ×
          </button>
        </div>
        <label class="mt-5 block text-sm font-medium"
          >Name<input
            v-model="form.name"
            required
            maxlength="100"
            class="border-input mt-1 w-full rounded border px-3 py-2 font-normal" /></label
        ><label class="mt-3 block text-sm font-medium"
          >Goal<textarea
            v-model="form.goal"
            class="border-input mt-1 min-h-20 w-full rounded border px-3 py-2 font-normal"
          />
        </label>
        <div class="mt-3 grid grid-cols-2 gap-3">
          <label class="text-sm font-medium"
            >Starts<input
              v-model="form.startsOn"
              type="date"
              required
              class="border-input mt-1 w-full rounded border px-3 py-2 font-normal" /></label
          ><label class="text-sm font-medium"
            >Ends<input
              v-model="form.endsOn"
              type="date"
              required
              class="border-input mt-1 w-full rounded border px-3 py-2 font-normal"
          /></label>
        </div>
        <label class="mt-3 flex items-center gap-2 text-sm"
          ><input v-model="form.autoCreateNext" type="checkbox" /> Automatically create the next
          sprint</label
        >
        <div class="mt-5 flex justify-end gap-2">
          <button
            type="button"
            class="border-input rounded border px-3 py-2 text-sm"
            @click="dialogOpen = false"
          >
            Cancel</button
          ><button
            class="bg-primary text-primary-foreground rounded px-3 py-2 text-sm"
            :disabled="saving"
          >
            {{ saving ? 'Saving…' : 'Save sprint' }}
          </button>
        </div>
      </form>
    </div>
  </AppShell>
</template>
