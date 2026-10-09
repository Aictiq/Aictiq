<script setup lang="ts">
import { Check, Copy, Loader2, MoreHorizontal } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { hasOrgRole } from '@/api/organizations'
import {
  deleteRunner,
  getRunnerLoad,
  listRunnerMachinesElsewhere,
  listRunners,
  registerRunner,
  registerRunnerOnMachine,
  rotateRunner,
  updateRunner,
  type Runner,
  type RunnerIssued,
  type RunnerLoad as RunnerLoadData,
  type RunnerMachine,
} from '@/api/runners'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import RunnerLoad from '@/components/factory/RunnerLoad.vue'
import InlineEdit from '@/components/common/InlineEdit.vue'
import PlanLimitNotice from '@/components/common/PlanLimitNotice.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { planLimitRefusal, type PlanLimitRefusal } from '@/lib/billing'
import { since } from '@/lib/claims'
import {
  claudeUsagePollCommand,
  claudeUsagePollDocsUrl,
  claudeUsagePollHint,
  loadFor,
  runnerPlatformGuess,
  runnerRegisterCommand,
  runnerServiceSteps,
  runnerStatus,
  runnerStatusLabel,
  usageLimitsFor,
  usagePercent,
  usageResetLabel,
  usageWindowLabel,
  usageWindowStale,
  waitingSummary,
  type RunnerPlatform,
} from '@/lib/runners'
import { factorySetupPath } from '@/router/paths'
import { ApiError } from '@/utils/api'

/**
 * The machines that run an organization's agents. Managing them is an Admin's job - a runner
 * will be handed agent credentials for every run it takes - so everyone else sees why the
 * tab is empty rather than a roster that answers 403.
 *
 * Two things this screen keeps saying out loud: a secret is shown exactly once, and a runner
 * is only as alive as its last heartbeat.
 */
const org = useOrgScope()
const toast = useToast()
const route = useRoute()
const router = useRouter()

const slug = computed(() => org.slug.value)
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))

const runners = ref<Runner[]>([])
/** What the runners hold and what waits for them; null until read, or when it could not be. */
const runnerLoad = ref<RunnerLoadData | null>(null)

/** Runs for any runner, once for the organization rather than on every runner. */
const orgQueue = computed(() => {
  if (!runnerLoad.value) return null
  const { unassignedQueued, unassignedScheduled, nextUnassignedScheduledFor } = runnerLoad.value
  return (
    waitingSummary(unassignedQueued, unassignedScheduled, nextUnassignedScheduledFor).replace(
      /(\d+) queued/,
      '$1 waiting',
    ) || 'nothing waiting'
  )
})

const usageKinds = ['fiveHour', 'weekly'] as const
const usageWindowName = { fiveHour: '5-hour', weekly: 'Weekly' } as const

/**
 * A row per offered harness whose account reported usage, and the rest named on one line, so a
 * runner whose harnesses report nothing adds a single line rather than one per harness.
 */
function usageRows(runner: Runner) {
  const rows = (runner.capabilities?.harnesses ?? []).map((harness) => ({
    harness: harness.name,
    limits: usageLimitsFor(runner.capabilities?.usageLimits, harness.name),
  }))
  return {
    reported: rows.flatMap((row) =>
      row.limits ? [{ harness: row.harness, limits: row.limits }] : [],
    ),
    unavailable: rows.filter((row) => !row.limits).map((row) => row.harness),
  }
}

/** Amber close to the limit, red at it; a stale figure is greyed out whatever it says. */
function usageBarClass(percent: number, stale: boolean): string {
  if (stale) return 'bg-muted-foreground/40'
  if (percent >= 95) return 'bg-red-500'
  if (percent >= 80) return 'bg-amber-500'
  return 'bg-primary'
}
const loading = ref(true)
const busyId = ref<string | null>(null)

// The setup guide sends "Register a runner" here with ?register=1.
const registering = ref(route?.query.register === '1')
const name = ref('')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})
/** A free organization may register only so many runners; the refusal stays in the dialog. */
const refused = ref<PlanLimitRefusal | null>(null)

/** "Use a runner I already have": the caller's machines in their other organizations. */
const usingExisting = ref(false)
const machines = ref<RunnerMachine[]>([])
const machinesLoading = ref(false)
const selectedMachine = ref<RunnerMachine | null>(null)
const machineName = ref('')
const connecting = ref(false)
const connectErrors = ref<Record<string, string[]>>({})

