<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import type { WorkItemType } from '@/api/items'
import {
  connectJira,
  getJiraImport,
  isJiraImportActive,
  listJiraImports,
  previewJiraImport,
  startJiraImport,
  type JiraConnection,
  type JiraImportJob,
  type JiraImportPreview,
} from '@/api/jira-import'
import { hasProjectRole } from '@/api/projects'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input, PasswordInput } from '@/components/ui/input'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * A one-time move from Jira Cloud: connect, pick the Jira project, check how its types,
 * statuses and people land here, then watch the background job.
 *
 * The API token lives in this component's memory only - never in storage, a store or the
 * URL - and is dropped the moment the job is queued. Leaving the tab mid-wizard forgets
 * it too, which is the point: the next import asks again.
 */
const project = useProjectScope()
const toast = useToast()

const record = computed(() => project.record.value!)
const mayImport = computed(
  () => hasProjectRole(record.value.role, 'member') && !record.value.isArchived,
)

type Step = 'connect' | 'pick' | 'mapping' | 'job'
const step = ref<Step>('connect')

const itemTypes: { value: WorkItemType; label: string }[] = [
  { value: 'epic', label: 'Epic' },
  { value: 'feature', label: 'Feature' },
  { value: 'story', label: 'Story' },
  { value: 'task', label: 'Task' },
  { value: 'bug', label: 'Bug' },
]

// ── Connect ─────────────────────────────────────────────────────────────────────────
const siteUrl = ref('')
const email = ref('')
const apiToken = ref('')
const fieldErrors = ref<Record<string, string[]>>({})
const busy = ref(false)
const connection = ref<JiraConnection | null>(null)

const credentials = () => ({
  siteUrl: connection.value?.siteUrl ?? siteUrl.value.trim(),
  email: email.value.trim(),
  apiToken: apiToken.value,
})

/** Field errors inline; anything else (Jira down, a 500) as a toast. */
function report(error: unknown) {
  if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
    fieldErrors.value = error.fieldErrors
  } else {
    toast.error(error)
  }
}

async function connect() {
  busy.value = true
  fieldErrors.value = {}
  try {
    connection.value = await connectJira(project.slug.value, project.projectKey.value, {
      siteUrl: siteUrl.value.trim(),
      email: email.value.trim(),
      apiToken: apiToken.value,
    })
    siteUrl.value = connection.value.siteUrl
    projectQuery.value = ''
    step.value = 'pick'
  } catch (error) {
    connection.value = null
    report(error)
  } finally {
    busy.value = false
  }
}

// ── Pick ────────────────────────────────────────────────────────────────────────────
const projectQuery = ref('')
const preview = ref<JiraImportPreview | null>(null)
const previewingKey = ref<string | null>(null)

const visibleProjects = computed(() => {
  const needle = projectQuery.value.trim().toLowerCase()
  return (connection.value?.projects ?? []).filter(
    (candidate) =>
      !needle ||
      candidate.key.toLowerCase().includes(needle) ||
      candidate.name.toLowerCase().includes(needle),
  )
})

const typeMapping = ref<Record<string, WorkItemType>>({})
const statusMapping = ref<Record<string, string>>({})

async function pick(jiraProjectKey: string) {
  previewingKey.value = jiraProjectKey
  fieldErrors.value = {}
  try {
    const result = await previewJiraImport(project.slug.value, project.projectKey.value, {
      ...credentials(),
      jiraProjectKey,
    })
    preview.value = result
    // Pre-filled from the API's suggestions; every row stays editable.
    typeMapping.value = Object.fromEntries(
      result.issueTypes.map((type) => [type.name, type.suggestedType]),
    )
    statusMapping.value = Object.fromEntries(
      result.statuses.map((status) => [status.name, status.suggestedStateId]),
    )
    step.value = 'mapping'
  } catch (error) {
    report(error)
  } finally {
    previewingKey.value = null
  }
}

const unmatchedUsers = computed(
  () => preview.value?.users.filter((user) => !user.matchedUserId).length ?? 0,
)

