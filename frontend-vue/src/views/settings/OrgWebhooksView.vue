<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import {
  disconnectGitHubInstallation,
  githubInstallUrl,
  listGitHubDeliveries,
  listGitHubInstallations,
  reprocessGitHubDelivery,
  type GitHubDelivery,
  type GitHubInstallation,
} from '@/api/github'
import {
  createWebhook,
  deleteWebhook,
  listWebhookDeliveries,
  listWebhooks,
  redeliverWebhook,
  testWebhook,
  type Webhook,
  type WebhookDelivery,
  type WebhookEvent,
} from '@/api/webhooks'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'

const route = useRoute()
const toast = useToast()
const hooks = ref<Webhook[]>([])
const deliveries = ref<WebhookDelivery[]>([])
const selected = ref<Webhook | null>(null)
const installations = ref<GitHubInstallation[]>([])
const githubDeliveries = ref<GitHubDelivery[]>([])
const githubAvailable = ref(true)
const githubBusy = ref<number | string | null>(null)
const url = ref('')
const events = ref<WebhookEvent[]>(['item.updated'])
const allEvents: WebhookEvent[] = [
  'item.created',
  'item.updated',
  'item.transitioned',
  'item.commented',
  'sprint.started',
  'sprint.completed',
  'wiki.page.updated',
  'agent.claimed',
]
const slug = () => String(route.params.slug)
async function loadGitHub() {
  try {
    ;[installations.value, githubDeliveries.value] = await Promise.all([
      listGitHubInstallations(slug()),
      listGitHubDeliveries(slug()),
    ])
    githubAvailable.value = true
  } catch (e) {
    // A deliberately unconfigured GitHub App answers 404. It is not an error a settings
    // administrator can fix from the browser, so omit the entire section in that case.
    if ((e as { status?: number }).status === 404) githubAvailable.value = false
    else toast.error(e)
  }
}
async function load() {
  try {
    hooks.value = await listWebhooks(slug())
  } catch (e) {
    toast.error(e)
  }
  await loadGitHub()
}
async function connectGitHub() {
  try {
    const { url: installUrl } = await githubInstallUrl(slug())
    window.location.assign(installUrl)
  } catch (e) {
    toast.error(e)
  }
}
async function disconnect(installation: GitHubInstallation) {
  if (!window.confirm(`Disconnect GitHub installation for ${installation.accountLogin}?`)) return
  githubBusy.value = installation.installationId
  try {
    await disconnectGitHubInstallation(slug(), installation.installationId)
    await loadGitHub()
  } catch (e) {
    toast.error(e)
  } finally {
    githubBusy.value = null
  }
}
async function reprocess(delivery: GitHubDelivery) {
  githubBusy.value = delivery.deliveryId
  try {
    await reprocessGitHubDelivery(slug(), delivery.deliveryId)
    toast.success('GitHub delivery queued for reprocessing')
    await loadGitHub()
  } catch (e) {
    toast.error(e)
  } finally {
    githubBusy.value = null
  }
}
async function add() {
  try {
    const made = await createWebhook(slug(), { url: url.value, events: events.value })
    await navigator.clipboard?.writeText(made.secret)
    toast.success('Webhook created. Its secret was copied; it will not be shown again.')
    url.value = ''
    await load()
  } catch (e) {
    toast.error(e)
  }
}
async function open(hook: Webhook) {
  selected.value = hook
  try {
    deliveries.value = await listWebhookDeliveries(slug(), hook.id)
  } catch (e) {
    toast.error(e)
  }
}
async function test(hook: Webhook) {
  try {
    await testWebhook(slug(), hook.id)
    toast.success('Test delivery queued')
    await open(hook)
  } catch (e) {
    toast.error(e)
  }
}
async function remove(hook: Webhook) {
  try {
    await deleteWebhook(slug(), hook.id)
    if (selected.value?.id === hook.id) selected.value = null
    await load()
  } catch (e) {
    toast.error(e)
  }
}
async function retry(delivery: WebhookDelivery) {
  if (!selected.value) return
  try {
    await redeliverWebhook(slug(), selected.value.id, delivery.id)
    await open(selected.value)
  } catch (e) {
    toast.error(e)
  }
}
watch(() => route.params.slug, load)
onMounted(load)
</script>

