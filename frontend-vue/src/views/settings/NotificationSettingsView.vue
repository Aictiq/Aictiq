<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import {
  connectChatChannel,
  disconnectChatChannel,
  getNotificationPreferences,
  listChatChannels,
  putNotificationPreferences,
  testChatChannel,
  type ChatChannel,
  type ChatChannelConnect,
  type ChatChannelList,
  type ChatChannelType,
  type NotificationMode,
  type NotificationPreference,
} from '@/api/notifications'
import ChatChannelStatusBadge from '@/components/settings/ChatChannelStatusBadge.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import TelegramConnectCode from '@/components/settings/TelegramConnectCode.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import { ValidationError } from '@/utils/api'
import {
  chatChannelLabels,
  chatChannelTypes,
  notificationKindLabel,
  notificationKinds as kinds,
  testFailureMessage,
  webhookUrlPrefixes,
  type WebhookChannelType,
} from '@/utils/notifications'
const toast = useToast()
const saving = ref(false)
const preferences = ref<NotificationPreference[]>([])
// Comments on watched items reach the inbox; only mentions and replies are mailed as they
// happen, so "commented" offers the digest or nothing - on every channel alike.
const inboxOnly = new Set(['commented'])
const narrow = (kind: string, mode: NotificationMode) =>
  inboxOnly.has(kind) && mode === 'immediate' ? ('off' as const) : mode
const matrix = computed(() => kinds.map((kind): NotificationPreference => {
  const saved = preferences.value.find(p => p.kind === kind)
  const row = saved ?? { kind, inApp: true, email: 'immediate', telegram: null, slack: null, discord: null }
  return {
    ...row,
    email: narrow(kind, row.email),
    telegram: row.telegram && narrow(kind, row.telegram),
    slack: row.slack && narrow(kind, row.slack),
    discord: row.discord && narrow(kind, row.discord),
  }
}))
async function save() {
  saving.value = true
  try {
    preferences.value = await putNotificationPreferences(preferences.value)
    toast.saved('Notification preferences saved.')
  } catch (error) {
    toast.saveFailed(error, 'Notification preferences could not be saved.')
  } finally {
    saving.value = false
  }
}

// ── Chat channels ───────────────────────────────────────────────────────────────────
const channelList = ref<ChatChannelList | null>(null)
const busy = ref<ChatChannelType | null>(null)
const webhookUrls = reactive({ slack: '', discord: '' })
const urlErrors = ref<Partial<Record<ChatChannelType, string>>>({})
const telegramCode = ref<ChatChannelConnect | null>(null)
const testResults = ref<Record<string, { ok: boolean; message: string }>>({})
const channelOf = (type: ChatChannelType) => channelList.value?.channels.find(c => c.type === type)
/** Telegram needs a bot the server was configured with; the webhook channels never do. */
const offeredTypes = computed(() =>
  chatChannelTypes.filter(type => type !== 'telegram' || channelList.value?.telegramAvailable))
/** A channel still waiting for its code has nowhere to deliver to, so it gets no column yet. */
const columns = computed(() =>
  offeredTypes.value.filter(type => { const status = channelOf(type)?.status; return status === 'active' || status === 'broken' }))

// A pending Telegram channel turns active the moment someone sends the bot its code, in
// another app entirely - so the list is re-read every few seconds until it does.
let poll: ReturnType<typeof setTimeout> | undefined
async function loadChannels() {
  clearTimeout(poll)
  try {
    channelList.value = await listChatChannels()
  } catch (error) {
    toast.error(error, 'Chat channels could not be loaded.')
    return
  }
  if (channelOf('telegram')?.status !== 'pending') telegramCode.value = null
  if (channelList.value.channels.some(c => c.status === 'pending')) poll = setTimeout(loadChannels, 3000)
}
async function connect(type: ChatChannelType) {
  busy.value = type
  urlErrors.value = { ...urlErrors.value, [type]: undefined }
  try {
    const made = await connectChatChannel(type === 'telegram' ? { type } : { type, webhookUrl: webhookUrls[type].trim() })
    if (type === 'telegram') telegramCode.value = made
    else {
      webhookUrls[type] = ''
      toast.success(`${chatChannelLabels[type]} connected.`)
    }
    await loadChannels()
  } catch (error) {
    if (error instanceof ValidationError && error.fieldErrors.webhookUrl?.length)
      urlErrors.value = { ...urlErrors.value, [type]: error.fieldErrors.webhookUrl[0] }
    else toast.error(error, `${chatChannelLabels[type]} could not be connected.`)
  } finally {
    busy.value = null
  }
}
async function sendTest(channel: ChatChannel) {
  busy.value = channel.type
  try {
    await testChatChannel(channel.id)
    testResults.value = { ...testResults.value, [channel.id]: { ok: true, message: 'Test message sent.' } }
  } catch (error) {
    testResults.value = { ...testResults.value, [channel.id]: { ok: false, message: testFailureMessage(error) } }
  } finally {
    busy.value = null
  }
  // A test that gets through makes a broken channel active again, so show its new status.
  await loadChannels()
}
async function disconnect(channel: ChatChannel) {
  if (!window.confirm(`Disconnect ${chatChannelLabels[channel.type]}? Notifications stop going there.`)) return
  busy.value = channel.type
  try {
    await disconnectChatChannel(channel.id)
    await loadChannels()
  } catch (error) {
    toast.error(error, `${chatChannelLabels[channel.type]} could not be disconnected.`)
  } finally {
    busy.value = null
  }
}
onMounted(async () => {
  await Promise.all([
    getNotificationPreferences().then(saved => { preferences.value = saved }),
    loadChannels(),
  ])
})
onBeforeUnmount(() => clearTimeout(poll))
</script>

