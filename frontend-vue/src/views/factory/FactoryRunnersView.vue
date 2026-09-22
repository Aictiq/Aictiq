<script setup lang="ts">
import { Check, Copy, Loader2, MoreHorizontal } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import { hasOrgRole } from '@/api/organizations'
import {
  deleteRunner,
  listRunners,
  registerRunner,
  rotateRunner,
  updateRunner,
  type Runner,
  type RunnerIssued,
} from '@/api/runners'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import InlineEdit from '@/components/common/InlineEdit.vue'
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
import { since } from '@/lib/claims'
import { runnerRegisterCommand, runnerStatus, runnerStatusLabel } from '@/lib/runners'
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

const slug = computed(() => org.slug.value)
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))

const runners = ref<Runner[]>([])
const loading = ref(true)
const busyId = ref<string | null>(null)

const registering = ref(false)
const name = ref('')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

/** The secret just minted, and which runner it belongs to. Cleared when the dialog closes. */
const issued = ref<RunnerIssued | null>(null)
const issuedOpen = ref(false)
const copied = ref<'secret' | 'command' | null>(null)

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
    runners.value = await listRunners(slug.value)
  } catch (error) {
    if (!quiet) toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([slug, mayManage], () => load(), { immediate: true })

// "Online" is a judgement about time, so the roster refreshes itself quietly: a machine that
// stops heartbeating should turn grey without anyone reloading the page.
let refresh: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  refresh = setInterval(() => void load(true), 30_000)
})
onBeforeUnmount(() => clearInterval(refresh))

function replace(updated: Runner) {
  runners.value = runners.value.map((r) => (r.id === updated.id ? updated : r))
}

function showSecret(value: RunnerIssued) {
  issued.value = value
  copied.value = null
  issuedOpen.value = true
}

watch(issuedOpen, (open) => {
  // The secret leaves memory with the dialog: there is no second look.
  if (!open) issued.value = null
})

async function submit() {
  submitting.value = true
  fieldErrors.value = {}
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
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
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

async function copy(what: 'secret' | 'command') {
  if (!issued.value) return
  try {
    await navigator.clipboard.writeText(what === 'secret' ? issued.value.secret : command.value)
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
          a laptop, a CI box - with Claude Code, Codex or OpenCode already signed in. It picks up
          runs and reports back. Its secret is shown once, when it is registered.
        </p>
      </div>
      <Button v-if="mayManage" size="sm" @click="registering = true">Register runner</Button>
    </header>

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
      description="Register one, then run the command it gives you on the machine. It turns green here when it says hello."
      icon="◇"
    >
      <div class="flex flex-wrap justify-center gap-2">
        <Button @click="registering = true">Register runner</Button>
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
            Install Node.js and <code class="font-mono">@aictiq/cli</code> on a machine dedicated to
            this organization.
          </li>
          <li>
            <span class="text-foreground font-medium">Sign in and clone.</span>
            Sign in to Claude Code, Codex or OpenCode as the runner user, then clone the
            repositories it will work in.
          </li>
          <li>
            <span class="text-foreground font-medium">Register and keep it running.</span>
            Create the runner here, paste its one-time command on the machine, then start it.
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

    <Dialog v-model:open="issuedOpen">
      <DialogContent class="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{{ issued?.runner.name }} is registered</DialogTitle>
          <DialogDescription>
            Copy it now - this is the only time the secret is shown. On the machine, with the
            harnesses you want it to offer already signed in:
          </DialogDescription>
        </DialogHeader>

        <ol v-if="issued" class="text-muted-foreground list-decimal space-y-3 pl-4 text-xs">
          <li>Install the CLI: <code class="font-mono">npm install -g @aictiq/cli</code></li>
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
          <li>Start it: <code class="font-mono">aictiq runner start</code></li>
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

        <DialogFooter>
          <Button @click="issuedOpen = false">I have copied it</Button>
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
