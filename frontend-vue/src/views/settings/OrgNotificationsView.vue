<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import {
  connectOrgChatChannel,
  disconnectOrgChatChannel,
  getOrgNotificationDefaults,
  listOrgChatChannels,
  putOrgNotificationDefaults,
  testOrgChatChannel,
  updateOrgChatChannel,
  type ChatChannelConnect,
  type ChatChannelType,
  type NotificationKind,
  type NotificationMode,
  type OrgChatChannel,
  type OrgChatChannelList,
  type OrgNotificationDefault,
} from '@/api/notifications'
import { hasOrgRole } from '@/api/organizations'
import ChatChannelStatusBadge from '@/components/settings/ChatChannelStatusBadge.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import TelegramConnectCode from '@/components/settings/TelegramConnectCode.vue'
import { Button } from '@/components/ui/button'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { ValidationError } from '@/utils/api'
import {
  chatChannelLabels,
  chatChannelTypes,
  notificationKindLabel,
  notificationKinds,
  notificationModeLabels,
  testFailureMessage,
  webhookUrlPrefixes,
} from '@/utils/notifications'

/**
 * What the organization says about notifications as a whole: chat channels everyone's
 * updates go to (a team's Slack channel, a Telegram group), and the defaults a member gets
 * until they choose for themselves. Both are admin decisions, so the tab says so to
 * everyone else instead of loading what the API would refuse.
 */
const org = useOrgScope()
const toast = useToast()
const slug = () => org.slug.value
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))

const modes = Object.keys(notificationModeLabels) as NotificationMode[]
type DefaultColumn = Exclude<keyof OrgNotificationDefault, 'kind'>

// ── Shared channels ─────────────────────────────────────────────────────────────────
const list = ref<OrgChatChannelList | null>(null)
const busy = ref<string | null>(null)
/** Codes are shown once, by the answer that created them; a reload cannot fetch them again. */
const codes = ref<Record<string, ChatChannelConnect<OrgChatChannel>>>({})
const testResults = ref<Record<string, { ok: boolean; message: string }>>({})
const draft = ref({ type: 'slack' as ChatChannelType, name: '', webhookUrl: '' })
const urlError = ref<string | null>(null)
const offeredTypes = computed(() =>
  chatChannelTypes.filter((type) => type !== 'telegram' || list.value?.telegramAvailable),
)

// A Telegram group connects when someone sends the bot its code from Telegram itself, so
// the list is re-read every few seconds while any channel is still waiting.
let poll: ReturnType<typeof setTimeout> | undefined
async function loadChannels() {
  clearTimeout(poll)
  try {
    list.value = await listOrgChatChannels(slug())
  } catch (error) {
    toast.error(error, 'Shared channels could not be loaded.')
    return
  }
  if (list.value.channels.some((channel) => channel.status === 'pending')) {
    poll = setTimeout(loadChannels, 3000)
  }
}

function replace(channel: OrgChatChannel) {
  if (!list.value) return
  list.value = {
    ...list.value,
    channels: list.value.channels.map((c) => (c.id === channel.id ? channel : c)),
  }
}

async function add() {
  const { type, name, webhookUrl } = draft.value
  busy.value = 'add'
  urlError.value = null
  try {
    const made = await connectOrgChatChannel(slug(), {
      type,
      name: name.trim(),
      webhookUrl: type === 'telegram' ? undefined : webhookUrl.trim(),
    })
    if (made.connectCode) codes.value = { ...codes.value, [made.channel.id]: made }
    else toast.success(`${chatChannelLabels[type]} channel connected.`)
    draft.value = { type, name: '', webhookUrl: '' }
    await loadChannels()
  } catch (error) {
    if (error instanceof ValidationError && error.fieldErrors.webhookUrl?.length) {
      urlError.value = error.fieldErrors.webhookUrl[0]!
    } else toast.error(error, 'The channel could not be added.')
  } finally {
    busy.value = null
  }
}

/** Absent from `modes` means the channel does not carry that kind. */
const modeOf = (channel: OrgChatChannel, kind: NotificationKind) => channel.modes[kind] ?? 'off'

