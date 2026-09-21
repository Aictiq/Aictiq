<script setup lang="ts">
import { Loader2, MoreHorizontal, Star } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { listProjectMembers, type ProjectMember } from '@/api/projects'
import {
  dayNames,
  deleteTeam,
  getTeam,
  listTeamMembers,
  removeTeamMember,
  setTeamMember,
  updateTeam,
  type EstimationUnit,
  type Team,
  type TeamMember,
} from '@/api/teams'
import EmptyState from '@/components/common/EmptyState.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * One team: how it plans, who is on it, and how much of each of them a sprint can count on.
 *
 * Everything here is open to a project admin **or** this team's lead. The page asks the
 * API which of those the viewer is (`isLead`, and the project role) rather than deciding
 * for itself, so it never offers a control that answers 403.
 */
const route = useRoute()
const router = useRouter()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const teams = useTeamsStore()
const session = useSessionStore()
const toast = useToast()

const projectKey = computed(() => String(route.params.projectKey ?? ''))
const teamId = computed(() => String(route.params.teamId ?? ''))
const slug = computed(() => organizations.currentSlug)

const team = ref<Team | null>(null)
const members = ref<TeamMember[]>([])
const candidates = ref<ProjectMember[]>([])
const loading = ref(true)
const saving = ref(false)
const removing = ref(false)
const busyUserId = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})
const tab = ref<'planning' | 'members'>('planning')

const name = ref('')
const sprintLengthDays = ref(14)
const workingDays = ref<number[]>([1, 2, 3, 4, 5])
const estimationUnit = ref<EstimationUnit>('points')
const timeZone = ref('')

const projectRole = computed(() => projects.projects.find((p) => p.key === projectKey.value)?.role)
/** A project admin runs every team; a lead runs theirs. Both, and nobody else. */
const mayManage = computed(() => projectRole.value === 'admin' || team.value?.isLead === true)
/** Deleting is a project decision — a lead cannot delete the team out from under it. */
const mayDelete = computed(() => projectRole.value === 'admin' && team.value?.isDefault === false)

const timeZones =
  typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []

const addable = computed(() => {
  const onTeam = new Set(members.value.map((m) => m.userId))
  return candidates.value.filter((c) => !onTeam.has(c.userId))
})

function fill(loaded: Team) {
  team.value = loaded
  name.value = loaded.name
  sprintLengthDays.value = loaded.sprintLengthDays
  workingDays.value = [...loaded.workingDays]
  estimationUnit.value = loaded.estimationUnit
  timeZone.value = loaded.timeZone ?? ''
}