/** The secret just minted, and which runner it belongs to. Cleared when the dialog closes. */
const issued = ref<RunnerIssued | null>(null)
/** Minted for a machine that already runs for another organization: it gets a new profile. */
const issuedForExisting = ref(false)
const issuedOpen = ref(false)
const copied = ref<'secret' | 'command' | 'service' | null>(null)

/**
 * Starting the runner by hand ends with the terminal. The service definition is the part
 * that survives a logout and a reboot, so it belongs beside `runner start` rather than in
 * the README alone. The tab opens on a guess about the browser's machine; the runner often
 * lives elsewhere, so all three stay one click away.
 */
const platform = ref<RunnerPlatform>(
  runnerPlatformGuess(typeof navigator === 'undefined' ? '' : navigator.userAgent),
)
const service = computed(() => runnerServiceSteps[platform.value])

const confirmingDelete = ref<Runner | null>(null)

const origin = typeof window === 'undefined' ? '' : window.location.origin
const command = computed(() =>
  issued.value ? runnerRegisterCommand(origin, issued.value.secret) : '',
)

async function load(quiet = false) {
  if (!mayManage.value) {
    loading.value = false
    return
  }
  if (!quiet) loading.value = true
  try {
    // The load is a nicety on top of the roster: if it cannot be read, the roster still shows.
    const [roster, loaded] = await Promise.all([
      listRunners(slug.value),
      getRunnerLoad(slug.value).catch(() => null),
    ])
    runners.value = roster
    runnerLoad.value = loaded
  } catch (error) {
    if (!quiet) toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([slug, mayManage], () => load(), { immediate: true })

// "Online" is a judgement about time, so the roster refreshes itself quietly: a machine that
// stops heartbeating should turn grey without anyone reloading the page. The load comes with it.
let refresh: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  refresh = setInterval(() => void load(true), 30_000)
})
onBeforeUnmount(() => clearInterval(refresh))

function replace(updated: Runner) {
  runners.value = runners.value.map((r) => (r.id === updated.id ? updated : r))
}

function showSecret(value: RunnerIssued, forExisting = false) {
  issued.value = value
  issuedForExisting.value = forExisting
  copied.value = null
  issuedOpen.value = true
}

// A refusal belongs to the attempt it answered: opening either dialog again starts clean.
watch([registering, usingExisting], () => {
  refused.value = null
})

watch(issuedOpen, (open) => {
  // The secret leaves memory with the dialog: there is no second look.
  if (!open) issued.value = null
})

/**
 * "I have copied it" leads to the setup guide, following this runner: the rest of the setup
 * happens on the machine, and the guide ticks each step as the runner reports it.
 */
function continueSetup() {
  const runnerId = issued.value?.runner.id
  issuedOpen.value = false
  if (runnerId) void router.push(factorySetupPath(slug.value, { runner: runnerId }))
}