// ── Start ───────────────────────────────────────────────────────────────────────────
async function start() {
  if (!preview.value) return
  busy.value = true
  fieldErrors.value = {}
  try {
    const queued = await startJiraImport(project.slug.value, project.projectKey.value, {
      ...credentials(),
      jiraProjectKey: preview.value.jiraProject.key,
      issueTypes: typeMapping.value,
      statuses: statusMapping.value,
    })
    // The job has its own copy for as long as it runs; this one is no longer needed.
    apiToken.value = ''
    connection.value = null
    preview.value = null
    showJob(queued)
  } catch (error) {
    if (error instanceof ConflictError) {
      // One import per project at a time: show the one already running instead.
      toast.error(error)
      apiToken.value = ''
      connection.value = null
      preview.value = null
      await loadHistory()
    } else {
      report(error)
    }
  } finally {
    busy.value = false
  }
}

function startOver() {
  stopPolling()
  job.value = null
  connection.value = null
  preview.value = null
  apiToken.value = ''
  fieldErrors.value = {}
  step.value = 'connect'
}

// ── Progress and result ─────────────────────────────────────────────────────────────
const job = ref<JiraImportJob | null>(null)
const history = ref<JiraImportJob[]>([])
const loadingHistory = ref(true)
let timer: ReturnType<typeof setTimeout> | null = null

const percent = computed(() => {
  const current = job.value
  if (!current || current.totalIssues === 0) return current?.status === 'completed' ? 100 : 0
  return Math.min(100, Math.round((current.processedIssues / current.totalIssues) * 100))
})

const summary = computed(() => {
  const current = job.value
  if (!current) return []
  return [
    { label: 'Items created', value: current.createdItems },
    { label: 'Comments', value: current.createdComments },
    { label: 'Attachments', value: current.createdAttachments },
    { label: 'Sprints', value: current.createdSprints },
    { label: 'Skipped (already imported)', value: current.skippedIssues },
  ]
})

function stopPolling() {
  if (timer) clearTimeout(timer)
  timer = null
}

function showJob(next: JiraImportJob) {
  job.value = next
  step.value = 'job'
  history.value = [next, ...history.value.filter((entry) => entry.id !== next.id)]
  stopPolling()
  if (isJiraImportActive(next)) timer = setTimeout(poll, 2000)
}

async function poll() {
  const current = job.value
  if (!current) return
  try {
    showJob(await getJiraImport(project.slug.value, project.projectKey.value, current.id))
  } catch (error) {
    // One missed poll is not a failed import; try again on the next tick.
    if (error instanceof ApiError && error.status === 404) stopPolling()
    else timer = setTimeout(poll, 4000)
  }
}

async function loadHistory() {
  // Import history is a member's, like the import itself; a guest only sees why not.
  if (!hasProjectRole(record.value.role, 'member')) {
    history.value = []
    loadingHistory.value = false
    return
  }
  loadingHistory.value = true
  try {
    history.value = await listJiraImports(project.slug.value, project.projectKey.value)
    const running = history.value.find(isJiraImportActive)
    if (running) showJob(running)
  } catch (error) {
    history.value = []
    toast.error(error)
  } finally {
    loadingHistory.value = false
  }
}

function formatDate(value: string | null) {
  return value ? new Date(value).toLocaleString() : ''
}

const statusLabel: Record<JiraImportJob['status'], string> = {
  pending: 'Queued',
  running: 'Importing',
  completed: 'Finished',
  failed: 'Failed',
}

watch([project.slug, project.projectKey], loadHistory, { immediate: true })
onBeforeUnmount(stopPolling)
</script>