<template>
  <div class="space-y-8">
    <SettingsSection title="Channels" description="Connect a chat app to get notifications where you already talk.">
      <div class="grid gap-3">
        <article v-for="type in offeredTypes" :key="type" class="rounded border p-3 text-sm" :data-testid="`channel-${type}`">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div class="min-w-0">
              <p class="font-medium">{{ chatChannelLabels[type] }}</p>
              <p class="text-muted-foreground truncate text-xs">{{ channelOf(type)?.target ?? 'Not connected' }}</p>
            </div>
            <span v-if="channelOf(type)" class="flex items-center gap-2">
              <ChatChannelStatusBadge :status="channelOf(type)!.status" />
              <Button v-if="channelOf(type)!.status !== 'pending'" size="sm" variant="secondary" :disabled="busy === type" @click="sendTest(channelOf(type)!)">Send test</Button>
              <Button size="sm" variant="ghost" :disabled="busy === type" @click="disconnect(channelOf(type)!)">Disconnect</Button>
            </span>
          </div>
          <p v-if="channelOf(type)?.status === 'broken' && channelOf(type)?.lastError" class="text-destructive mt-2 text-xs">{{ channelOf(type)!.lastError }}</p>
          <p v-if="channelOf(type) && testResults[channelOf(type)!.id]" class="mt-2 text-xs" :class="testResults[channelOf(type)!.id]!.ok ? 'text-muted-foreground' : 'text-destructive'" role="status">{{ testResults[channelOf(type)!.id]!.message }}</p>
          <template v-if="type === 'telegram'">
            <TelegramConnectCode v-if="telegramCode?.connectCode && channelOf(type)?.status === 'pending'" class="mt-3" :code="telegramCode.connectCode" :url="telegramCode.connectUrl" :expires-at="telegramCode.expiresAt" :bot-username="channelList?.telegramBotUsername ?? null" />
            <div v-else-if="channelOf(type)?.status !== 'active'" class="mt-3 flex items-center gap-3">
              <Button size="sm" :disabled="busy === type" @click="connect(type)">{{ channelOf(type) ? 'Get a new code' : 'Connect' }}</Button>
              <span class="text-muted-foreground text-xs">You get a one-time code to send to the bot.</span>
            </div>
          </template>
          <form v-else-if="channelOf(type)?.status !== 'active'" class="mt-3" @submit.prevent="connect(type)">
            <div class="flex gap-2">
              <input v-model="webhookUrls[type as WebhookChannelType]" class="h-8 flex-1 rounded border px-2 text-sm" type="url" required :aria-label="`${chatChannelLabels[type]} webhook URL`" :placeholder="`${webhookUrlPrefixes[type as WebhookChannelType]}…`" />
              <Button size="sm" :disabled="busy === type">{{ channelOf(type) ? 'Reconnect' : 'Connect' }}</Button>
            </div>
            <p v-if="urlErrors[type]" class="text-destructive mt-1 text-xs">{{ urlErrors[type] }}</p>
            <p v-else class="text-muted-foreground mt-1 text-xs">An incoming webhook URL starting with {{ webhookUrlPrefixes[type as WebhookChannelType] }}</p>
          </form>
        </article>
      </div>
    </SettingsSection>

    <SettingsSection title="Notifications" description="Choose where Aictiq sends work updates." wide>
      <div class="overflow-x-auto">
        <table class="text-sm">
          <thead>
            <tr class="text-muted-foreground border-b text-left text-xs">
              <th class="py-2 pr-6 font-medium">Update</th>
              <th class="py-2 pr-4 font-medium">Inbox</th>
              <th class="py-2 pr-4 font-medium">Email</th>
              <th v-for="type in columns" :key="type" class="py-2 pr-4 font-medium" title="Default follows your organization's default for this update, or your email setting when it has none.">{{ chatChannelLabels[type] }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="preference in matrix" :key="preference.kind" class="border-b">
              <td class="py-2 pr-6">{{ notificationKindLabel(preference.kind) }}</td>
              <td class="py-2 pr-4"><input v-model="preference.inApp" type="checkbox" :aria-label="`${notificationKindLabel(preference.kind)} in Inbox`" @change="preferences = matrix" /></td>
              <td class="py-2 pr-4"><select v-model="preference.email" class="border rounded px-1" :aria-label="`${notificationKindLabel(preference.kind)} by email`" @change="preferences = matrix"><option value="off">Off</option><option v-if="!inboxOnly.has(preference.kind)" value="immediate">Immediate</option><option value="digest">Daily digest</option></select></td>
              <td v-for="type in columns" :key="type" class="py-2 pr-4"><select v-model="preference[type]" class="border rounded px-1" :aria-label="`${notificationKindLabel(preference.kind)} on ${chatChannelLabels[type]}`" @change="preferences = matrix"><option :value="null">Default</option><option value="off">Off</option><option v-if="!inboxOnly.has(preference.kind)" value="immediate">Immediate</option><option value="digest">Daily digest</option></select></td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-if="columns.length" class="text-muted-foreground mt-2 text-xs">
        Default uses your organization's default for that update; when it has none, the chat app follows your email setting.
      </p>
      <button class="mt-4 rounded border px-3 py-2 text-sm disabled:opacity-50" :disabled="saving" @click="save">Save preferences</button>
    </SettingsSection>
  </div>
</template>