async function load() {
  loading.value = true
  fieldErrors.value = {}

  await organizations.load()
  await projects.load()
  if (!slug.value || !projectKey.value || !teamId.value) {
    loading.value = false
    return
  }

  try {
    fill(await getTeam(slug.value, projectKey.value, teamId.value))
    members.value = await listTeamMembers(slug.value, projectKey.value, teamId.value)
  } catch (error) {
    team.value = null
    if (!(error instanceof ApiError) || error.status !== 404) toast.error(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch([slug, projectKey, teamId], load)

watch(tab, async (next) => {
  // Only project members can be on a team, so that is the list to pick from — and it is
  // not needed until the tab is open.
  if (next !== 'members' || !slug.value || candidates.value.length > 0) return
  try {
    candidates.value = await listProjectMembers(slug.value, projectKey.value)
  } catch (error) {
    toast.error(error)
  }
})

function toggleDay(day: number) {
  workingDays.value = workingDays.value.includes(day)
    ? workingDays.value.filter((d) => d !== day)
    : [...workingDays.value, day].sort((a, b) => a - b)
}

async function save() {
  if (!team.value || !slug.value) return

  saving.value = true
  fieldErrors.value = {}

  try {
    const updated = await updateTeam(slug.value, projectKey.value, team.value.id, {
      name: name.value.trim(),
      sprintLengthDays: sprintLengthDays.value,
      workingDays: workingDays.value,
      estimationUnit: estimationUnit.value,
      // Empty means "clear the override" to the API, which is what an emptied field means.
      timeZone: timeZone.value,
      version: team.value.version,
    })
    fill(updated)
    teams.replace(updated)
    toast.success('Saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      toast.error(error)
      await load()
    } else if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}

async function makeDefault() {
  if (!team.value || !slug.value) return

  saving.value = true
  try {
    const updated = await updateTeam(slug.value, projectKey.value, team.value.id, {
      isDefault: true,
      version: team.value.version,
    })
    fill(updated)
    teams.replace(updated)
    toast.success(`${updated.name} is now where new work lands.`)
  } catch (error) {
    toast.error(error)
  } finally {
    saving.value = false
  }
}

async function destroy() {
  if (!team.value || !slug.value) return

  removing.value = true
  try {
    const { id, name: deleted } = team.value
    await deleteTeam(slug.value, projectKey.value, id)
    teams.remove(id)
    toast.success(`${deleted} was deleted.`)
    await router.replace(`/projects/${projectKey.value}/settings`)
  } catch (error) {
    // 409 when items still point at it — the message says which.
    toast.error(error)
  } finally {
    removing.value = false
  }
}

async function setMember(userId: string, body: { isLead?: boolean; capacityHoursPerDay?: number }) {
  if (!team.value || !slug.value) return

  busyUserId.value = userId
  try {
    await setTeamMember(slug.value, projectKey.value, team.value.id, userId, body)
    members.value = await listTeamMembers(slug.value, projectKey.value, team.value.id)
    team.value = await getTeam(slug.value, projectKey.value, team.value.id)
  } catch (error) {
    toast.error(error)
  } finally {
    busyUserId.value = null
  }
}

async function removeMember(member: TeamMember) {
  if (!team.value || !slug.value) return

  busyUserId.value = member.userId
  try {
    await removeTeamMember(slug.value, projectKey.value, team.value.id, member.userId)
    members.value = await listTeamMembers(slug.value, projectKey.value, team.value.id)
    if (member.userId === session.user?.id) team.value = await getTeam(slug.value, projectKey.value, team.value.id)
  } catch (error) {
    toast.error(error)
  } finally {
    busyUserId.value = null
  }
}
</script>

<template>
  <UiPageState v-if="loading" state="loading" />

  <EmptyState
      v-else-if="!team"
      title="Team not found"
      description="It does not exist in this project, or you do not have access to it."
      icon="◇"
    >
      <Button @click="router.replace('/projects')">All projects</Button>
  </EmptyState>

  <div v-else>
      <header class="border-border border-b pb-4">
        <h1 class="flex items-center gap-2 text-xl font-semibold tracking-tight">
          {{ team.name }}
          <Star
            v-if="team.isDefault"
            class="text-muted-foreground size-4"
            aria-label="The project's default team"
          />
        </h1>
        <p class="text-muted-foreground mt-1 text-[12.5px]">
          <span class="font-mono">{{ team.key }}</span> · {{ team.memberCount }}
          {{ team.memberCount === 1 ? 'person' : 'people' }}
          <template v-if="team.isDefault"> · new work lands here</template>
        </p>
      </header>

      <div class="border-border flex items-center gap-4 border-b" role="tablist">
        <button
          v-for="option in (['planning', 'members'] as const)"
          :key="option"
          type="button"
          role="tab"
          :aria-selected="tab === option"
          class="-mb-px border-b-2 px-1 py-2 text-[12.5px] capitalize"
          :class="
            tab === option
              ? 'border-primary text-foreground font-medium'
              : 'text-muted-foreground hover:text-foreground border-transparent'
          "
          @click="tab = option"
        >
          {{ option }}
        </button>
      </div>

      <template v-if="tab === 'planning'">
        <form class="space-y-5 py-6" novalidate @submit.prevent="save">
          <div class="space-y-1.5">
            <label for="team-name" class="text-sm font-medium">Name</label>
            <Input
              id="team-name"
              v-model="name"
              required
              :disabled="!mayManage"
              :aria-invalid="Boolean(fieldErrors.name)"
            />
            <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>

          <div class="space-y-1.5">
            <label for="team-sprint-length" class="text-sm font-medium">Sprint length</label>
            <div class="flex items-center gap-2">
              <Input
                id="team-sprint-length"
                v-model.number="sprintLengthDays"
                type="number"
                min="1"
                max="28"
                class="w-24"
                :disabled="!mayManage"
                :aria-invalid="Boolean(fieldErrors.sprintLengthDays)"
              />
              <span class="text-muted-foreground text-sm">days</span>
            </div>
            <p
              v-for="message in fieldErrors.sprintLengthDays"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">Working days</legend>
            <div class="flex flex-wrap gap-1.5">
              <button
                v-for="(label, day) in dayNames"
                :key="label"
                type="button"
                :disabled="!mayManage"
                :aria-pressed="workingDays.includes(day)"
                class="border-border rounded-lg border px-2.5 py-1 text-xs disabled:opacity-50"
                :class="
                  workingDays.includes(day)
                    ? 'bg-primary text-primary-foreground border-primary'
                    : 'text-muted-foreground hover:bg-accent'
                "
                @click="toggleDay(day)"
              >
                {{ label.slice(0, 3) }}
              </button>
            </div>
            <p class="text-muted-foreground text-xs">
              Capacity and burndown are counted in these — a team that does not work Fridays
              should not be told it should have.
            </p>
            <p
              v-for="message in fieldErrors.workingDays"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </fieldset>

          <div class="space-y-1.5">
            <label for="team-estimation" class="text-sm font-medium">Estimate in</label>
            <select
              id="team-estimation"
              v-model="estimationUnit"
              :disabled="!mayManage"
              class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
            >
              <option value="points">Story points</option>
              <option value="hours">Hours</option>
            </select>
          </div>

          <div class="space-y-1.5">
            <label for="team-timezone" class="text-sm font-medium">
              Time zone <span class="text-muted-foreground font-normal">(optional)</span>
            </label>
            <select
              id="team-timezone"
              v-model="timeZone"
              :disabled="!mayManage"
              class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
            >
              <option value="">Follow the organization</option>
              <option v-if="timeZone && !timeZones.includes(timeZone)" :value="timeZone">
                {{ timeZone }}
              </option>
              <option v-for="zone in timeZones" :key="zone" :value="zone">{{ zone }}</option>
            </select>
            <p class="text-muted-foreground text-xs">
              Where this team's days begin and end. A second office plans in its own hours.
            </p>
            <p
              v-for="message in fieldErrors.timeZone"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <div v-if="mayManage" class="flex justify-end">
            <Button type="submit" :disabled="saving || !name.trim()">
              <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
              Save changes
            </Button>
          </div>
          <p v-else class="text-muted-foreground text-xs">
            Only a project admin or this team's lead can change these.
          </p>
        </form>

        <section
          v-if="projectRole === 'admin' && !team.isDefault"
          class="border-border mt-4 space-y-3 rounded-lg border p-4"
        >
          <div>
            <h2 class="text-sm font-medium">Make this the default team</h2>
            <p class="text-muted-foreground mt-1 text-xs">
              New work lands on the default team when nobody names one. Promoting this team
              demotes the current default in the same breath — a project always has exactly one.
            </p>
            <div class="mt-3 flex justify-end">
              <Button variant="outline" :disabled="saving" @click="makeDefault">
                Make default
              </Button>
            </div>
          </div>
        </section>

        <section v-if="mayDelete" class="border-destructive/30 mt-4 rounded-lg border p-4">
          <h2 class="text-destructive text-sm font-medium">Delete team</h2>
          <p class="text-muted-foreground mt-1 text-xs">
            Only possible while no items are assigned to it. Its members stay on the project.
          </p>
          <div class="mt-3 flex justify-end">
            <Button variant="destructive" :disabled="removing" @click="destroy">
              <Loader2 v-if="removing" class="animate-spin" aria-hidden="true" />
              Delete
            </Button>
          </div>
        </section>
      </template>

      <template v-else>
        <div v-if="mayManage" class="py-3">
          <label for="team-add-member" class="sr-only">Add someone to this team</label>
          <select
            id="team-add-member"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none"
            @change="
              (event) => {
                const select = event.target as HTMLSelectElement
                if (select.value) setMember(select.value, {})
                select.value = ''
              }
            "
          >
            <option value="">Add someone from the project…</option>
            <option v-for="candidate in addable" :key="candidate.userId" :value="candidate.userId">
              {{ candidate.displayName }}
            </option>
          </select>
        </div>

        <EmptyState
          v-if="members.length === 0"
          title="Nobody on this team yet"
          description="Add people from the project to plan sprints and size capacity."
          icon="◍"
        />

        <table v-else class="w-full border-collapse text-sm">
          <thead>
            <tr class="border-border border-b">
              <th scope="col" class="font-label px-3 py-2 text-left">Person</th>
              <th scope="col" class="font-label px-3 py-2 text-left">Capacity</th>
              <th scope="col" class="w-10 px-3 py-2"><span class="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="member in members"
              :key="member.userId"
              class="border-border/60 hover:bg-accent/60 border-b last:border-b-0"
            >
              <td class="px-3 py-2">
                <div class="flex items-center gap-2.5">
                  <UserAvatar :name="member.displayName" :is-agent="member.isAgent" />
                  <div class="min-w-0">
                    <div class="truncate font-medium">
                      {{ member.displayName }}
                      <span v-if="member.isLead" class="text-muted-foreground font-normal">
                        · lead
                      </span>
                    </div>
                    <div v-if="member.email" class="text-muted-foreground truncate text-xs">
                      {{ member.email }}
                    </div>
                  </div>
                </div>
              </td>
              <td class="px-3 py-2">
                <div class="flex items-center gap-1.5">
                  <Input
                    :model-value="member.capacityHoursPerDay ?? ''"
                    type="number"
                    min="0"
                    max="24"
                    step="0.5"
                    class="w-20"
                    :disabled="!mayManage || busyUserId === member.userId"
                    :aria-label="`Hours per day for ${member.displayName}`"
                    @change="
                      (event: Event) =>
                        setMember(member.userId, {
                          capacityHoursPerDay: Number((event.target as HTMLInputElement).value),
                        })
                    "
                  />
                  <span class="text-muted-foreground text-xs">h/day</span>
                </div>
              </td>
              <td class="px-3 py-2 text-right">
                <Loader2
                  v-if="busyUserId === member.userId"
                  class="text-muted-foreground inline size-4 animate-spin"
                  aria-hidden="true"
                />
                <DropdownMenu v-else-if="mayManage || member.userId === session.user?.id">
                  <DropdownMenuTrigger as-child>
                    <Button variant="ghost" size="icon" :aria-label="`Manage ${member.displayName}`">
                      <MoreHorizontal class="size-4" aria-hidden="true" />
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end" class="w-52">
                    <DropdownMenuItem
                      v-if="mayManage"
                      @select="setMember(member.userId, { isLead: !member.isLead })"
                    >
                      {{ member.isLead ? 'Remove as lead' : 'Make team lead' }}
                    </DropdownMenuItem>
                    <DropdownMenuItem variant="destructive" @select="removeMember(member)">
                      {{ member.userId === session.user?.id ? 'Leave team' : 'Remove from team' }}
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </td>
            </tr>
          </tbody>
        </table>

        <p class="text-muted-foreground pt-3 text-xs">
          A lead runs this team — its planning settings and its roster — without running the
          project.
        </p>
      </template>
  </div>
</template>
