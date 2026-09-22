<script setup lang="ts">
import { Check, Copy, GitPullRequest, Loader2, MoreHorizontal } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import {
  createAgent,
  createAgentToken,
  disableAgent,
  listAgentTokens,
  listAgents,
  revokeAgentToken,
  updateAgent,
  type Agent,
  type AgentToken,
  type AgentTokenIssued,
} from '@/api/agents'
import {
  getAgentContribution,
  listAgentActivity,
  type AgentActivityEntry,
  type AgentContribution,
} from '@/api/agent-activity'
import { hasOrgRole } from '@/api/organizations'
import { listRuns, type Run } from '@/api/runs'
import { tokenScopes, type TokenScope } from '@/api/tokens'
import EmptyState from '@/components/common/EmptyState.vue'
import InlineEdit from '@/components/common/InlineEdit.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import AgentConnectSheet from '@/components/settings/AgentConnectSheet.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
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
import { runDuration, runStatusLabel, runStatusTone, runSuccessRate } from '@/lib/runs'
import { factoryRunPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * The agents of one organization, and the tokens that are their only way in.
 *
 * Two things this screen has to keep saying out loud: who *owns* each agent - the person
 * answerable for what it does - and that a token is shown exactly once.
 */
const org = useOrgScope()
const session = useSessionStore()
const organizations = useOrganizationsStore()
const router = useRouter()
const toast = useToast()

const agents = ref<Agent[]>([])
const loading = ref(true)
const creating = ref(false)
const submitting = ref(false)
const busyId = ref<string | null>(null)

const expanded = ref<string | null>(null)
const tokens = ref<AgentToken[]>([])
const tokensLoading = ref(false)
const justIssued = ref<AgentTokenIssued | null>(null)
const copied = ref(false)

/**
 * The connect drawer. It opens by itself the moment a token is issued, because that is the
 * only moment the snippets can carry a real token - Aictiq never shows a secret twice.
 */
const connecting = ref<Agent | null>(null)
const connectOpen = ref(false)

/**
 * What the agents are actually doing. Kept beside the roster rather than on a dashboard
 * because the first question about an agent is never "how many tokens does it have" - it
 * is "what has it been doing, and should it still be running".
 */
const activity = ref<AgentActivityEntry[]>([])
const activityLoading = ref(false)
const contribution = ref<AgentContribution | null>(null)
/** The `actor:agents` feed for the whole organization - everything every agent just did. */
const orgActivity = ref<AgentActivityEntry[]>([])
const showOrgActivity = ref(false)

/**
 * What each agent's runs say about it, kept once loaded: the drawer refetches tokens and
 * activity every time it opens, but the record of what has run does not change under it.
 */
interface AgentRuns {
  runs: Run[]
  rate: { rate: number | null; total: number } | null
  failed: boolean
}
const runsByAgent = ref<Record<string, AgentRuns>>({})
const runsLoading = ref<Record<string, boolean>>({})
const runsOpen = ref<Record<string, boolean>>({})

const displayName = ref('')
const tokenName = ref('')
const tokenScopeSelection = ref<TokenScope[]>(['read', 'write', 'mcp'])
const fieldErrors = ref<Record<string, string[]>>({})

const slug = computed(() => org.slug.value)
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))
/** Factory operators may follow a run to its page; stakeholders see its status only. */
const mayOperateFactory = computed(() => organizations.current?.canOperateFactory === true)

/** Its owner may rotate its token without asking an admin - the credential is theirs to replace. */
function mayManageTokens(agent: Agent) {
  return mayManage.value || agent.ownerUserId === session.user?.id
}