<template>
  <section class="space-y-6">
    <div v-if="githubAvailable" class="border-border rounded-lg border p-4">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 class="text-lg font-semibold">GitHub</h2>
          <p class="text-muted-foreground text-sm">
            Connect the GitHub App to bind repositories and create branches from work items.
          </p>
        </div>
        <Button @click="connectGitHub">Connect GitHub</Button>
      </div>
      <div v-if="installations.length" class="mt-4 grid gap-2">
        <article
          v-for="installation in installations"
          :key="installation.installationId"
          class="border-border flex items-center justify-between gap-3 rounded border p-3"
        >
          <div>
            <p class="font-medium">{{ installation.accountLogin }}</p>
            <p class="text-muted-foreground text-xs">
              {{ installation.accountType }} · {{ installation.status }}
            </p>
          </div>
          <div class="flex items-center gap-2">
            <span v-if="installation.status === 'suspended'" class="text-amber-600 text-xs"
              >Suspended in GitHub</span
            ><Button
              size="sm"
              variant="ghost"
              :disabled="githubBusy === installation.installationId"
              @click="disconnect(installation)"
              >Disconnect</Button
            >
          </div>
        </article>
      </div>
      <p v-else class="text-muted-foreground mt-4 text-sm">
        No GitHub App installation is connected yet.
      </p>
      <details v-if="githubDeliveries.length" class="mt-5">
        <summary class="cursor-pointer text-sm font-medium">Webhook inbox</summary>
        <div class="mt-2 divide-y">
          <div
            v-for="delivery in githubDeliveries"
            :key="delivery.deliveryId"
            class="flex items-center justify-between gap-3 py-2 text-xs"
          >
            <div>
              <code>{{ delivery.eventType }}</code> · {{ delivery.status
              }}<span class="text-muted-foreground">
                · {{ new Date(delivery.receivedAt).toLocaleString() }}</span
              >
              <p v-if="delivery.lastError" class="text-destructive mt-1">
                {{ delivery.lastError }}
              </p>
            </div>
            <Button
              size="xs"
              variant="ghost"
              :disabled="githubBusy === delivery.deliveryId"
              @click="reprocess(delivery)"
              >Reprocess</Button
            >
          </div>
        </div>
      </details>
    </div>
    <div>
      <h2 class="text-lg font-semibold">Outgoing webhooks</h2>
      <p class="text-sm text-muted-foreground">
        Send signed project events to Zapier, n8n, or your own service.
      </p>
    </div>
    <form class="flex gap-2" @submit.prevent="add">
      <input
        v-model="url"
        class="h-9 flex-1 rounded border px-3"
        type="url"
        required
        placeholder="https://receiver.example/aictiq"
      /><Button>Create</Button>
    </form>
    <div class="flex flex-wrap gap-3 text-sm">
      <label v-for="event in allEvents" :key="event"
        ><input v-model="events" type="checkbox" :value="event" class="mr-1" />{{ event }}</label
      >
    </div>
    <div v-for="hook in hooks" :key="hook.id" class="flex items-center gap-3 rounded border p-3">
      <code class="flex-1 truncate">{{ hook.url }}</code
      ><span class="text-xs"
        >{{ hook.active ? 'active' : 'disabled' }} · {{ hook.consecutiveFailures }} failures</span
      ><Button variant="secondary" size="sm" @click="open(hook)">Log</Button
      ><Button variant="secondary" size="sm" @click="test(hook)">Test</Button
      ><Button variant="destructive" size="sm" @click="remove(hook)">Delete</Button>
    </div>
    <aside v-if="selected" class="rounded border p-4">
      <div class="mb-3 flex justify-between">
        <h3 class="font-medium">Delivery log</h3>
        <Button variant="secondary" size="sm" @click="selected = null">Close</Button>
      </div>
      <div v-for="delivery in deliveries" :key="delivery.id" class="border-t py-2 text-sm">
        <div>
          {{ delivery.event }} · {{ delivery.status }} · attempt {{ delivery.attempt }}
          <Button
            v-if="delivery.status !== 'delivered'"
            size="sm"
            variant="link"
            @click="retry(delivery)"
            >Redeliver</Button
          >
        </div>
        <pre
          v-if="delivery.responseExcerpt"
          class="mt-1 whitespace-pre-wrap text-xs text-muted-foreground"
          >{{ delivery.responseExcerpt }}</pre>
        <p v-if="delivery.lastError" class="text-xs text-destructive">{{ delivery.lastError }}</p>
      </div>
    </aside>
  </section>
</template>