<template>
  <div class="grid gap-8">
    <SettingsSection
      wide
      title="Import from Jira"
      description="Bring every issue of one Jira Cloud project here, with its comments, attachments and sprints. Issues already imported are skipped, so running it again never duplicates anything."
    >
      <p
        v-if="!mayImport"
        class="border-border text-muted-foreground rounded-lg border border-dashed p-4 text-sm"
        data-testid="jira-import-disabled"
      >
        {{
          record.isArchived
            ? 'This project is archived, so nothing can be imported into it until it is restored.'
            : 'Only project members who can edit work can import from Jira.'
        }}
      </p>

      <template v-else>
        <ol class="text-muted-foreground mb-5 flex flex-wrap gap-4 text-xs" aria-label="Steps">
          <li
            v-for="(label, key) in {
              connect: '1. Connect',
              pick: '2. Pick project',
              mapping: '3. Mapping',
              job: '4. Import',
            }"
            :key="key"
            :class="step === key && 'text-foreground font-medium'"
            :aria-current="step === key ? 'step' : undefined"
          >
            {{ label }}
          </li>
        </ol>

        <!-- 1. Connect -->
        <form v-if="step === 'connect'" class="grid max-w-md gap-4" @submit.prevent="connect">
          <div class="grid gap-1.5">
            <label for="jira-site" class="text-sm font-medium">Jira site URL</label>
            <Input
              id="jira-site"
              v-model="siteUrl"
              placeholder="https://your-team.atlassian.net"
              autocomplete="url"
              required
              :aria-invalid="!!fieldErrors.siteUrl"
            />
            <p v-if="fieldErrors.siteUrl" class="text-destructive text-xs" role="alert">
              {{ fieldErrors.siteUrl[0] }}
            </p>
          </div>
          <div class="grid gap-1.5">
            <label for="jira-email" class="text-sm font-medium">Account email</label>
            <Input
              id="jira-email"
              v-model="email"
              type="email"
              autocomplete="email"
              required
              :aria-invalid="!!fieldErrors.email"
            />
            <p v-if="fieldErrors.email" class="text-destructive text-xs" role="alert">
              {{ fieldErrors.email[0] }}
            </p>
          </div>
          <div class="grid gap-1.5">
            <label for="jira-token" class="text-sm font-medium">API token</label>
            <PasswordInput
              id="jira-token"
              v-model="apiToken"
              autocomplete="off"
              required
              :aria-invalid="!!fieldErrors.apiToken"
            />
            <p v-if="fieldErrors.apiToken" class="text-destructive text-xs" role="alert">
              {{ fieldErrors.apiToken[0] }}
            </p>
            <p class="text-muted-foreground text-xs">
              Create one at id.atlassian.com under Security, API tokens. It is used for this import
              only and is not kept after the import ends.
            </p>
          </div>
          <div>
            <Button type="submit" :disabled="busy">
              <Loader2 v-if="busy" class="size-4 animate-spin" aria-hidden="true" />
              Connect
            </Button>
          </div>
        </form>

        <!-- 2. Pick a Jira project -->
        <div v-else-if="step === 'pick' && connection" class="grid max-w-xl gap-3">
          <p class="text-sm">
            Connected to <span class="font-mono">{{ connection.siteUrl }}</span> as
            <span class="font-medium">{{ connection.accountName }}</span
            >. Pick the Jira project to import.
          </p>
          <Input
            v-model="projectQuery"
            placeholder="Search Jira projects"
            aria-label="Search Jira projects"
          />
          <ul class="border-border divide-border divide-y rounded-lg border">
            <li
              v-for="candidate in visibleProjects"
              :key="candidate.id"
              class="flex items-center justify-between gap-3 px-3 py-2"
            >
              <span class="min-w-0 truncate text-sm">
                <span class="font-mono text-xs">{{ candidate.key }}</span>
                {{ candidate.name }}
              </span>
              <Button
                size="sm"
                variant="secondary"
                :disabled="previewingKey !== null"
                @click="pick(candidate.key)"
              >
                <Loader2
                  v-if="previewingKey === candidate.key"
                  class="size-4 animate-spin"
                  aria-hidden="true"
                />
                Select
              </Button>
            </li>
            <li v-if="!visibleProjects.length" class="text-muted-foreground px-3 py-3 text-sm">
              No Jira projects this account can see match.
            </li>
          </ul>
          <p v-if="fieldErrors.jiraProjectKey" class="text-destructive text-xs" role="alert">
            {{ fieldErrors.jiraProjectKey[0] }}
          </p>
          <div><Button variant="ghost" size="sm" @click="startOver">Back</Button></div>
        </div>

        <!-- 3. Mapping preview -->
        <div v-else-if="step === 'mapping' && preview" class="grid gap-6">
          <p class="text-sm" data-testid="jira-issue-count">
            <span class="font-medium">{{ preview.jiraProject.name }}</span>
            ({{ preview.jiraProject.key }}) has
            <span class="font-medium">{{ preview.issueCount }}</span>
            {{ preview.issueCount === 1 ? 'issue' : 'issues' }}.
            <template v-if="preview.alreadyImported">
              {{ preview.alreadyImported }} already imported, will be skipped.
            </template>
          </p>

          <div class="grid gap-6 lg:grid-cols-2">
            <section>
              <h3 class="mb-2 text-sm font-medium">Issue types</h3>
              <table class="w-full text-sm">
                <thead class="text-muted-foreground text-left text-xs">
                  <tr>
                    <th class="py-1 font-normal">Jira type</th>
                    <th class="py-1 font-normal">Aictiq type</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="type in preview.issueTypes" :key="type.name">
                    <td class="py-1 pr-3">{{ type.name }}</td>
                    <td class="py-1">
                      <select
                        v-model="typeMapping[type.name]"
                        class="border-input bg-background h-8 w-full rounded-md border px-2 text-sm"
                        :aria-label="`Aictiq type for ${type.name}`"
                      >
                        <option
                          v-for="option in itemTypes"
                          :key="option.value"
                          :value="option.value"
                        >
                          {{ option.label }}
                        </option>
                      </select>
                    </td>
                  </tr>
                </tbody>
              </table>
            </section>

            <section>
              <h3 class="mb-2 text-sm font-medium">Statuses</h3>
              <table class="w-full text-sm">
                <thead class="text-muted-foreground text-left text-xs">
                  <tr>
                    <th class="py-1 font-normal">Jira status</th>
                    <th class="py-1 font-normal">Workflow state</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="status in preview.statuses" :key="status.name">
                    <td class="py-1 pr-3">{{ status.name }}</td>
                    <td class="py-1">
                      <select
                        v-model="statusMapping[status.name]"
                        class="border-input bg-background h-8 w-full rounded-md border px-2 text-sm"
                        :aria-label="`Workflow state for ${status.name}`"
                      >
                        <option v-for="state in preview.states" :key="state.id" :value="state.id">
                          {{ state.name }}
                        </option>
                      </select>
                    </td>
                  </tr>
                </tbody>
              </table>
            </section>
          </div>

          <section>
            <h3 class="text-sm font-medium">People</h3>
            <p class="text-muted-foreground mt-0.5 mb-2 text-xs">
              Matched to project members by email.
              <template v-if="unmatchedUsers">
                {{ unmatchedUsers }} not matched: their issues are left unassigned and their name is
                kept on the item; their comments are posted by you, starting with their name.
              </template>
            </p>
            <table class="w-full max-w-2xl text-sm">
              <tbody>
                <tr v-for="user in preview.users" :key="user.accountId">
                  <td class="py-1 pr-3">{{ user.displayName }}</td>
                  <td class="py-1">
                    <span v-if="user.matchedUserId">{{ user.matchedDisplayName }}</span>
                    <span v-else class="text-muted-foreground">
                      Not a member - left unassigned, name kept on the item
                    </span>
                  </td>
                </tr>
                <tr v-if="!preview.users.length">
                  <td class="text-muted-foreground py-1">No Jira users found on this project.</td>
                </tr>
              </tbody>
            </table>
          </section>

          <p v-if="Object.keys(fieldErrors).length" class="text-destructive text-xs" role="alert">
            {{ Object.values(fieldErrors).flat()[0] }}
          </p>
          <div class="flex gap-2">
            <Button :disabled="busy" @click="start">
              <Loader2 v-if="busy" class="size-4 animate-spin" aria-hidden="true" />
              Import {{ preview.issueCount - preview.alreadyImported }}
              {{ preview.issueCount - preview.alreadyImported === 1 ? 'issue' : 'issues' }}
            </Button>
            <Button variant="ghost" :disabled="busy" @click="step = 'pick'">Back</Button>
          </div>
        </div>

        <!-- 4. Progress and result -->
        <div v-else-if="step === 'job' && job" class="grid max-w-2xl gap-4" data-testid="jira-job">
          <div>
            <div class="flex items-baseline justify-between text-sm">
              <span>
                <span class="font-medium">{{ statusLabel[job.status] }}</span>
                {{ job.jiraProjectName }} ({{ job.jiraProjectKey }})
              </span>
              <span class="text-muted-foreground font-mono text-xs">
                {{ job.processedIssues }} / {{ job.totalIssues }}
              </span>
            </div>
            <div
              class="bg-muted mt-2 h-2 overflow-hidden rounded-full"
              role="progressbar"
              :aria-valuenow="percent"
              aria-valuemin="0"
              aria-valuemax="100"
              aria-label="Import progress"
            >
              <div
                class="h-full rounded-full transition-[width]"
                :class="job.status === 'failed' ? 'bg-destructive' : 'bg-primary'"
                :style="{ width: `${percent}%` }"
              />
            </div>
          </div>

          <dl class="grid grid-cols-2 gap-3 sm:grid-cols-5">
            <div
              v-for="entry in summary"
              :key="entry.label"
              class="border-border rounded-lg border p-3"
            >
              <dt class="text-muted-foreground text-xs">{{ entry.label }}</dt>
              <dd class="mt-1 font-mono text-lg">{{ entry.value }}</dd>
            </div>
          </dl>

          <section v-if="job.failures.length" data-testid="jira-failures">
            <h3 class="text-sm font-medium">
              {{ job.failures.length }} {{ job.failures.length === 1 ? 'problem' : 'problems' }}
            </h3>
            <ul class="border-border divide-border mt-2 divide-y rounded-lg border text-sm">
              <li
                v-for="(failure, index) in job.failures"
                :key="index"
                class="flex gap-3 px-3 py-2"
              >
                <span class="w-20 shrink-0 font-mono text-xs">{{ failure.issueKey ?? '-' }}</span>
                <span class="text-muted-foreground w-20 shrink-0 text-xs">{{ failure.kind }}</span>
                <span class="min-w-0">{{ failure.reason }}</span>
              </li>
            </ul>
          </section>
          <p v-else-if="!isJiraImportActive(job)" class="text-muted-foreground text-sm">
            Nothing failed.
          </p>

          <div v-if="!isJiraImportActive(job)">
            <Button variant="secondary" size="sm" @click="startOver">Start another import</Button>
          </div>
        </div>
      </template>
    </SettingsSection>

    <SettingsSection title="Past imports">
      <UiPageState v-if="loadingHistory" state="loading" />
      <ul
        v-else-if="history.length"
        class="border-border divide-border divide-y rounded-lg border text-sm"
      >
        <li v-for="entry in history" :key="entry.id" class="flex items-center gap-3 px-3 py-2">
          <span class="min-w-0 flex-1 truncate">
            <span class="font-mono text-xs">{{ entry.jiraProjectKey }}</span>
            {{ entry.jiraProjectName }}
            <span class="text-muted-foreground text-xs">· {{ formatDate(entry.createdAt) }}</span>
          </span>
          <span class="text-muted-foreground text-xs">
            {{ statusLabel[entry.status] }} · {{ entry.createdItems }} items ·
            {{ entry.failures.length }} {{ entry.failures.length === 1 ? 'problem' : 'problems' }}
          </span>
          <Button size="xs" variant="ghost" @click="showJob(entry)">View</Button>
        </li>
      </ul>
      <p v-else class="text-muted-foreground text-sm">No imports into this project yet.</p>
    </SettingsSection>
  </div>
</template>