async function load() {
  runsByAgent.value = {}
  runsOpen.value = {}
  loading.value = true
  try {
    agents.value = await listAgents(slug.value)
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
  // Never fatal to the roster: a Guest can read the agents but may see no projects at all,
  // and an empty feed is the honest answer rather than an error over the whole screen.
  try {
    contribution.value = await getAgentContribution(slug.value)
  } catch {
    contribution.value = null
  }
  try {
    orgActivity.value = await listAgentActivity(slug.value, { agentsOnly: true, limit: 50 })
  } catch {
    orgActivity.value = []
  }
}

watch(slug, load, { immediate: true })

async function submit() {
  submitting.value = true
  fieldErrors.value = {}

  try {
    const created = await createAgent(slug.value, { displayName: displayName.value.trim() })
    agents.value = [...agents.value, created]
    creating.value = false
    displayName.value = ''
    // Straight into its tokens: an agent with no token cannot do anything, so the next
    // thing anyone wants is the one it is about to be given.
    await open(created)
    toast.success(`${created.displayName} is ready. Give it a token to get started.`)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

async function open(agent: Agent) {
  if (expanded.value === agent.userId) {
    expanded.value = null
    return
  }

  expanded.value = agent.userId
  justIssued.value = null
  tokens.value = []
  activity.value = []

  tokensLoading.value = true
  activityLoading.value = true
  void loadRuns(agent.userId)
  try {
    tokens.value = await listAgentTokens(slug.value, agent.userId)
  } catch (error) {
    toast.error(error)
  } finally {
    tokensLoading.value = false
  }
  try {
    activity.value = await listAgentActivity(slug.value, { actorId: agent.userId, limit: 50 })
  } catch {
    activity.value = []
  } finally {
    activityLoading.value = false
  }
}

/**
 * The runs block loads when its drawer opens: the last ten runs for the rows, and enough
 * pages behind them to rate the last thirty days. Either failing leaves the block quiet
 * rather than taking the roster down with it.
 */
async function loadRuns(agentUserId: string) {
  if (runsByAgent.value[agentUserId] || runsLoading.value[agentUserId]) return
  runsLoading.value[agentUserId] = true
  try {
    const cutoff = new Date(Date.now() - 30 * 86_400_000)
    const [recent, windowRuns] = await Promise.all([
      listRuns(slug.value, { agent: agentUserId, pageSize: 10 }),
      runsBeforeCutoff(agentUserId, cutoff),
    ])
    runsByAgent.value[agentUserId] = {
      runs: recent.items,
      rate: runSuccessRate(windowRuns, cutoff),
      failed: false,
    }
  } catch {
    runsByAgent.value[agentUserId] = { runs: [], rate: null, failed: true }
  } finally {
    delete runsLoading.value[agentUserId]
  }
}

/** Newest-first pages of 100 until a page reaches past the cutoff, runs short, or five are read. */
async function runsBeforeCutoff(agentUserId: string, cutoff: Date): Promise<Run[]> {
  const all: Run[] = []
  for (let page = 1; page <= 5; page++) {
    const batch = await listRuns(slug.value, { agent: agentUserId, page, pageSize: 100 })
    all.push(...batch.items)
    const oldest = batch.items.at(-1)
    if (batch.items.length < 100 || !oldest || new Date(oldest.queuedAt) < cutoff) break
  }
  return all
}

function runsSummary(agentUserId: string): string {
  const rate = runsByAgent.value[agentUserId]?.rate
  if (!rate) return ''
  if (rate.total === 0 || rate.rate === null) return 'No runs in the last 30 days'
  return `${rate.rate}% succeeded · ${rate.total} ${rate.total === 1 ? 'run' : 'runs'} in the last 30 days`
}

function runsOf(agentUserId: string): Run[] {
  return runsByAgent.value[agentUserId]?.runs ?? []
}

function openRun(run: Run) {
  if (!mayOperateFactory.value) return
  void router.push(factoryRunPath(slug.value, run.id))
}

/** "3 days ago" is what a token's last use is actually read for: is this thing still alive. */
function ago(value: string | null): string {
  if (!value) return 'never'
  const days = Math.floor((Date.now() - new Date(value).getTime()) / 86_400_000)
  if (days <= 0) return 'today'
  return days === 1 ? 'yesterday' : `${days} days ago`
}

/**
 * Renaming is only ever cosmetic: the agent's id is what its work is recorded against, so
 * a name it turned out to share with a person can be fixed without disturbing anything it
 * has already done.
 */
async function rename(agent: Agent, name: string) {
  busyId.value = agent.userId
  try {
    const updated = await updateAgent(slug.value, agent.userId, { displayName: name })
    agents.value = agents.value.map((a) => (a.userId === updated.userId ? updated : a))
  } catch (error) {
    toast.error(error)
    await load()
  } finally {
    busyId.value = null
  }
}

function toggleScope(scope: TokenScope) {
  tokenScopeSelection.value = tokenScopeSelection.value.includes(scope)
    ? tokenScopeSelection.value.filter((s) => s !== scope)
    : [...tokenScopeSelection.value, scope]
}

async function issue(agent: Agent) {
  if (!tokenName.value.trim()) return

  busyId.value = agent.userId
  try {
    justIssued.value = await createAgentToken(slug.value, agent.userId, {
      name: tokenName.value.trim(),
      scopes: tokenScopeSelection.value,
    })
    copied.value = false
    tokenName.value = ''
    tokens.value = await listAgentTokens(slug.value, agent.userId)
    await load()
    connect(agent)
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

function connect(agent: Agent) {
  connecting.value = agent
  connectOpen.value = true
}

async function copy() {
  if (!justIssued.value) return
  try {
    await navigator.clipboard.writeText(justIssued.value.secret)
    copied.value = true
  } catch {
    toast.error(new Error('Could not copy - select the token and copy it manually.'))
  }
}

async function revoke(agent: Agent, token: AgentToken) {
  busyId.value = agent.userId
  try {
    await revokeAgentToken(slug.value, agent.userId, token.id)
    tokens.value = tokens.value.filter((t) => t.id !== token.id)
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

async function setActive(agent: Agent, isActive: boolean) {
  busyId.value = agent.userId
  try {
    if (isActive) {
      const updated = await updateAgent(slug.value, agent.userId, { isActive: true })
      agents.value = agents.value.map((a) => (a.userId === updated.userId ? updated : a))
      toast.success(`${agent.displayName} is active again. Give it a new token.`)
    } else {
      await disableAgent(slug.value, agent.userId)
      await load()
      toast.success(`${agent.displayName} was disabled and its tokens revoked.`)
    }
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <SettingsSection wide>
    <header class="flex items-center justify-between gap-3 pb-3">
      <div>
        <h2 class="text-sm font-medium">Agents</h2>
        <p class="text-muted-foreground mt-0.5 text-xs">
          An agent is a member of this organization that signs in with a token instead of a
          password, and someone here is answerable for it. A token is shown once, when it is
          created.
        </p>
      </div>
      <Button v-if="mayManage" size="sm" @click="creating = true">New agent</Button>
    </header>

    <div
      v-if="contribution && (contribution.completedTotal > 0 || contribution.byAgent.length > 0)"
      class="border-border mb-4 rounded-lg border p-3"
    >
      <p class="font-label">This sprint</p>
      <p class="mt-1 text-sm">
        Agents finished
        <strong>{{ contribution.completedByAgents }}</strong>
        of <strong>{{ contribution.completedTotal }}</strong>
        completed items in the active sprints.
      </p>
      <ul v-if="contribution.byAgent.length" class="mt-2 space-y-1">
        <li
          v-for="row in contribution.byAgent"
          :key="row.agent.id"
          class="flex items-center gap-2 text-xs"
        >
          <UserAvatar :name="row.agent.displayName" is-agent size="sm" />
          <span class="font-medium">{{ row.agent.displayName }}</span>
          <span class="text-muted-foreground">
            {{ row.completed }} done<template v-if="row.inProgress">
              · {{ row.inProgress }} in progress</template
            >
          </span>
        </li>
      </ul>
    </div>

    <div v-if="orgActivity.length" class="border-border mb-4 rounded-lg border p-3">
      <div class="flex items-center justify-between gap-2">
        <p class="font-label">What the agents are doing</p>
        <Button variant="ghost" size="sm" @click="showOrgActivity = !showOrgActivity">
          {{ showOrgActivity ? 'Hide' : `Show ${orgActivity.length}` }}
        </Button>
      </div>
      <ul v-if="showOrgActivity" class="mt-2 space-y-1">
        <li
          v-for="(entry, index) in orgActivity"
          :key="`${entry.itemKey}-${entry.at}-${index}`"
          class="flex items-center gap-2 text-xs"
        >
          <UserAvatar :name="entry.actor?.displayName ?? 'Agent'" is-agent size="sm" />
          <span class="font-medium">{{ entry.actor?.displayName ?? 'Agent' }}</span>
          <span class="font-mono">{{ entry.itemKey }}</span>
          <span class="text-muted-foreground truncate">
            {{ entry.kind === 'commented' ? entry.summary : `changed ${entry.summary}` }} ·
            {{ since(entry.at) }}
          </span>
        </li>
      </ul>
    </div>

    <UiPageState v-if="loading" state="loading" />

    <EmptyState
      v-else-if="agents.length === 0"
      title="No agents yet"
      description="An agent is a member of this organization that signs in with a token instead of a password. Everything it does is signed with its name and yours."
      icon="◇"
    >
      <Button v-if="mayManage" @click="creating = true">New agent</Button>
    </EmptyState>

    <ul v-else class="border-border divide-border divide-y rounded-lg border">
      <li v-for="agent in agents" :key="agent.userId" class="px-3 py-2.5">
        <div class="flex items-center gap-2.5">
          <UserAvatar :name="agent.displayName" is-agent />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-1.5">
              <InlineEdit
                :model-value="agent.displayName"
                :disabled="!mayManage"
                :label="`Name of ${agent.displayName}`"
                class="min-w-0 flex-1"
                @commit="(name: string) => rename(agent, name)"
              />
              <span v-if="!agent.isActive" class="text-muted-foreground text-sm">· disabled</span>
            </div>
            <div class="text-muted-foreground truncate text-[11px]">
              owned by {{ agent.ownerName }} · {{ agent.tokenCount }}
              {{ agent.tokenCount === 1 ? 'token' : 'tokens' }} ·
              <template v-if="agent.lastActiveAt">
                last active {{ new Date(agent.lastActiveAt).toLocaleDateString() }}
              </template>
              <template v-else>never used</template>
            </div>
          </div>

          <Loader2
            v-if="busyId === agent.userId"
            class="text-muted-foreground size-4 animate-spin"
            aria-hidden="true"
          />
          <template v-else>
            <Button
              v-if="mayManageTokens(agent) && agent.isActive"
              variant="ghost"
              size="sm"
              @click="connect(agent)"
            >
              Connect
            </Button>
            <Button v-if="mayManageTokens(agent)" variant="ghost" size="sm" @click="open(agent)">
              {{ expanded === agent.userId ? 'Hide tokens' : 'Tokens' }}
            </Button>
            <DropdownMenu v-if="mayManage">
              <DropdownMenuTrigger as-child>
                <Button variant="ghost" size="icon" :aria-label="`Manage ${agent.displayName}`">
                  <MoreHorizontal class="size-4" aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" class="w-56">
                <DropdownMenuItem
                  v-if="agent.isActive"
                  variant="destructive"
                  @select="setActive(agent, false)"
                >
                  Disable and revoke its tokens
                </DropdownMenuItem>
                <DropdownMenuItem v-else @select="setActive(agent, true)">
                  Re-enable
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </template>
        </div>

        <div
          v-if="expanded === agent.userId"
          class="border-border mt-2.5 space-y-2.5 border-t pt-2.5"
        >
          <div
            v-if="justIssued"
            class="border-primary/40 bg-accent/40 rounded-lg border p-2.5"
            role="status"
          >
            <p class="text-xs font-medium">Copy it now - this is the only time it is shown.</p>
            <div class="mt-1.5 flex items-center gap-2">
              <code
                class="bg-background border-border min-w-0 flex-1 truncate rounded border px-2 py-1 font-mono text-[11px]"
              >
                {{ justIssued.secret }}
              </code>
              <Button variant="ghost" size="icon" aria-label="Copy the token" @click="copy">
                <Check v-if="copied" class="size-4" aria-hidden="true" />
                <Copy v-else class="size-4" aria-hidden="true" />
              </Button>
            </div>
          </div>

          <UiPageState v-if="tokensLoading" state="loading" />

          <p v-else-if="tokens.length === 0" class="text-muted-foreground text-xs">
            No tokens. Until it has one, this agent cannot do anything at all.
          </p>

          <div
            v-for="token in tokens"
            v-else
            :key="token.id"
            class="flex items-center gap-2 text-xs"
          >
            <span class="font-medium">{{ token.name }}</span>
            <span class="text-muted-foreground font-mono">{{ token.display }}</span>
            <span class="text-muted-foreground flex-1 truncate">
              {{ token.scopes.length ? token.scopes.join(', ') : 'everything its owner can do' }}
            </span>
            <span class="text-muted-foreground">last used {{ ago(token.lastUsedAt) }}</span>
            <Button variant="ghost" size="sm" @click="revoke(agent, token)">Revoke</Button>
          </div>

          <form
            v-if="agent.isActive"
            class="flex items-end gap-2"
            novalidate
            @submit.prevent="issue(agent)"
          >
            <div class="flex-1">
              <label :for="`token-name-${agent.userId}`" class="sr-only">Token name</label>
              <Input
                :id="`token-name-${agent.userId}`"
                v-model="tokenName"
                placeholder="Where it will run - 'CI', 'workstation'"
              />
            </div>
            <Button type="submit" size="sm" :disabled="!tokenName.trim()">New token</Button>
          </form>

          <div class="border-border border-t pt-2.5">
            <p class="font-label">Recent activity</p>
            <UiPageState v-if="activityLoading" state="loading" />
            <p v-else-if="activity.length === 0" class="text-muted-foreground mt-1 text-xs">
              Nothing yet in the projects you can see.
            </p>
            <ul v-else class="mt-1.5 space-y-1">
              <li
                v-for="(entry, index) in activity"
                :key="`${entry.itemKey}-${entry.at}-${index}`"
                class="text-xs"
              >
                <span class="font-mono">{{ entry.itemKey }}</span>
                <span class="text-muted-foreground">
                  {{ entry.kind === 'commented' ? 'commented' : `changed ${entry.summary}` }} ·
                  {{ since(entry.at) }}
                </span>
                <span v-if="entry.kind === 'commented'" class="ml-1">{{ entry.summary }}</span>
              </li>
            </ul>
          </div>

          <div class="border-border border-t pt-2.5">
            <div class="flex items-center justify-between gap-2">
              <p class="font-label">Runs</p>
              <div class="flex items-center gap-2">
                <span v-if="runsSummary(agent.userId)" class="text-muted-foreground text-[11px]">
                  {{ runsSummary(agent.userId) }}
                </span>
                <Button
                  v-if="runsOf(agent.userId).length"
                  variant="ghost"
                  size="sm"
                  @click="runsOpen[agent.userId] = !runsOpen[agent.userId]"
                >
                  {{ runsOpen[agent.userId] ? 'Hide' : `Show ${runsOf(agent.userId).length}` }}
                </Button>
              </div>
            </div>
            <UiPageState v-if="runsLoading[agent.userId]" state="loading" />
            <p
              v-else-if="runsByAgent[agent.userId]?.failed"
              class="text-muted-foreground mt-1 text-xs"
            >
              Runs could not be loaded.
            </p>
            <ul
              v-else-if="runsOpen[agent.userId] && runsOf(agent.userId).length"
              class="mt-1.5 space-y-1"
            >
              <li
                v-for="run in runsOf(agent.userId)"
                :key="run.id"
                class="flex items-center gap-2 text-xs"
              >
                <component
                  :is="mayOperateFactory ? 'button' : 'div'"
                  class="flex min-w-0 flex-1 items-center gap-2 rounded px-1 py-0.5 text-left"
                  :class="mayOperateFactory ? 'cursor-pointer hover:bg-accent/60' : ''"
                  :type="mayOperateFactory ? 'button' : undefined"
                  :aria-label="mayOperateFactory ? `Open the run of ${run.itemKey}` : undefined"
                  @click="openRun(run)"
                >
                  <span
                    class="shrink-0 whitespace-nowrap rounded border px-1.5 text-[11px] leading-4"
                    :class="runStatusTone[run.status]"
                  >
                    {{ runStatusLabel[run.status] }}
                  </span>
                  <span class="shrink-0 font-mono">{{ run.itemKey }}</span>
                  <span class="text-muted-foreground min-w-0 flex-1 truncate">
                    {{ run.playbookName ?? '' }}
                  </span>
                  <span v-if="runDuration(run)" class="text-muted-foreground shrink-0">
                    {{ runDuration(run) }}
                  </span>
                </component>
                <a
                  v-if="run.pullRequestUrl"
                  :href="run.pullRequestUrl"
                  target="_blank"
                  rel="noopener noreferrer"
                  class="text-muted-foreground hover:text-foreground shrink-0"
                  :aria-label="`Pull request for ${run.itemKey}`"
                >
                  <GitPullRequest class="size-3.5" aria-hidden="true" />
                </a>
              </li>
            </ul>
          </div>

          <fieldset v-if="agent.isActive" class="flex flex-wrap gap-3">
            <legend class="sr-only">What the token may do</legend>
            <label
              v-for="option in tokenScopes"
              :key="option.scope"
              class="flex cursor-pointer items-center gap-1.5 text-[11px]"
            >
              <input
                type="checkbox"
                :checked="tokenScopeSelection.includes(option.scope)"
                @change="toggleScope(option.scope)"
              />
              {{ option.label }}
            </label>
          </fieldset>
        </div>
      </li>
    </ul>

    <AgentConnectSheet
      v-model:open="connectOpen"
      :agent="connecting"
      :slug="slug"
      :secret="
        connecting && justIssued && expanded === connecting.userId ? justIssued.secret : null
      "
    />

    <Dialog v-model:open="creating">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>New agent</DialogTitle>
          <DialogDescription>
            It joins as a member and you are answerable for it. It signs in with a token, never a
            password, and everything it does is labelled as its work.
          </DialogDescription>
        </DialogHeader>

        <form id="create-agent" class="space-y-4" novalidate @submit.prevent="submit">
          <div class="space-y-1.5">
            <label for="agent-name" class="text-sm font-medium">Name</label>
            <Input
              id="agent-name"
              v-model="displayName"
              required
              placeholder="claude-dev"
              :aria-invalid="Boolean(fieldErrors.displayName)"
            />
            <p
              v-for="message in fieldErrors.displayName"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>
        </form>

        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="submitting" @click="creating = false">
            Cancel
          </Button>
          <Button type="submit" form="create-agent" :disabled="submitting || !displayName.trim()">
            <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
            Create agent
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
