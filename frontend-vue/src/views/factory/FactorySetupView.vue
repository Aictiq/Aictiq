<script setup lang="ts">
import { ExternalLink, FolderGit2, Loader2, PartyPopper, RefreshCw, Server } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import { listAgents, type Agent } from '@/api/agents'
import { hasOrgRole } from '@/api/organizations'
import {
  createStarterPlaybook,
  getFactorySettings,
  listPlaybooks,
  type FactorySettings,
  type Playbook,
} from '@/api/playbooks'
import { listProjectMembers, listProjects, type Project, type ProjectMember } from '@/api/projects'
import { listRunnerChoices, listRunners, type Runner } from '@/api/runners'
import { listRuns } from '@/api/runs'
import CommandBlock from '@/components/factory/CommandBlock.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import FactoryFlowDiagram from '@/components/factory/FactoryFlowDiagram.vue'
import SetupStepRow from '@/components/factory/SetupStepRow.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import { Button } from '@/components/ui/button'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import {
  isSetupStepDone,
  machineSteps,
  projectSteps,
  readSetupTicks,
  runnerPlatformOf,
  writeSetupTicks,
  type SetupStep,
} from '@/lib/factorySetup'
import {
  runnerMapCommand,
  runnerPlatformGuess,
  runnerRootCommand,
  runnerServiceSteps,
  runnerStatus,
  runnerStatusLabel,
  type RunnerPlatform,
} from '@/lib/runners'
import { factoryPath, orgSettingsPath, projectItemsPath, projectSettingsPath } from '@/router/paths'
import { useSessionStore } from '@/stores/session'

/**
 * Everything a machine and a project need before an agent can finish a run, on one page that
 * checks itself. It follows one runner and one project (both in the URL, so a registration or
 * a new project can link straight here) and polls while it is open: a step turns green when
 * the runner reports it or the API shows it, not when someone clicks. The few steps no server
 * can see - a git sign-in on the machine - take a tick, and say so.
 */
const org = useOrgScope()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const session = useSessionStore()

const slug = computed(() => org.slug.value)
const isAdmin = computed(() => hasOrgRole(org.record.value?.role, 'admin'))
const userId = computed(() => session.user?.id ?? 'anonymous')

// ── what the guide follows ─────────────────────────────────────────────────────────────

const runners = ref<Runner[]>([])
const offeredHarnesses = ref<string[]>([])
const runnersLoaded = ref(false)
const projects = ref<Project[]>([])
const projectsLoaded = ref(false)

const runnerId = computed(() =>
  typeof route.query.runner === 'string' ? route.query.runner : null,
)
const projectKey = computed(() =>
  typeof route.query.project === 'string' ? route.query.project : null,
)

const runner = computed(() => runners.value.find((r) => r.id === runnerId.value) ?? null)
const project = computed(() => projects.value.find((p) => p.key === projectKey.value) ?? null)

function follow(focus: { runner?: string | null; project?: string | null }) {
  const query = { ...route.query }
  for (const [key, value] of Object.entries(focus)) {
    if (value) query[key] = value
    else delete query[key]
  }
  void router.replace({ query })
}

async function loadRunners() {
  try {
    if (isAdmin.value) {
      runners.value = await listRunners(slug.value)
      offeredHarnesses.value = [
        ...new Set(
          runners.value
            .filter((r) => !r.isDisabled)
            .flatMap((r) => r.capabilities?.harnesses.map((h) => h.name) ?? []),
        ),
      ]
    } else {
      // Not an Admin: only what picking a runner at run time needs.
      runners.value = []
      offeredHarnesses.value = [
        ...new Set((await listRunnerChoices(slug.value)).flatMap((r) => r.harnesses)),
      ]
    }
  } catch {
    // The next poll tries again; a stale roster is better than a toast every five seconds.
  } finally {
    runnersLoaded.value = true
  }
  // Nothing named, or a runner that is gone: follow the most useful one.
  if (isAdmin.value && !runner.value && runners.value.length > 0) {
    const pick =
      runners.value.find((r) => runnerStatus(r) === 'never') ??
      runners.value.find((r) => r.isOnline) ??
      runners.value[0]!
    follow({ runner: pick.id })
  }
}