async function setMode(channel: OrgChatChannel, kind: NotificationKind, mode: NotificationMode) {
  busy.value = channel.id
  try {
    replace(
      await updateOrgChatChannel(slug(), channel.id, { modes: { ...channel.modes, [kind]: mode } }),
    )
    toast.saved(`${channel.name} updated.`)
  } catch (error) {
    toast.saveFailed(error, `${channel.name} could not be updated.`)
    await loadChannels()
  } finally {
    busy.value = null
  }
}

async function sendTest(channel: OrgChatChannel) {
  busy.value = channel.id
  try {
    await testOrgChatChannel(slug(), channel.id)
    testResults.value = {
      ...testResults.value,
      [channel.id]: { ok: true, message: 'Test message sent.' },
    }
  } catch (error) {
    testResults.value = {
      ...testResults.value,
      [channel.id]: { ok: false, message: testFailureMessage(error) },
    }
  } finally {
    busy.value = null
  }
}

async function disconnect(channel: OrgChatChannel) {
  if (!window.confirm(`Disconnect ${channel.name}? Organization updates stop going there.`)) return
  busy.value = channel.id
  try {
    await disconnectOrgChatChannel(slug(), channel.id)
    await loadChannels()
  } catch (error) {
    toast.error(error, `${channel.name} could not be disconnected.`)
  } finally {
    busy.value = null
  }
}

// ── Member defaults ─────────────────────────────────────────────────────────────────
const defaults = ref<OrgNotificationDefault[]>([])
const savingDefaults = ref(false)
const defaultColumns = computed(() =>
  (['email', ...offeredTypes.value] as DefaultColumn[]).map((column) => ({
    column,
    label: column === 'email' ? 'Email' : chatChannelLabels[column],
  })),
)

function fill(saved: OrgNotificationDefault[]) {
  defaults.value = notificationKinds.map(
    (kind) =>
      saved.find((row) => row.kind === kind) ?? {
        kind,
        email: null,
        telegram: null,
        slack: null,
        discord: null,
      },
  )
}

async function saveDefaults() {
  savingDefaults.value = true
  try {
    // A row with nothing set is the same as no row, so only the decisions are sent.
    const chosen = defaults.value.filter(
      (row) => row.email || row.telegram || row.slack || row.discord,
    )
    fill(await putOrgNotificationDefaults(slug(), chosen))
    toast.saved('Member defaults saved.')
  } catch (error) {
    toast.saveFailed(error, 'Member defaults could not be saved.')
  } finally {
    savingDefaults.value = false
  }
}

async function load() {
  if (!mayManage.value) return
  codes.value = {}
  testResults.value = {}
  await Promise.all([
    loadChannels(),
    getOrgNotificationDefaults(slug()).then(fill, (error) =>
      toast.error(error, 'Member defaults could not be loaded.'),
    ),
  ])
}

watch(() => org.slug.value, load, { immediate: true })
onBeforeUnmount(() => clearTimeout(poll))
</script>

