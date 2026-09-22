<script setup lang="ts">
import { Loader2, Star } from '@lucide/vue'
import { toTypedSchema } from '@vee-validate/zod'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useForm } from 'vee-validate'
import { z } from 'zod'

import { createTeam, listTeams, type Team } from '@/api/teams'
import EmptyState from '@/components/common/EmptyState.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { teamSettingsPath } from '@/router/paths'
import { useTeamsStore } from '@/stores/teams'
import { ApiError } from '@/utils/api'

/** The project-owned list of teams, with the one action that creates another. */
const project = useProjectScope()
const teamsStore = useTeamsStore()
const toast = useToast()

const teams = ref<Team[]>([])
const loading = ref(true)
const creating = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const { defineField, errors: validationErrors, handleSubmit, resetForm } = useForm({
  validationSchema: toTypedSchema(
    z.object({ name: z.string().trim().min(1, 'Enter a team name.').max(120) }),
  ),
  initialValues: { name: '' },
})
const [name] = defineField('name')

const record = computed(() => project.record.value!)
const mayManage = computed(() => record.value.role === 'admin' && !record.value.isArchived)
const slug = computed(() => project.slug.value)
const projectKey = computed(() => project.projectKey.value)

function focusNewTeam() {
  document.getElementById('project-new-team')?.focus()
}

async function load() {
  loading.value = true
  try {
    teams.value = await listTeams(project.slug.value, project.projectKey.value)
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([project.slug, project.projectKey], load, { immediate: true })

const create = handleSubmit(async (values) => {
  creating.value = true
  fieldErrors.value = {}
  try {
    const created = await createTeam(project.slug.value, project.projectKey.value, { name: values.name })
    teams.value = [...teams.value, created].sort((a, b) => a.name.localeCompare(b.name))
    resetForm()
    await teamsStore.reload()
    toast.success(`${created.name} is ready.`)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    creating.value = false
  }
})
</script>

<template>
  <SettingsSection wide title="Teams" description="Backlogs, boards and sprints belong to a team.">
    <form v-if="mayManage" class="mb-4 flex items-start gap-2" novalidate @submit.prevent="create">
      <div class="flex-1 space-y-1.5">
        <label for="project-new-team" class="sr-only">New team name</label>
        <Input id="project-new-team" v-model="name" placeholder="Platform" :aria-invalid="Boolean(fieldErrors.name)" />
        <p v-if="validationErrors.name" class="text-destructive text-xs">{{ validationErrors.name }}</p>
        <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">{{ message }}</p>
      </div>
      <Button type="submit" :disabled="creating">
        <Loader2 v-if="creating" class="animate-spin" aria-hidden="true" /> Add team
      </Button>
    </form>

    <UiPageState v-if="loading" state="loading" />
    <EmptyState v-else-if="teams.length === 0" title="No teams yet" description="Create a team to start planning work." icon="◍">
      <Button v-if="mayManage" @click="focusNewTeam">Add team</Button>
    </EmptyState>
    <table v-else class="w-full border-collapse text-sm">
      <thead><tr class="border-border border-b"><th scope="col" class="font-label px-3 py-2 text-left">Team</th><th scope="col" class="font-label px-3 py-2 text-left">Sprint</th><th scope="col" class="font-label px-3 py-2 text-left">People</th></tr></thead>
      <tbody>
        <tr v-for="team in teams" :key="team.id" class="border-border/60 hover:bg-accent/60 border-b last:border-b-0">
          <td class="px-3 py-2"><RouterLink :to="teamSettingsPath(slug, projectKey, team.id)" class="font-medium hover:underline">{{ team.name }}</RouterLink><span class="text-muted-foreground ml-1.5 font-mono text-[11px]">{{ team.key }}</span><Star v-if="team.isDefault" class="text-muted-foreground ml-1 inline size-3" aria-label="Default team" /></td>
          <td class="text-muted-foreground px-3 py-2 text-xs">{{ team.sprintLengthDays }} days · {{ team.estimationUnit }}</td>
          <td class="text-muted-foreground px-3 py-2 text-xs">{{ team.memberCount }}</td>
        </tr>
      </tbody>
    </table>
    <p v-if="teams.length" class="text-muted-foreground pt-3 text-xs">Every project has one default team, where work lands when nobody names one.</p>
  </SettingsSection>
</template>