async function loadProjects() {
  try {
    projects.value = (await listProjects(slug.value)).filter((p) => !p.isArchived)
  } catch (error) {
    toast.error(error)
  } finally {
    projectsLoaded.value = true
  }
  if (!project.value && projects.value.length > 0) follow({ project: projects.value[0]!.key })
}

// ── the project's facts ───────────────────────────────────────────────────────────────

const settings = ref<FactorySettings | null>(null)
const agents = ref<Agent[] | null>(null)
const members = ref<ProjectMember[] | null>(null)
const playbooks = ref<Playbook[] | null>(null)
const succeededRuns = ref<number | null>(null)
const projectLoading = ref(false)
let projectGeneration = 0

async function loadProject(quiet = false) {
  const key = projectKey.value
  if (!key || !project.value) return
  const mine = ++projectGeneration
  if (!quiet) {
    projectLoading.value = true
    settings.value = null
    agents.value = members.value = playbooks.value = null
    succeededRuns.value = null
  }
  // Each fact fails alone: one refused request must not blank the whole checklist.
  const settle = <T,>(promise: Promise<T>, apply: (value: T) => void) =>
    promise.then(
      (value) => {
        if (mine === projectGeneration) apply(value)
      },
      () => undefined,
    )
  await Promise.all([
    settle(getFactorySettings(slug.value, key), (v) => (settings.value = v)),
    settle(listAgents(slug.value), (v) => (agents.value = v)),
    settle(listProjectMembers(slug.value, key), (v) => (members.value = v)),
    settle(listPlaybooks(slug.value, key), (v) => (playbooks.value = v)),
    settle(
      listRuns(slug.value, { project: key, status: 'succeeded', pageSize: 1 }),
      (v) => (succeededRuns.value = v.totalCount),
    ),
  ])
  if (mine === projectGeneration) projectLoading.value = false
}

// ── ticks ─────────────────────────────────────────────────────────────────────────────

const machineTicks = ref<Set<string>>(new Set())
const projectTicks = ref<Set<string>>(new Set())

watch(
  [userId, slug, runnerId],
  () =>
    (machineTicks.value = readSetupTicks(
      userId.value,
      slug.value,
      `runner.${runnerId.value ?? '-'}`,
    )),
  { immediate: true },
)
watch(
  [userId, slug, projectKey, runnerId],
  () =>
    (projectTicks.value = readSetupTicks(
      userId.value,
      slug.value,
      `project.${projectKey.value ?? '-'}.${runnerId.value ?? '-'}`,
    )),
  { immediate: true },
)

function tick(which: 'machine' | 'project', id: string, value: boolean) {
  const target = which === 'machine' ? machineTicks : projectTicks
  const next = new Set(target.value)
  if (value) next.add(id)
  else next.delete(id)
  target.value = next
  writeSetupTicks(
    userId.value,
    slug.value,
    which === 'machine'
      ? `runner.${runnerId.value ?? '-'}`
      : `project.${projectKey.value ?? '-'}.${runnerId.value ?? '-'}`,
    next,
  )
}

// ── the steps ─────────────────────────────────────────────────────────────────────────

const machine = computed(() => machineSteps(runner.value, machineTicks.value))
const projectChecklist = computed(() =>
  projectKey.value
    ? projectSteps(
        {
          projectKey: projectKey.value,
          settings: settings.value,
          agents: agents.value,
          members: members.value,
          playbooks: playbooks.value,
          succeededRuns: succeededRuns.value,
          runner: runner.value,
          offeredHarnesses: offeredHarnesses.value,
        },
        projectTicks.value,
      )
    : [],
)

