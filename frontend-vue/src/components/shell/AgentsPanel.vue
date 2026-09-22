<script setup lang="ts">
import { Check, Copy, Loader2, MoreHorizontal } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'

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
import { hasOrgRole } from '@/api/organizations'
import { tokenScopes, type TokenScope } from '@/api/tokens'
import EmptyState from '@/components/common/EmptyState.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
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
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * The agents of one organization, and the tokens that are their only way in.
 *
 * Two things this screen has to keep saying out loud: who *owns* each agent - the person
 * answerable for what it does - and that a token is shown exactly once.
 */
const organizations = useOrganizationsStore()
const session = useSessionStore()
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

const displayName = ref('')
const tokenName = ref('')
const tokenScopeSelection = ref<TokenScope[]>(['read', 'write', 'mcp'])
const fieldErrors = ref<Record<string, string[]>>({})

const slug = computed(() => organizations.currentSlug)
const myRole = computed(() => organizations.current?.role)
const mayManage = computed(() => hasOrgRole(myRole.value, 'admin'))

/** Its owner may rotate its token without asking an admin - the credential is theirs to replace. */
function mayManageTokens(agent: Agent) {
  return mayManage.value || agent.ownerUserId === session.user?.id
}

async function load() {
  if (!slug.value) return
  loading.value = true
  try {
    agents.value = await listAgents(slug.value)
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(slug, load)

async function submit() {
  if (!slug.value) return
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
  if (!slug.value) return

  tokensLoading.value = true
  try {
    tokens.value = await listAgentTokens(slug.value, agent.userId)
  } catch (error) {
    toast.error(error)
  } finally {
    tokensLoading.value = false
  }
}

function toggleScope(scope: TokenScope) {
  tokenScopeSelection.value = tokenScopeSelection.value.includes(scope)
    ? tokenScopeSelection.value.filter((s) => s !== scope)
    : [...tokenScopeSelection.value, scope]
}

async function issue(agent: Agent) {
  if (!slug.value || !tokenName.value.trim()) return

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
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
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
  if (!slug.value) return
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
  if (!slug.value) return
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
  <UiPageState v-if="loading" state="loading" />

  <template v-else>
    <div v-if="mayManage" class="flex justify-end py-3">
      <Button size="sm" @click="creating = true">New agent</Button>
    </div>

    <EmptyState
      v-if="agents.length === 0"
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
            <div class="truncate text-sm font-medium">
              {{ agent.displayName }}
              <span v-if="!agent.isActive" class="text-muted-foreground font-normal">
                · disabled
              </span>
            </div>
            <div class="text-muted-foreground truncate text-[11px]">
              owned by {{ agent.ownerName }} ·
              {{ agent.tokenCount }} {{ agent.tokenCount === 1 ? 'token' : 'tokens' }} ·
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
              v-if="mayManageTokens(agent)"
              variant="ghost"
              size="sm"
              @click="open(agent)"
            >
              {{ expanded === agent.userId ? 'Hide tokens' : 'Tokens' }}
            </Button>
            <DropdownMenu v-if="mayManage">
              <DropdownMenuTrigger as-child>
                <Button variant="ghost" size="icon" :aria-label="`Manage ${agent.displayName}`">
                  <MoreHorizontal class="size-4" aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" class="w-56">
                <DropdownMenuItem v-if="agent.isActive" variant="destructive" @select="setActive(agent, false)">
                  Disable and revoke its tokens
                </DropdownMenuItem>
                <DropdownMenuItem v-else @select="setActive(agent, true)">
                  Re-enable
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </template>
        </div>

        <div v-if="expanded === agent.userId" class="border-border mt-2.5 space-y-2.5 border-t pt-2.5">
          <div
            v-if="justIssued"
            class="border-primary/40 bg-accent/40 rounded-lg border p-2.5"
            role="status"
          >
            <p class="text-xs font-medium">Copy it now - this is the only time it is shown.</p>
            <div class="mt-1.5 flex items-center gap-2">
              <code class="bg-background border-border min-w-0 flex-1 truncate rounded border px-2 py-1 font-mono text-[11px]">
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

    <Dialog v-model:open="creating">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>New agent</DialogTitle>
          <DialogDescription>
            It joins as a member and you are answerable for it. It signs in with a token, never
            a password, and everything it does is labelled as its work.
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
  </template>
</template>