async function submit() {
  submitting.value = true
  fieldErrors.value = {}
  refused.value = null
  try {
    const created = await registerRunner(slug.value, name.value.trim())
    runners.value = [...runners.value, created.runner].sort((a, b) => a.name.localeCompare(b.name))
    registering.value = false
    name.value = ''
    showSecret(created)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else if (error instanceof ApiError && error.status === 409) {
      fieldErrors.value = { name: ['Another runner in this organization already has that name.'] }
    } else if (planLimitRefusal(error)) {
      refused.value = planLimitRefusal(error)
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

async function openExisting() {
  usingExisting.value = true
  selectedMachine.value = null
  machineName.value = ''
  connectErrors.value = {}
  machinesLoading.value = true
  try {
    machines.value = await listRunnerMachinesElsewhere(slug.value)
  } catch (error) {
    machines.value = []
    toast.error(error)
  } finally {
    machinesLoading.value = false
  }
}

function selectMachine(machine: RunnerMachine) {
  if (machine.isConnectedHere) return
  selectedMachine.value = machine
  machineName.value = machine.name
  connectErrors.value = {}
}

function registerInstead() {
  usingExisting.value = false
  registering.value = true
}

async function connect() {
  const machine = selectedMachine.value
  if (!machine) return
  connecting.value = true
  connectErrors.value = {}
  refused.value = null
  try {
    const name = machineName.value.trim()
    const created = await registerRunnerOnMachine(
      slug.value,
      machine.runnerId,
      name && name !== machine.name ? name : undefined,
    )
    runners.value = [...runners.value, created.runner].sort((a, b) => a.name.localeCompare(b.name))
    usingExisting.value = false
    showSecret(created, true)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      connectErrors.value = error.fieldErrors
    } else if (error instanceof ApiError && error.status === 409) {
      connectErrors.value = {
        name: ['Another runner in this organization already has that name. Pick another.'],
      }
    } else if (planLimitRefusal(error)) {
      refused.value = planLimitRefusal(error)
    } else {
      toast.error(error)
    }
  } finally {
    connecting.value = false
  }
}

function machineStatus(machine: RunnerMachine) {
  return runnerStatus({
    isDisabled: false,
    isOnline: machine.isOnline,
    lastSeenAt: machine.lastSeenAt,
  })
}

async function rename(runner: Runner, value: string) {
  busyId.value = runner.id
  try {
    replace(await updateRunner(slug.value, runner.id, { name: value }))
  } catch (error) {
    toast.error(error)
    await load(true)
  } finally {
    busyId.value = null
  }
}

async function setDisabled(runner: Runner, disabled: boolean) {
  busyId.value = runner.id
  try {
    replace(await updateRunner(slug.value, runner.id, { disabled }))
    toast.success(
      disabled
        ? `${runner.name} is disabled. Its secret stops working immediately.`
        : `${runner.name} is enabled again.`,
    )
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

async function rotate(runner: Runner) {
  busyId.value = runner.id
  try {
    const rotated = await rotateRunner(slug.value, runner.id)
    replace(rotated.runner)
    showSecret(rotated)
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

async function remove() {
  const runner = confirmingDelete.value
  if (!runner) return
  busyId.value = runner.id
  try {
    await deleteRunner(slug.value, runner.id)
    runners.value = runners.value.filter((r) => r.id !== runner.id)
    toast.success(`${runner.name} was deleted. Its secret no longer works.`)
    confirmingDelete.value = null
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

const copyable: Record<'secret' | 'command' | 'service', () => string> = {
  secret: () => issued.value?.secret ?? '',
  command: () => command.value,
  service: () => service.value.commands,
}

async function copy(what: 'secret' | 'command' | 'service') {
  if (!issued.value) return
  try {
    await navigator.clipboard.writeText(copyable[what]())
    copied.value = what
  } catch {
    toast.error(new Error('Could not copy - select the text and copy it manually.'))
  }
}

const statusDot: Record<ReturnType<typeof runnerStatus>, string> = {
  online: 'bg-success',
  offline: 'bg-muted-foreground/50',
  never: 'border-muted-foreground/60 border bg-transparent',
  disabled: 'bg-destructive/70',
}
</script>

<template>
  <SettingsSection wide>
    <header class="flex flex-wrap items-center justify-between gap-3 pb-3">
      <div class="min-w-0">
        <h2 class="text-sm font-medium">Runners</h2>
        <p class="text-muted-foreground mt-0.5 text-xs">
          A runner is <code class="font-mono">aictiq runner</code> on a machine you prepare - a VPS,
          a laptop, a CI box - with Claude Code, Codex, OpenCode, Cursor or GitHub Copilot already
          signed in. It picks up runs and reports back. Its secret is shown once, when it is
          registered.
        </p>
      </div>
      <div v-if="mayManage" class="flex flex-wrap gap-2">
        <Button size="sm" variant="ghost" @click="router.push(factorySetupPath(slug))">
          Setup guide
        </Button>
        <Button size="sm" variant="outline" @click="openExisting">Use existing runner</Button>
        <Button size="sm" @click="registering = true">Register runner</Button>
      </div>
    </header>

    <p
      v-if="orgQueue && !loading && runners.length"
      class="text-muted-foreground pb-2 text-[11px]"
      data-testid="runner-org-queue"
    >
      <span class="text-foreground font-medium">Org queue</span> (runs for any runner):
      {{ orgQueue }}
    </p>

    <EmptyState
      v-if="!mayManage"
      title="Runners are managed by Admins"
      description="A runner is handed agent credentials for every run it takes, so only organization Owners and Admins can register or see them."
      icon="◇"
    >
      <FactoryDocsLink />
    </EmptyState>

    <UiPageState v-else-if="loading" state="loading" />

    <EmptyState
      v-else-if="runners.length === 0"
      title="No runners yet"
      description="Register one, then run the command it gives you on the machine. It turns green here when it says hello. A machine that already runs for another of your organizations can run for this one too."
      icon="◇"
    >
      <div class="flex flex-wrap justify-center gap-2">
        <Button @click="registering = true">Register new runner</Button>
        <Button variant="outline" @click="openExisting">Use a runner I already have</Button>
        <FactoryDocsLink />
      </div>
    </EmptyState>

    <ul v-else class="border-border divide-border divide-y rounded-lg border">
      <li v-for="runner in runners" :key="runner.id" class="px-3 py-2.5">
        <div class="flex items-center gap-2.5">
          <span
            class="size-2 flex-none rounded-full"
            :class="statusDot[runnerStatus(runner)]"
            aria-hidden="true"
          />
          <div class="min-w-0 flex-1">
            <InlineEdit
              :model-value="runner.name"
              :label="`Name of ${runner.name}`"
              class="min-w-0"
              @commit="(value: string) => rename(runner, value)"
            />
            <div class="text-muted-foreground truncate text-[11px]">
              <span>{{ runnerStatusLabel[runnerStatus(runner)] }}</span>
              <template v-if="runner.lastSeenAt"> · seen {{ since(runner.lastSeenAt) }}</template>
              · <span class="font-mono">{{ runner.tokenDisplay }}</span>
              <template v-if="runner.registeredByName">
                · registered by {{ runner.registeredByName }}</template
              >
              <template v-if="runner.capabilities?.os">
                · {{ runner.capabilities.os }}/{{ runner.capabilities.arch }}</template
              >
            </div>
            <div
              v-if="runner.capabilities && runner.capabilities.harnesses.length"
              class="mt-1 flex flex-wrap gap-1"
            >
              <Badge
                v-for="harness in runner.capabilities.harnesses"
                :key="harness.name"
                variant="secondary"
                class="font-mono text-[10px]"
                :title="harness.version ?? undefined"
              >
                {{ harness.name }}
              </Badge>
            </div>
            <RunnerLoad
              class="mt-1.5"
              :slug="slug"
              :runner-name="runner.name"
              :slots="runner.capabilities?.maxParallel ?? 1"
              :entry="loadFor(runnerLoad, runner.id)"
              :muted="runnerStatus(runner) !== 'online'"
            />
            <ul
              v-if="runner.capabilities && runner.capabilities.harnesses.length"
              class="mt-1 space-y-1 text-[11px]"
              :aria-label="`Harness usage on ${runner.name}`"
            >
              <li
                v-for="{ harness, limits } in usageRows(runner).reported"
                :key="harness"
                class="flex flex-wrap items-center gap-x-3 gap-y-0.5"
                :data-testid="`runner-usage-${harness}`"
              >
                <span class="text-muted-foreground w-14 flex-none font-mono">{{ harness }}</span>
                <template v-for="kind in usageKinds" :key="kind">
                  <span v-if="limits[kind]" class="flex items-center gap-1.5">
                    <span class="text-muted-foreground">{{ usageWindowLabel[kind] }}</span>
                    <span
                      class="bg-muted h-1.5 w-16 overflow-hidden rounded-full"
                      role="meter"
                      :aria-label="`${usageWindowName[kind]} usage`"
                      aria-valuemin="0"
                      aria-valuemax="100"
                      :aria-valuenow="Math.round(limits[kind]!.usedPercent)"
                    >
                      <span
                        class="block h-full rounded-full"
                        :class="
                          usageBarClass(limits[kind]!.usedPercent, usageWindowStale(limits, kind))
                        "
                        :style="{ width: `${Math.min(100, limits[kind]!.usedPercent)}%` }"
                      />
                    </span>
                    <span
                      class="tabular-nums"
                      :class="usageWindowStale(limits, kind) ? 'text-muted-foreground' : ''"
                    >
                      {{ usagePercent(limits[kind]!.usedPercent) }}
                    </span>
                    <span v-if="usageWindowStale(limits, kind)" class="text-muted-foreground italic"
                      >stale</span
                    >
                  </span>
                </template>
                <span
                  v-if="limits.weekly?.resetsAt && !usageWindowStale(limits, 'weekly')"
                  class="text-muted-foreground"
                >
                  weekly resets {{ usageResetLabel(limits.weekly.resetsAt, true) }}
                </span>
                <span
                  class="text-muted-foreground"
                  :title="new Date(limits.observedAt).toLocaleString()"
                >
                  as of {{ since(limits.observedAt) }}
                </span>
              </li>
              <li
                v-if="usageRows(runner).unavailable.length"
                class="text-muted-foreground/70"
                data-testid="runner-usage-unavailable"
              >
                Usage not available<template v-if="usageRows(runner).reported.length"
                  >: {{ usageRows(runner).unavailable.join(', ') }}</template
                >
              </li>
              <li
                v-if="claudeUsagePollHint(runner.capabilities)"
                class="text-muted-foreground/70"
                data-testid="runner-usage-claude-hint"
              >
                Claude usage is updated after runs only. Live usage: run
                <code class="font-mono">{{ claudeUsagePollCommand }}</code> on this machine.
                <a
                  :href="claudeUsagePollDocsUrl"
                  target="_blank"
                  rel="noopener"
                  class="underline underline-offset-2"
                  title="The runner reads Claude Code's OAuth token on that machine and sends it only to api.anthropic.com. Only the percentages and reset times reach Aictiq."
                  >What it sends</a
                >
              </li>
            </ul>
            <p
              v-if="runner.capabilities?.updateFailure"
              class="mt-1 line-clamp-2 text-[11px] text-amber-700 dark:text-amber-400"
              :title="runner.capabilities.updateFailure.error"
            >
              Self-update to {{ runner.capabilities.updateFailure.version }} failed
              {{ since(runner.capabilities.updateFailure.at) }}:
              {{ runner.capabilities.updateFailure.error }}
            </p>
          </div>

          <Loader2
            v-if="busyId === runner.id"
            class="text-muted-foreground size-4 animate-spin"
            aria-hidden="true"
          />
          <DropdownMenu v-else>
            <DropdownMenuTrigger as-child>
              <Button variant="ghost" size="icon" :aria-label="`Manage ${runner.name}`">
                <MoreHorizontal class="size-4" aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" class="w-60">
              <DropdownMenuItem
                @select="router.push(factorySetupPath(slug, { runner: runner.id }))"
              >
                Setup guide
              </DropdownMenuItem>
              <DropdownMenuItem @select="rotate(runner)">New secret (re-register)</DropdownMenuItem>
              <DropdownMenuItem v-if="runner.isDisabled" @select="setDisabled(runner, false)">
                Enable
              </DropdownMenuItem>
              <DropdownMenuItem v-else @select="setDisabled(runner, true)">
                Disable
              </DropdownMenuItem>
              <DropdownMenuItem variant="destructive" @select="confirmingDelete = runner">
                Delete
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </li>
    </ul>

    <Dialog v-model:open="registering">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Register a runner</DialogTitle>
          <DialogDescription>
            Name it after the machine. You get a secret once; the runner uses it to reach this
            organization and nothing else.
          </DialogDescription>
        </DialogHeader>

        <ol
          data-testid="runner-setup-checklist"
          class="text-muted-foreground list-decimal space-y-2 pl-5 text-xs"
        >
          <li>
            <span class="text-foreground font-medium">Prepare the machine.</span>
            Install Node.js and <code class="font-mono">@aictiq/cli</code> on the machine. One
            machine can run for several of your organizations; each gets its own secret,
            repositories and roots, and their runs never execute at the same time.
          </li>
          <li>
            <span class="text-foreground font-medium">Sign in and clone.</span>
            Sign in to Claude Code, Codex, OpenCode, Cursor or GitHub Copilot as the runner user,
            then clone the repositories it will work in.
          </li>
          <li>
            <span class="text-foreground font-medium">Register and keep it running.</span>
            Create the runner here, paste its one-time command on the machine, then start it. The
            next screen also gives you the commands that install it as a service, so it survives a
            reboot.
          </li>
        </ol>

        <form id="register-runner" class="space-y-4" novalidate @submit.prevent="submit">
          <div class="space-y-1.5">
            <label for="runner-name" class="text-sm font-medium">Name</label>
            <Input
              id="runner-name"
              v-model="name"
              required
              placeholder="vps-1"
              :aria-invalid="Boolean(fieldErrors.name)"
            />
            <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>
          <PlanLimitNotice v-if="refused" :slug="slug" :refusal="refused" />
        </form>

        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="submitting" @click="registering = false">
            Cancel
          </Button>
          <Button type="submit" form="register-runner" :disabled="submitting || !name.trim()">
            <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
            Register
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog v-model:open="usingExisting">
      <DialogContent class="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Use a runner you already have</DialogTitle>
          <DialogDescription>
            Your own runners in other organizations you administer. This organization gets its own
            secret, repositories and roots on the machine, and runs from different organizations
            never execute at the same time. Every organization's agents still run as the same user
            there, so connect only organizations you trust alike.
          </DialogDescription>
        </DialogHeader>

        <UiPageState v-if="machinesLoading" state="loading" />

        <div
          v-else-if="machines.length === 0"
          data-testid="runner-machines-empty"
          class="text-muted-foreground space-y-3 text-xs"
        >
          <p>
            None of your runners can be used here. A runner is offered when you registered it
            yourself, in another organization where you are an Owner or Admin.
          </p>
          <Button size="sm" @click="registerInstead">Register a new runner</Button>
        </div>

        <div v-else class="space-y-4">
          <div
            role="radiogroup"
            aria-label="Your runners"
            class="border-border divide-border divide-y rounded-lg border"
          >
            <button
              v-for="machine in machines"
              :key="machine.runnerId"
              type="button"
              role="radio"
              :aria-checked="selectedMachine?.runnerId === machine.runnerId"
              :disabled="machine.isConnectedHere"
              data-testid="runner-machine"
              class="hover:bg-muted/50 flex w-full items-start gap-2.5 px-3 py-2.5 text-left disabled:cursor-not-allowed disabled:opacity-60"
              :class="{ 'bg-muted': selectedMachine?.runnerId === machine.runnerId }"
              @click="selectMachine(machine)"
            >
              <span
                class="mt-1.5 size-2 flex-none rounded-full"
                :class="statusDot[machineStatus(machine)]"
                aria-hidden="true"
              />
              <span class="min-w-0 flex-1">
                <span class="block truncate text-sm font-medium">{{ machine.name }}</span>
                <span class="text-muted-foreground block truncate text-[11px]">
                  {{ runnerStatusLabel[machineStatus(machine)] }}
                  <template v-if="machine.lastSeenAt">
                    · seen {{ since(machine.lastSeenAt) }}</template
                  >
                  <template v-if="machine.capabilities?.os">
                    · {{ machine.capabilities.os }}/{{ machine.capabilities.arch }}</template
                  >
                </span>
                <span class="text-muted-foreground block text-[11px]">
                  <template v-if="machine.isConnectedHere"
                    >Already runs for this organization ·
                  </template>
                  Runs for {{ machine.organizations.map((o) => o.name).join(', ') }}
                </span>
                <span
                  v-if="machine.capabilities && machine.capabilities.harnesses.length"
                  class="mt-1 flex flex-wrap gap-1"
                >
                  <Badge
                    v-for="harness in machine.capabilities.harnesses"
                    :key="harness.name"
                    variant="secondary"
                    class="font-mono text-[10px]"
                  >
                    {{ harness.name }}
                  </Badge>
                </span>
              </span>
            </button>
          </div>

          <form
            v-if="selectedMachine"
            id="connect-runner"
            class="space-y-1.5"
            novalidate
            @submit.prevent="connect"
          >
            <label for="machine-name" class="text-sm font-medium">Name in this organization</label>
            <Input
              id="machine-name"
              v-model="machineName"
              required
              :aria-invalid="Boolean(connectErrors.name)"
            />
            <p
              v-for="message in [
                ...(connectErrors.name ?? []),
                ...(connectErrors.sameMachineAs ?? []),
              ]"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
            <PlanLimitNotice v-if="refused" :slug="slug" :refusal="refused" />
          </form>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="ghost"
            :disabled="connecting"
            @click="usingExisting = false"
          >
            Cancel
          </Button>
          <Button
            v-if="machines.length > 0"
            type="submit"
            form="connect-runner"
            :disabled="connecting || !selectedMachine || !machineName.trim()"
          >
            <Loader2 v-if="connecting" class="animate-spin" aria-hidden="true" />
            Connect
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog v-model:open="issuedOpen">
      <DialogContent class="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{{ issued?.runner.name }} is registered</DialogTitle>
          <DialogDescription v-if="issuedForExisting">
            Copy it now - this is the only time the secret is shown. On
            {{ issued?.runner.name }}, this organization becomes one more profile beside the ones it
            already runs for:
          </DialogDescription>
          <DialogDescription v-else>
            Copy it now - this is the only time the secret is shown. On the machine, with the
            harnesses you want it to offer already signed in:
          </DialogDescription>
        </DialogHeader>

        <ol v-if="issued" class="text-muted-foreground min-w-0 list-decimal space-y-3 pl-4 text-xs">
          <li v-if="issuedForExisting">
            Update the CLI if it is older than this feature:
            <code class="font-mono">npm install -g @aictiq/cli@latest</code>
          </li>
          <li v-else>Install the CLI: <code class="font-mono">npm install -g @aictiq/cli</code></li>
          <li>
            Register this machine:
            <div class="mt-1.5 flex items-start gap-2">
              <code
                data-testid="runner-register-command"
                class="bg-background border-border text-foreground min-w-0 flex-1 rounded border px-2 py-1 font-mono text-[11px] break-all"
              >
                {{ command }}
              </code>
              <Button
                variant="ghost"
                size="icon"
                aria-label="Copy the command"
                @click="copy('command')"
              >
                <Check v-if="copied === 'command'" class="size-4" aria-hidden="true" />
                <Copy v-else class="size-4" aria-hidden="true" />
              </Button>
            </div>
          </li>
          <li v-if="issuedForExisting">
            A running <code class="font-mono">aictiq runner start</code> connects within a few
            seconds; otherwise start it. Then say where this organization's clones live:
            <code class="font-mono">aictiq runner root &lt;path&gt; --org {{ slug }}</code>
          </li>
          <li v-else>Start it: <code class="font-mono">aictiq runner start</code></li>
          <li v-if="!issuedForExisting" data-testid="runner-service-step">
            Then stop it and install it as a service, so it comes back after a logout or a reboot:
            <div class="mt-1.5 flex flex-wrap gap-1" role="group" aria-label="Runner platform">
              <Button
                v-for="(steps, key) in runnerServiceSteps"
                :key="key"
                size="sm"
                class="h-6 px-2 text-[11px]"
                :variant="platform === key ? 'secondary' : 'ghost'"
                :aria-pressed="platform === key"
                @click="platform = key"
              >
                {{ steps.label }}
              </Button>
            </div>
            <div class="mt-1.5 flex items-start gap-2">
              <pre
                data-testid="runner-service-command"
                class="bg-background border-border text-foreground min-w-0 flex-1 rounded border px-2 py-1 font-mono text-[11px] break-all whitespace-pre-wrap"
                >{{ service.commands }}</pre>
              <Button
                variant="ghost"
                size="icon"
                aria-label="Copy the service commands"
                @click="copy('service')"
              >
                <Check v-if="copied === 'service'" class="size-4" aria-hidden="true" />
                <Copy v-else class="size-4" aria-hidden="true" />
              </Button>
            </div>
            <p class="mt-1 text-[11px]">{{ service.note }}</p>
          </li>
        </ol>

        <div v-if="issued" class="border-border mt-1 flex items-center gap-2 rounded border p-2">
          <span class="text-muted-foreground text-[11px]">Secret</span>
          <code data-testid="runner-secret" class="min-w-0 flex-1 truncate font-mono text-[11px]">{{
            issued.secret
          }}</code>
          <Button variant="ghost" size="icon" aria-label="Copy the secret" @click="copy('secret')">
            <Check v-if="copied === 'secret'" class="size-4" aria-hidden="true" />
            <Copy v-else class="size-4" aria-hidden="true" />
          </Button>
        </div>

        <p v-if="issued" class="text-muted-foreground text-[11px]">
          Next, the setup guide follows {{ issued.runner.name }} and ticks each step as the machine
          reports it: harness, start, service, and the projects it can reach.
        </p>

        <DialogFooter>
          <Button data-testid="runner-continue-setup" @click="continueSetup">
            I have copied it - continue setup
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog
      :open="confirmingDelete !== null"
      @update:open="
        (open: boolean) => {
          if (!open) confirmingDelete = null
        }
      "
    >
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete {{ confirmingDelete?.name }}?</DialogTitle>
          <DialogDescription>
            Its secret stops working at once and it leaves this list. To bring the machine back,
            register a new runner. Disabling is the reversible option.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" @click="confirmingDelete = null">Cancel</Button>
          <Button variant="destructive" :disabled="busyId !== null" @click="remove">Delete</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