const machineTitles: Record<string, string> = {
  install: 'Prepare the machine',
  harness: 'Sign in to a coding harness',
  register: 'Register the runner',
  start: 'Start it',
  service: 'Keep it running as a service',
}
const projectTitles: Record<string, string> = {
  agent: 'An agent with access to the project',
  repository: 'Tell Aictiq where the code lives',
  checkout: 'Let the runner find the checkout',
  push: 'Let the machine push',
  connection: 'Aictiq MCP and tokens',
  playbook: 'A playbook the runner can run',
  'first-run': 'Hand over the first item',
}

const doneCount = (steps: SetupStep[]) => steps.filter((s) => isSetupStepDone(s.state)).length
const total = computed(
  () => (isAdmin.value ? machine.value.length : 0) + projectChecklist.value.length,
)
const done = computed(
  () => (isAdmin.value ? doneCount(machine.value) : 0) + doneCount(projectChecklist.value),
)
const allDone = computed(() => total.value > 0 && done.value === total.value)
const ring = computed(() => {
  const circumference = 2 * Math.PI * 22
  return {
    circumference,
    offset: circumference * (1 - (total.value ? done.value / total.value : 0)),
  }
})

/** Open the first step that still needs someone; a person's own choice wins afterwards. */
const opened = ref<Record<string, boolean>>({})
const firstOpen = (steps: SetupStep[]) => steps.find((s) => !isSetupStepDone(s.state))?.id ?? null
const isOpen = (track: string, steps: SetupStep[], id: string) =>
  opened.value[`${track}.${id}`] ?? firstOpen(steps) === id
function toggle(track: string, steps: SetupStep[], id: string) {
  opened.value = { ...opened.value, [`${track}.${id}`]: !isOpen(track, steps, id) }
}

// ── actions and copy ──────────────────────────────────────────────────────────────────

const origin = typeof window === 'undefined' ? '' : window.location.origin
const registerCommand = computed(() => `aictiq runner register --url ${origin} --token jrn_…`)
const hint = computed(() => settings.value?.localPathHint ?? '')

const harnessTab = ref<'claude' | 'codex' | 'opencode' | 'cursor'>('claude')
const harnessGuides = {
  claude: {
    label: 'Claude Code',
    commands:
      'npm install -g @anthropic-ai/claude-code\nclaude          # sign in, then /exit\nclaude --version',
    docs: 'https://docs.anthropic.com/en/docs/claude-code/getting-started',
  },
  codex: {
    label: 'Codex',
    commands:
      'npm install -g @openai/codex\ncodex login     # --device-auth on a machine without a browser\ncodex --version',
    docs: 'https://developers.openai.com/codex/cli',
  },
  opencode: {
    label: 'OpenCode',
    commands: 'npm install -g opencode-ai\nopencode auth login\nopencode --version',
    docs: 'https://opencode.ai/docs',
  },
  cursor: {
    label: 'Cursor',
    commands:
      'curl https://cursor.com/install -fsS | bash\nagent login     # or set CURSOR_API_KEY for headless runs\nagent --version',
    docs: 'https://cursor.com/docs/cli/headless',
  },
} as const

const chosenPlatform = ref<RunnerPlatform | null>(null)
const platform = computed<RunnerPlatform>(
  () =>
    chosenPlatform.value ??
    runnerPlatformOf(runner.value) ??
    runnerPlatformGuess(typeof navigator === 'undefined' ? '' : navigator.userAgent),
)
const service = computed(() => runnerServiceSteps[platform.value])

const mayManageProject = computed(() => project.value?.role === 'admin')
const creatingStarter = ref(false)
async function createStarter() {
  if (!projectKey.value) return
  creatingStarter.value = true
  try {
    const created = await createStarterPlaybook(slug.value, projectKey.value)
    playbooks.value = [...(playbooks.value ?? []), created]
    toast.success(`${created.name} playbook created.`, 'Tune its instructions under Playbooks.')
  } catch (error) {
    toast.error(error)
  } finally {
    creatingStarter.value = false
  }
}

// ── polling ───────────────────────────────────────────────────────────────────────────