<template>
  <p v-if="!mayManage" class="text-muted-foreground text-sm">
    Only organization owners and admins can manage shared notification channels and member defaults.
    Your own notifications are in your personal settings.
  </p>
  <div v-else class="space-y-10">
    <SettingsSection
      title="Shared channels"
      description="Post organization updates to a team chat everyone can see."
      wide
    >
      <form class="flex max-w-2xl flex-wrap items-start gap-2" @submit.prevent="add">
        <select
          v-model="draft.type"
          aria-label="Channel type"
          class="h-8 rounded border px-2 text-sm"
          @change="urlError = null"
        >
          <option v-for="type in offeredTypes" :key="type" :value="type">
            {{ chatChannelLabels[type] }}
          </option>
        </select>
        <input
          v-model="draft.name"
          class="h-8 w-40 rounded border px-2 text-sm"
          required
          aria-label="Channel name"
          placeholder="#releases"
        />
        <div v-if="draft.type !== 'telegram'" class="min-w-60 flex-1">
          <input
            v-model="draft.webhookUrl"
            class="h-8 w-full rounded border px-2 text-sm"
            type="url"
            required
            aria-label="Webhook URL"
            :placeholder="`${webhookUrlPrefixes[draft.type]}…`"
          />
          <p v-if="urlError" class="text-destructive mt-1 text-xs">{{ urlError }}</p>
          <p v-else class="text-muted-foreground mt-1 text-xs">
            An incoming webhook URL starting with {{ webhookUrlPrefixes[draft.type] }}
          </p>
        </div>
        <Button size="sm" :disabled="busy === 'add'">Add channel</Button>
      </form>

      <p v-if="list && !list.channels.length" class="text-muted-foreground mt-4 text-sm">
        No shared channels yet.
      </p>
      <div class="mt-4 grid gap-3">
        <article
          v-for="channel in list?.channels ?? []"
          :key="channel.id"
          class="rounded border p-3 text-sm"
          :data-testid="`org-channel-${channel.id}`"
        >
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div class="min-w-0">
              <p class="font-medium">{{ channel.name }}</p>
              <p class="text-muted-foreground truncate text-xs">
                {{ chatChannelLabels[channel.type]
                }}<template v-if="channel.target"> · {{ channel.target }}</template>
              </p>
            </div>
            <span class="flex items-center gap-2">
              <ChatChannelStatusBadge :status="channel.status" />
              <Button
                v-if="channel.status !== 'pending'"
                size="sm"
                variant="secondary"
                :disabled="busy === channel.id"
                @click="sendTest(channel)"
                >Send test</Button
              >
              <Button
                size="sm"
                variant="ghost"
                :disabled="busy === channel.id"
                @click="disconnect(channel)"
                >Disconnect</Button
              >
            </span>
          </div>
          <p
            v-if="channel.status === 'broken' && channel.lastError"
            class="text-destructive mt-2 text-xs"
          >
            {{ channel.lastError }}
          </p>
          <p
            v-if="testResults[channel.id]"
            class="mt-2 text-xs"
            :class="testResults[channel.id]!.ok ? 'text-muted-foreground' : 'text-destructive'"
            role="status"
          >
            {{ testResults[channel.id]!.message }}
          </p>
          <template v-if="channel.status === 'pending'">
            <TelegramConnectCode
              v-if="codes[channel.id]?.connectCode"
              class="mt-3"
              group
              :code="codes[channel.id]!.connectCode!"
              :url="codes[channel.id]!.connectUrl"
              :expires-at="codes[channel.id]!.expiresAt"
              :bot-username="list?.telegramBotUsername ?? null"
            />
            <p v-else class="text-muted-foreground mt-2 text-xs">
              Waiting for the connect code. Lost it? Disconnect this channel and add it again.
            </p>
          </template>
          <div v-else class="mt-3 grid grid-cols-[auto_auto] justify-start gap-x-4 gap-y-1">
            <template v-for="kind in list?.kinds ?? []" :key="kind">
              <span class="self-center">{{ notificationKindLabel(kind) }}</span>
              <select
                :value="modeOf(channel, kind)"
                class="rounded border px-1"
                :aria-label="`${notificationKindLabel(kind)} on ${channel.name}`"
                :disabled="busy === channel.id"
                @change="
                  setMode(
                    channel,
                    kind,
                    ($event.target as HTMLSelectElement).value as NotificationMode,
                  )
                "
              >
                <option v-for="mode in modes" :key="mode" :value="mode">
                  {{ notificationModeLabels[mode] }}
                </option>
              </select>
            </template>
          </div>
        </article>
      </div>
    </SettingsSection>

    <SettingsSection
      title="Member defaults"
      description="Applies to members who have not chosen a setting themselves. A member's own setting always wins."
      wide
    >
      <div class="overflow-x-auto">
        <table class="text-sm">
          <thead>
            <tr class="text-muted-foreground border-b text-left text-xs">
              <th class="py-2 pr-6 font-medium">Update</th>
              <th
                v-for="{ column, label } in defaultColumns"
                :key="column"
                class="py-2 pr-4 font-medium"
              >
                {{ label }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in defaults" :key="row.kind" class="border-b">
              <td class="py-2 pr-6">{{ notificationKindLabel(row.kind) }}</td>
              <td v-for="{ column, label } in defaultColumns" :key="column" class="py-2 pr-4">
                <select
                  v-model="row[column]"
                  class="rounded border px-1"
                  :aria-label="`${notificationKindLabel(row.kind)} by ${label}`"
                >
                  <option :value="null">Not set</option>
                  <option v-for="mode in modes" :key="mode" :value="mode">
                    {{ notificationModeLabels[mode] }}
                  </option>
                </select>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <Button class="mt-4" size="sm" :disabled="savingDefaults" @click="saveDefaults">
        Save defaults
      </Button>
    </SettingsSection>
  </div>
</template>
