<script setup lang="ts">
import { Check, Copy, Loader2, TriangleAlert } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'

import {
  createToken,
  describeScopes,
  listTokens,
  revokeToken,
  tokenScopes,
  type AccessToken,
  type AccessTokenCreated,
  type TokenScope,
} from '@/api/tokens'
import EmptyState from '@/components/common/EmptyState.vue'
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
import { Input } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'

/**
 * Access tokens, and the one screen in Aictiq where a secret is ever on display.
 *
 * The API stores only a hash, so the value shown after creating one cannot be fetched
 * again from anywhere - which is why the banner stays until it is dismissed rather than
 * disappearing on the next render, and why the dialog does not close by itself.
 */
const organizations = useOrganizationsStore()
const toast = useToast()

const tokens = ref<AccessToken[]>([])
const loading = ref(true)
const creating = ref(false)
const submitting = ref(false)
const busyId = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})
const justCreated = ref<AccessTokenCreated | null>(null)
const copied = ref(false)

const name = ref('')
const scopes = ref<TokenScope[]>([])
const expiresInDays = ref<number | ''>(365)
const bindToOrganization = ref(false)

const currentOrganization = computed(() => organizations.current)

async function load() {
  loading.value = true
  try {
    await organizations.load()
    tokens.value = await listTokens()
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

function open() {
  name.value = ''
  scopes.value = []
  expiresInDays.value = 365
  bindToOrganization.value = false
  fieldErrors.value = {}
  creating.value = true
}

function toggleScope(scope: TokenScope) {
  scopes.value = scopes.value.includes(scope)
    ? scopes.value.filter((s) => s !== scope)
    : [...scopes.value, scope]
}

async function submit() {
  submitting.value = true
  fieldErrors.value = {}

  try {
    const created = await createToken({
      name: name.value.trim(),
      scopes: scopes.value,
      organizationId:
        bindToOrganization.value && currentOrganization.value
          ? currentOrganization.value.id
          : undefined,
      expiresInDays: expiresInDays.value === '' ? undefined : Number(expiresInDays.value),
    })
    creating.value = false
    // Kept on screen deliberately: this is the only moment the secret exists outside the
    // caller's own copy of it.
    justCreated.value = created
    copied.value = false
    tokens.value = [created.token, ...tokens.value]
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

async function copy() {
  if (!justCreated.value) return
  try {
    await navigator.clipboard.writeText(justCreated.value.secret)
    copied.value = true
  } catch {
    toast.error(new Error('Could not copy - select the token and copy it manually.'))
  }
}

async function revoke(token: AccessToken) {
  busyId.value = token.id
  try {
    await revokeToken(token.id)
    tokens.value = tokens.value.filter((t) => t.id !== token.id)
    if (justCreated.value?.token.id === token.id) justCreated.value = null
    toast.success(`${token.name} was revoked.`)
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <SettingsSection
    wide
    title="Personal access tokens"
    description="How the CLI, the MCP server and your scripts sign in. A token can never do more than you can."
  >
    <div v-if="justCreated" class="border-primary/40 bg-accent/40 mb-4 rounded-lg border p-3" role="status">
      <div class="flex items-start gap-2">
        <TriangleAlert class="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <div class="min-w-0 flex-1">
          <p class="text-sm font-medium">Copy it now - this is the only time it is shown.</p>
          <p class="text-muted-foreground mt-0.5 text-xs">
            Aictiq stores only a hash of it, so it cannot be shown again. Revoke and create another
            if you lose it.
          </p>
          <code
            class="bg-background border-border mt-2 block truncate rounded border px-2 py-1.5 font-mono text-xs"
          >
            {{ justCreated.secret }}
          </code>
        </div>
        <Button variant="ghost" size="icon" aria-label="Copy the token" @click="copy">
          <Check v-if="copied" class="size-4" aria-hidden="true" />
          <Copy v-else class="size-4" aria-hidden="true" />
        </Button>
      </div>
      <div class="mt-2 flex justify-end">
        <Button variant="ghost" size="sm" @click="justCreated = null">Done</Button>
      </div>
    </div>

    <UiPageState v-if="loading" state="loading" />

    <EmptyState
      v-else-if="tokens.length === 0"
      title="No tokens yet"
      description="Create one for each thing that signs in as you - a laptop, a CI job, an agent - so you can revoke them one at a time."
      icon="◇"
    >
      <Button @click="open">New token</Button>
    </EmptyState>

    <template v-else>
      <div class="flex justify-end">
        <Button size="sm" @click="open">New token</Button>
      </div>

      <table class="mt-3 w-full border-collapse text-sm">
        <thead>
          <tr class="border-border border-b">
            <th scope="col" class="font-label px-3 py-2 text-left">Token</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Can do</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Last used</th>
            <th scope="col" class="w-20 px-3 py-2"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="token in tokens"
            :key="token.id"
            class="border-border/60 hover:bg-accent/60 border-b last:border-b-0"
          >
            <td class="px-3 py-2">
              <div class="truncate font-medium">{{ token.name }}</div>
              <div class="text-muted-foreground truncate font-mono text-[11px]">
                {{ token.display }}
              </div>
            </td>
            <td class="px-3 py-2 text-xs">
              {{ describeScopes(token.scopes) }}
              <span v-if="token.organizationId" class="text-muted-foreground block">
                One organization only
              </span>
            </td>
            <td class="text-muted-foreground px-3 py-2 text-xs">
              <span v-if="token.isExpired" class="text-destructive">Expired</span>
              <span v-else-if="token.lastUsedAt">
                {{ new Date(token.lastUsedAt).toLocaleDateString() }}
              </span>
              <span v-else>Never</span>
            </td>
            <td class="px-3 py-2 text-right">
              <Loader2
                v-if="busyId === token.id"
                class="text-muted-foreground inline size-4 animate-spin"
                aria-hidden="true"
              />
              <Button v-else variant="ghost" size="sm" @click="revoke(token)">Revoke</Button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>

    <Dialog v-model:open="creating">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>New access token</DialogTitle>
          <DialogDescription>
            Give it its own name so you can revoke it without touching the others.
          </DialogDescription>
        </DialogHeader>

        <form id="create-token" class="space-y-4" novalidate @submit.prevent="submit">
          <div class="space-y-1.5">
            <label for="token-name" class="text-sm font-medium">Name</label>
            <Input
              id="token-name"
              v-model="name"
              required
              placeholder="laptop CLI"
              :aria-invalid="Boolean(fieldErrors.name)"
            />
            <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">What it may do</legend>
            <label
              v-for="option in tokenScopes"
              :key="option.scope"
              class="flex cursor-pointer items-start gap-2 text-[12.5px]"
            >
              <input
                type="checkbox"
                class="mt-0.5"
                :checked="scopes.includes(option.scope)"
                @change="toggleScope(option.scope)"
              />
              <span>
                <span class="font-medium">{{ option.label }}</span>
                <span class="text-muted-foreground"> - {{ option.description }}</span>
              </span>
            </label>
            <p class="text-muted-foreground text-xs">
              Tick nothing and the token can do everything you can. Ticking narrows it; it can
              never grant more than you have.
            </p>
            <p v-for="message in fieldErrors.scopes" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </fieldset>

          <div class="space-y-1.5">
            <label for="token-expiry" class="text-sm font-medium">Expires in</label>
            <div class="flex items-center gap-2">
              <Input
                id="token-expiry"
                v-model="expiresInDays"
                type="number"
                min="1"
                max="3650"
                class="w-24"
                :aria-invalid="Boolean(fieldErrors.expiresInDays)"
              />
              <span class="text-muted-foreground text-sm">days</span>
            </div>
            <p
              v-for="message in fieldErrors.expiresInDays"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <label
            v-if="currentOrganization"
            class="flex cursor-pointer items-start gap-2 text-[12.5px]"
          >
            <input v-model="bindToOrganization" type="checkbox" class="mt-0.5" />
            <span>
              <span class="font-medium">Only for {{ currentOrganization.name }}</span>
              <span class="text-muted-foreground">
                - the token is refused everywhere else, even where you are a member.
              </span>
            </span>
          </label>
        </form>

        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="submitting" @click="creating = false">
            Cancel
          </Button>
          <Button type="submit" form="create-token" :disabled="submitting || !name.trim()">
            <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
            Create token
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