/**
 * Five seconds for the roster: the moment a runner says hello is the moment this page exists
 * for. The project's facts change when someone acts elsewhere, so they refresh less often
 * and whenever the tab comes back into view.
 */
let ticker: ReturnType<typeof setInterval> | undefined
let beats = 0
const refreshing = ref(false)

async function refresh() {
  refreshing.value = true
  await Promise.all([loadRunners(), loadProject(true)])
  refreshing.value = false
}

function onVisible() {
  if (document.visibilityState === 'visible') void refresh()
}

onMounted(() => {
  ticker = setInterval(() => {
    if (document.visibilityState !== 'visible') return
    beats++
    void loadRunners()
    if (beats % 3 === 0) void loadProject(true)
  }, 5_000)
  document.addEventListener('visibilitychange', onVisible)
})
onBeforeUnmount(() => {
  clearInterval(ticker)
  document.removeEventListener('visibilitychange', onVisible)
})

watch(
  [slug, isAdmin],
  () => {
    runnersLoaded.value = projectsLoaded.value = false
    void loadRunners()
    void loadProjects()
  },
  { immediate: true },
)
watch([projectKey, () => project.value?.id], () => void loadProject())

const statusDot: Record<ReturnType<typeof runnerStatus>, string> = {
  online: 'bg-success',
  offline: 'bg-muted-foreground/50',
  never: 'border-muted-foreground/60 border bg-transparent',
  disabled: 'bg-destructive/70',
}
</script>

