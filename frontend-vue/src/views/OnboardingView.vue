<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { createInvitation, parseAddresses } from '@/api/invitations'
import { getOrganizationCreationPolicy } from '@/api/organizations'
import { suggestKey } from '@/api/projects'
import { createToken, type AccessTokenCreated } from '@/api/tokens'
import EmptyState from '@/components/common/EmptyState.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { ApiError } from '@/utils/api'

const onboarding = useOnboardingStore()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const router = useRouter()
const toast = useToast()
const step = ref(1)
const loading = ref(true)
const canCreate = ref(true)
const busy = ref(false)
const orgName = ref('')
const projectName = ref('')
const projectKey = ref('')
const emails = ref('')
const token = ref<AccessTokenCreated | null>(null)
const errors = ref<Record<string, string[]>>({})
const key = computed(() => projectKey.value.trim().toUpperCase() || suggestKey(projectName.value))

onMounted(async () => {
  await organizations.load()
  if (!organizations.isEmpty) { await router.replace('/projects'); return }
  try { canCreate.value = (await getOrganizationCreationPolicy()).canCreateOrganization } catch (error) { toast.error(error) }
  finally { loading.value = false }
})

async function createOrganization() {
  busy.value = true; errors.value = {}
  try { await organizations.create({ name: orgName.value.trim() }); step.value = 2 }
  catch (error) { if (error instanceof ApiError) errors.value = error.fieldErrors; else toast.error(error) }
  finally { busy.value = false }
}
async function createProject() {
  busy.value = true; errors.value = {}
  try { await projects.create({ name: projectName.value.trim(), key: key.value || undefined }); step.value = 3 }
  catch (error) { if (error instanceof ApiError) errors.value = error.fieldErrors; else toast.error(error) }
  finally { busy.value = false }
}
async function invite() {
  busy.value = true
  try { await Promise.all(parseAddresses(emails.value).map((email) => createInvitation(organizations.currentSlug!, email, 'member'))); step.value = 4 }
  catch (error) { toast.error(error) } finally { busy.value = false }
}
/**
 * The manual CLI/MCP path, and deliberately not the default one: this mints a
 * *personal* access token, which is how a person's own tooling talks to the API. It is
 * not a runner credential — a runner gets its own `jrn_` secret from Factory → Runners —
 * and it is not how an agent is given an identity either. Setting up AI work properly is
 * what Get started walks through, so that is the primary action here.
 */
async function connectAgent() {
  busy.value = true
  try { token.value = await createToken({ name: 'Onboarding agent', scopes: ['read', 'write', 'mcp'] }) }
  catch (error) { toast.error(error) } finally { busy.value = false }
}
async function finish() { await router.replace('/backlog') }
// Open first, then leave: the next screen mounts its own shell, and a shell that mounts
// with the checklist already open never offers the welcome dialog on top of it.
async function finishIntoChecklist() { onboarding.openChecklist(); await finish() }
</script>

<template>
  <AppShell><main class="mx-auto max-w-xl px-6 py-10">
    <p class="font-label">Get started · {{ step }} of 4</p>
    <h1 class="mt-1 text-2xl font-semibold">{{ ['Create your organization', 'Create the first project', 'Invite your teammates', 'Set up AI work'][step - 1] }}</h1>
    <p class="text-muted-foreground mt-2 text-sm">You can complete this with a keyboard, and skip the parts you do not need yet.</p>
    <div v-if="loading" class="text-muted-foreground mt-8 flex items-center gap-2 text-sm"><Loader2 class="size-4 animate-spin" /> Loading…</div>
    <EmptyState v-else-if="!canCreate" class="mt-8" title="Ask an admin for an invite" description="This self-hosted Aictiq accepts new members by invitation. An administrator can send one to your email address." icon="◇" />
    <form v-else-if="step === 1" class="mt-8 space-y-4" @submit.prevent="createOrganization"><label class="block text-sm font-medium" for="onboard-org">Organization name</label><Input id="onboard-org" v-model="orgName" autofocus placeholder="Acme" :aria-invalid="Boolean(errors.name)" /><p v-for="message in errors.name" :key="message" class="text-destructive text-xs">{{ message }}</p><Button type="submit" :disabled="busy || !orgName.trim()"><Loader2 v-if="busy" class="animate-spin" />Continue</Button></form>
    <form v-else-if="step === 2" class="mt-8 space-y-4" @submit.prevent="createProject"><label class="block text-sm font-medium" for="onboard-project">Project name</label><Input id="onboard-project" v-model="projectName" autofocus placeholder="Website" /><label class="block text-sm font-medium" for="onboard-key">Project key</label><Input id="onboard-key" v-model="projectKey" class="font-mono uppercase" :placeholder="key || 'WEB'" /><Button type="submit" :disabled="busy || !projectName.trim()"><Loader2 v-if="busy" class="animate-spin" />Continue</Button></form>
    <form v-else-if="step === 3" class="mt-8 space-y-4" @submit.prevent="invite"><label class="block text-sm font-medium" for="onboard-invites">Email addresses</label><textarea id="onboard-invites" v-model="emails" rows="4" class="border-border bg-background w-full rounded-lg border p-2 text-sm" placeholder="ada@example.com, grace@example.com" /><div class="flex gap-2"><Button type="submit" :disabled="busy || !emails.trim()">Send invitations</Button><Button type="button" variant="ghost" @click="step = 4">Skip for now</Button></div></form>
    <!-- Handing work to an agent takes an agent account, a runner, a playbook and a
         repository, which is more than a step in a wizard: Get started is that walkthrough.
         A personal token for your own CLI or MCP client is a separate, optional thing, so
         it sits below and says so. -->
    <section v-else class="mt-8 space-y-6">
      <div class="space-y-3">
        <p class="text-sm">
          Handing an item to an agent needs an agent account, a runner on a machine you
          trust, a playbook and a repository. Get started walks through each one and checks
          it off when it is really done — you can leave and come back to it any time.
        </p>
        <div class="flex flex-wrap gap-2">
          <Button type="button" @click="finishIntoChecklist">Open Get started</Button>
          <Button type="button" variant="ghost" @click="finish">Skip for now</Button>
        </div>
      </div>

      <div class="border-border space-y-3 border-t pt-4">
        <h2 class="text-sm font-medium">Or connect your own CLI or MCP client</h2>
        <p class="text-muted-foreground text-sm">
          A personal access token lets your terminal or an MCP client act as you. It is not
          a runner credential — a runner gets its own secret from Factory → Runners.
        </p>
        <template v-if="token">
          <code class="bg-muted block rounded p-3 text-xs break-all">{{ token.secret }}</code>
          <code class="bg-muted block rounded p-3 text-xs">aictiq mcp --token $AICTIQ_TOKEN</code>
        </template>
        <Button v-else type="button" variant="secondary" size="sm" :disabled="busy" @click="connectAgent">
          Create a personal token
        </Button>
      </div>
    </section>
  </main></AppShell>
</template>