<template>
  <SettingsSection wide>
    <header class="flex flex-wrap items-start justify-between gap-4 pb-4">
      <div class="min-w-0 max-w-xl">
        <h2 class="text-sm font-medium">Setup guide</h2>
        <p class="text-muted-foreground mt-0.5 text-xs">
          Everything a runner machine and a project need before an agent can finish a run. Steps
          tick themselves as Aictiq sees them done - leave this open while you work on the machine.
        </p>
        <div class="mt-2 flex flex-wrap gap-2">
          <FactoryDocsLink />
          <Button variant="ghost" size="sm" :disabled="refreshing" @click="refresh">
            <RefreshCw
              class="size-3.5"
              :class="{ 'animate-spin': refreshing }"
              aria-hidden="true"
            />
            Check again
          </Button>
        </div>
      </div>

      <div
        v-if="total > 0"
        class="flex items-center gap-3"
        data-testid="setup-progress"
        role="status"
        :aria-label="`${done} of ${total} steps done`"
      >
        <svg viewBox="0 0 56 56" class="size-14 -rotate-90" aria-hidden="true">
          <circle cx="28" cy="28" r="22" fill="none" class="stroke-muted" stroke-width="6" />
          <circle
            cx="28"
            cy="28"
            r="22"
            fill="none"
            class="stroke-success transition-[stroke-dashoffset] duration-700 ease-out"
            stroke-width="6"
            stroke-linecap="round"
            :stroke-dasharray="ring.circumference"
            :stroke-dashoffset="ring.offset"
          />
        </svg>
        <div>
          <p class="text-lg leading-none font-medium tabular-nums">{{ done }}/{{ total }}</p>
          <p class="text-muted-foreground text-xs">steps done</p>
        </div>
      </div>
    </header>

    <FactoryFlowDiagram />

    <div
      v-if="allDone"
      class="setup-celebrate border-success/40 bg-success/10 mt-4 flex items-center gap-3 rounded-lg border px-4 py-3"
      data-testid="setup-all-done"
    >
      <PartyPopper class="text-success size-5 flex-none" aria-hidden="true" />
      <p class="text-sm">
        <span class="font-medium">This runner and {{ project?.name }} are ready.</span>
        <span class="text-muted-foreground">
          Add rules under Rules once manual runs are predictable.
        </span>
      </p>
    </div>

    <div class="mt-4 grid gap-4 lg:grid-cols-2">
      <!-- The machine -->
      <section class="border-border rounded-xl border" aria-labelledby="setup-machine">
        <header class="border-border flex flex-wrap items-center gap-2 border-b px-4 py-3">
          <Server class="text-primary size-4" aria-hidden="true" />
          <h3 id="setup-machine" class="text-sm font-medium">The runner machine</h3>
          <span v-if="isAdmin" class="text-muted-foreground text-xs tabular-nums">
            {{ doneCount(machine) }}/{{ machine.length }}
          </span>
          <div v-if="isAdmin && runners.length > 0" class="ml-auto flex items-center gap-2">
            <span
              v-if="runner"
              class="size-2 rounded-full"
              :class="statusDot[runnerStatus(runner)]"
              aria-hidden="true"
            />
            <select
              aria-label="Runner to follow"
              data-testid="setup-runner"
              class="border-input bg-background h-7 rounded-md border px-1.5 text-xs"
              :value="runnerId ?? ''"
              @change="follow({ runner: ($event.target as HTMLSelectElement).value })"
            >
              <option v-for="r in runners" :key="r.id" :value="r.id">
                {{ r.name }} · {{ runnerStatusLabel[runnerStatus(r)] }}
              </option>
            </select>
          </div>
        </header>

        <p v-if="!isAdmin" class="text-muted-foreground px-4 py-6 text-xs">
          Runners are registered by organization Owners and Admins, because a runner is handed agent
          credentials for every run it takes. Ask one to set up a machine; the project steps on this
          page are yours.
        </p>
        <div v-else-if="!runnersLoaded" class="flex justify-center py-8">
          <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
        </div>
        <ol v-else class="px-4" data-testid="setup-machine-steps">
          <SetupStepRow
            v-for="(step, index) in machine"
            :key="step.id"
            :step="step"
            :index="index"
            :title="machineTitles[step.id]!"
            :open="isOpen('machine', machine, step.id)"
            @toggle="toggle('machine', machine, step.id)"
            @tick="(value) => tick('machine', step.id, value)"
          >
            <template v-if="step.id === 'install'">
              <p>
                A VPS, a spare laptop or a CI box with Node.js 24 or newer, Git and the GitHub CLI.
                Use an unprivileged account dedicated to the runner: the agent can read everything
                that account can. Then install Aictiq's CLI:
              </p>
              <CommandBlock
                label="install the CLI"
                :command="'npm install -g @aictiq/cli\naictiq --version'"
              />
            </template>

            <template v-else-if="step.id === 'harness'">
              <p>
                At least one harness, signed in as the runner's account - that account pays for and
                owns its runs. The runner detects them on its PATH and tells Aictiq which it has.
              </p>
              <div class="flex flex-wrap gap-1" role="group" aria-label="Harness">
                <Button
                  v-for="(guide, key) in harnessGuides"
                  :key="key"
                  size="sm"
                  class="h-6 px-2 text-[11px]"
                  :variant="harnessTab === key ? 'secondary' : 'ghost'"
                  :aria-pressed="harnessTab === key"
                  @click="harnessTab = key"
                >
                  {{ guide.label }}
                </Button>
              </div>
              <CommandBlock
                :label="`${harnessGuides[harnessTab].label} setup`"
                :command="harnessGuides[harnessTab].commands"
              />
              <a
                :href="harnessGuides[harnessTab].docs"
                target="_blank"
                rel="noopener noreferrer"
                class="text-primary inline-flex items-center gap-1 hover:underline"
              >
                {{ harnessGuides[harnessTab].label }} installation guide
                <ExternalLink class="size-3" aria-hidden="true" />
              </a>
            </template>

            <template v-else-if="step.id === 'register'">
              <template v-if="!runner">
                <p>
                  Name the runner after the machine. Aictiq shows its secret once, with the exact
                  command to paste; this page then follows it.
                </p>
                <Button
                  size="sm"
                  @click="
                    router.push({ path: factoryPath(slug, 'runners'), query: { register: '1' } })
                  "
                >
                  Register a runner
                </Button>
              </template>
              <template v-else>
                <p>Paste the command from the registration dialog on the machine. It looks like:</p>
                <CommandBlock label="register command" :command="registerCommand" />
                <p>
                  The secret was shown once. Lost it? Under
                  <RouterLink
                    class="text-primary hover:underline"
                    :to="factoryPath(slug, 'runners')"
                    >Runners</RouterLink
                  >, choose <span class="text-foreground">New secret</span> for {{ runner.name }}.
                </p>
              </template>
            </template>

            <template v-else-if="step.id === 'start'">
              <p>
                Start it in a terminal first and watch it connect. It turns green here within
                seconds.
              </p>
              <CommandBlock label="start command" :command="'aictiq runner start'" />
              <p>
                In another terminal, <code class="font-mono">aictiq runner status</code> shows every
                registration, harness and repository.
              </p>
            </template>

            <template v-else-if="step.id === 'service'">
              <p>
                Stop the terminal runner, then install it as a service so it survives logouts and
                reboots:
              </p>
              <div class="flex flex-wrap gap-1" role="group" aria-label="Runner platform">
                <Button
                  v-for="(steps, key) in runnerServiceSteps"
                  :key="key"
                  size="sm"
                  class="h-6 px-2 text-[11px]"
                  :variant="platform === key ? 'secondary' : 'ghost'"
                  :aria-pressed="platform === key"
                  @click="chosenPlatform = key"
                >
                  {{ steps.label }}
                </Button>
              </div>
              <CommandBlock
                :label="`${service.label} service commands`"
                :command="service.commands"
              />
              <p>{{ service.note }}</p>
            </template>
          </SetupStepRow>
        </ol>
      </section>

      <!-- The project -->
      <section class="border-border rounded-xl border" aria-labelledby="setup-project">
        <header class="border-border flex flex-wrap items-center gap-2 border-b px-4 py-3">
          <FolderGit2 class="text-primary size-4" aria-hidden="true" />
          <h3 id="setup-project" class="text-sm font-medium">The project</h3>
          <span v-if="projectChecklist.length" class="text-muted-foreground text-xs tabular-nums">
            {{ doneCount(projectChecklist) }}/{{ projectChecklist.length }}
          </span>
          <select
            v-if="projects.length > 0"
            aria-label="Project to set up"
            data-testid="setup-project"
            class="border-input bg-background ml-auto h-7 rounded-md border px-1.5 text-xs"
            :value="projectKey ?? ''"
            @change="follow({ project: ($event.target as HTMLSelectElement).value })"
          >
            <option v-for="p in projects" :key="p.key" :value="p.key">
              {{ p.name }} ({{ p.key }})
            </option>
          </select>
        </header>

        <div v-if="!projectsLoaded || (projectLoading && project)" class="flex justify-center py-8">
          <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
        </div>
        <p v-else-if="projects.length === 0" class="text-muted-foreground px-4 py-6 text-xs">
          No project yet. Create one from Projects, then come back here to connect it.
        </p>
        <ol v-else class="px-4" data-testid="setup-project-steps">
          <SetupStepRow
            v-for="(step, index) in projectChecklist"
            :key="step.id"
            :step="step"
            :index="index"
            :title="projectTitles[step.id]!"
            :open="isOpen('project', projectChecklist, step.id)"
            @toggle="toggle('project', projectChecklist, step.id)"
            @tick="(value) => tick('project', step.id, value)"
          >
            <template v-if="step.id === 'agent'">
              <p>
                An agent is the bot identity that claims the item, comments, commits and opens the
                pull request. A person owns it, and it must be a member of this project.
              </p>
              <Button
                v-if="isAdmin"
                size="sm"
                variant="outline"
                @click="router.push(orgSettingsPath(slug, 'agents'))"
              >
                Open agents
              </Button>
              <p v-else>Ask an organization Admin to create one and add it to the project.</p>
            </template>

            <template v-else-if="step.id === 'repository' && projectKey">
              <p>
                <span class="text-foreground">GitHub binding</span>: each run clones a connected
                repository with a short-lived credential.
                <span class="text-foreground">Runner-local checkout</span>: the runner uses a clone
                already on the machine.
              </p>
              <Button
                size="sm"
                variant="outline"
                @click="router.push(projectSettingsPath(slug, projectKey, 'factory'))"
              >
                Open project factory settings
              </Button>
            </template>

            <template v-else-if="step.id === 'checkout' && projectKey">
              <template v-if="settings?.repoSource === 1">
                <p>Clone the repository on the machine, as the runner's account:</p>
                <CommandBlock
                  label="clone command"
                  :command="`git clone git@github.com:YOUR-ORG/YOUR-REPOSITORY.git ${hint || '~/src/YOUR-REPOSITORY'}`"
                />
                <p>
                  Then trust path hints under the folder that holds your clones - new projects there
                  need no runner change:
                </p>
                <CommandBlock
                  label="repository root command"
                  :command="runnerRootCommand(hint, slug)"
                />
                <p>Or map just this project to its clone:</p>
                <CommandBlock
                  label="project mapping command"
                  :command="runnerMapCommand(projectKey, hint, slug)"
                />
                <p>
                  The runner re-reads its configuration for every run and reports it here on its
                  next heartbeat.
                </p>
              </template>
              <p v-else>A bound repository needs nothing on the machine.</p>
            </template>

            <template v-else-if="step.id === 'push'">
              <template v-if="settings?.repoSource === 1">
                <p>
                  The agent pushes a branch and opens a pull request with the machine's git and
                  <code class="font-mono">gh</code> sign-in. Sign in once as the runner's account:
                </p>
                <CommandBlock
                  label="GitHub CLI sign-in"
                  :command="'gh auth login\ngh auth status'"
                />
                <p>Aictiq confirms this step itself after the first successful run.</p>
              </template>
              <p v-else>
                A bound repository gets its push credential from the GitHub App, per run.
              </p>
            </template>

            <template v-else-if="step.id === 'connection'">
              <p>
                Nothing to configure on the runner. For every run it starts the
                <code class="font-mono">aictiq mcp</code> server for the harness and hands it a
                token that belongs to the run's agent and stops working when the run ends. No
                personal access token is pasted anywhere, and the runner's own secret never reaches
                the agent.
              </p>
            </template>

            <template v-else-if="step.id === 'playbook'">
              <p>
                A playbook is the reusable prompt for this project: definition of done, test
                commands, when to stop. Its harness must be one the runner offers.
              </p>
              <div class="flex flex-wrap gap-2">
                <Button
                  v-if="mayManageProject && playbooks?.length === 0"
                  size="sm"
                  :disabled="creatingStarter"
                  data-testid="setup-create-starter"
                  @click="createStarter"
                >
                  <Loader2 v-if="creatingStarter" class="animate-spin" aria-hidden="true" />
                  Create the starter playbook
                </Button>
                <Button
                  size="sm"
                  variant="outline"
                  @click="router.push(factoryPath(slug, 'playbooks'))"
                >
                  Open playbooks
                </Button>
              </div>
            </template>

            <template v-else-if="step.id === 'first-run' && projectKey">
              <p>
                Open an unclaimed item and choose
                <span class="text-foreground">Hand to agent</span>. Watch it under Runs; the
                outcome, and the pull request, land on the item.
              </p>
              <div class="flex flex-wrap gap-2">
                <Button size="sm" @click="router.push(projectItemsPath(slug, projectKey))"
                  >Choose an item</Button
                >
                <Button size="sm" variant="outline" @click="router.push(factoryPath(slug, 'runs'))"
                  >Open runs</Button
                >
              </div>
            </template>
          </SetupStepRow>
        </ol>
      </section>
    </div>
  </SettingsSection>
</template>

<style scoped>
.setup-celebrate {
  animation: setup-rise 0.6s cubic-bezier(0.2, 0.9, 0.3, 1.3);
}

@keyframes setup-rise {
  from {
    transform: translateY(8px) scale(0.98);
    opacity: 0;
  }
}
</style>
