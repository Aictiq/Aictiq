<script setup lang="ts">
import { Check, Copy, Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import {
  getFleetSettings,
  revokeFleetEnrollToken,
  rotateFleetEnrollToken,
  rotateFleetWebhookSecret,
  updateFleetSettings,
  type FleetSettings,
  type UpdateFleetBody,
} from '@/api/fleet'
import { hasOrgRole } from '@/api/organizations'
import EmptyState from '@/components/common/EmptyState.vue'
import CommandBlock from '@/components/factory/CommandBlock.vue'
import FleetDowngradedNotice from '@/components/factory/FleetDowngradedNotice.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { loadFleetStatus } from '@/composables/useFleetStatus'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { planLimitMessage } from '@/lib/billing'
import { since } from '@/lib/claims'
import { durationLabel, fleetEnrollCommand } from '@/lib/fleet'
import { orgSettingsPath } from '@/router/paths'
import { ApiError } from '@/utils/api'

/**
 * Fleet mode, an Admin's switch: runner tags, required tags on rules, playbooks and Start
 * run, least-loaded dispatch, and scale from zero through the organization's own webhook.
 *
 * Only paid hosted plans can turn it on. Self-hosted instances answer 404 here and the tab is
 * not even in the nav; self-hosted support may come later, and then this page needs nothing
 * but the server to say `available`.
 *
 * Two secrets live here, and like a runner's each is shown exactly once: the webhook's signing
 * secret (minted with the first URL saved, or rotated) and the enrollment token ephemeral
 * runners use. Both leave memory when the page does.
 */
const org = useOrgScope()
const toast = useToast()

const slug = computed(() => org.slug.value)
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))

const settings = ref<FleetSettings | null>(null)
const loading = ref(true)
/** 404: this instance does not offer fleet mode. */
const unavailable = ref(false)

const toggling = ref(false)
/** The server's reason for refusing to turn it on (402), said under the switch. */
const refused = ref<string | null>(null)

type Unit = 's' | 'min'
const webhookUrl = ref('')
const scaleAfter = ref(2)
const scaleAfterUnit = ref<Unit>('min')
const cooldown = ref(5)
const cooldownUnit = ref<Unit>('min')
const savingScale = ref(false)
const scaleErrors = ref<Record<string, string[]>>({})

/** Shown once, then gone with the page. */
const webhookSecret = ref<string | null>(null)
const enrollToken = ref<string | null>(null)
const busy = ref<'rotate-secret' | 'mint-token' | 'revoke-token' | null>(null)
const copied = ref<'secret' | 'token' | null>(null)

const origin = typeof window === 'undefined' ? '' : window.location.origin
const enrollCommand = computed(() => fleetEnrollCommand(origin, enrollToken.value ?? '<token>'))

/** Whole minutes read as minutes; anything else stays in seconds, so nothing is rounded away. */
function split(seconds: number): { value: number; unit: Unit } {
  return seconds % 60 === 0 ? { value: seconds / 60, unit: 'min' } : { value: seconds, unit: 's' }
}
const toSeconds = (value: number, unit: Unit) => Math.round(unit === 'min' ? value * 60 : value)

function fill(next: FleetSettings) {
  settings.value = next
  webhookUrl.value = next.scaleWebhookUrl ?? ''
  ;({ value: scaleAfter.value, unit: scaleAfterUnit.value } = split(next.scaleAfterSeconds))
  ;({ value: cooldown.value, unit: cooldownUnit.value } = split(next.scaleCooldownSeconds))
}

async function load() {
  if (!mayManage.value) {
    loading.value = false
    return
  }
  loading.value = true
  unavailable.value = false
  try {
    fill(await getFleetSettings(slug.value))
  } catch (error) {
    settings.value = null
    if (error instanceof ApiError && error.status === 404) unavailable.value = true
    else toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([slug, mayManage], load, { immediate: true })

/** The other screens read the shared status; after a change here it must be read again. */
const refreshStatus = () => void loadFleetStatus(slug.value, true)

/** Turning it off is always allowed - a downgraded organization must be able to. */
const switchDisabled = computed(
  () =>
    toggling.value ||
    !settings.value ||
    (!settings.value.enabled && settings.value.availability !== 'available'),
)

async function toggle() {
  const current = settings.value
  if (!current || switchDisabled.value) return
  toggling.value = true
  refused.value = null
  try {
    const saved = await updateFleetSettings(slug.value, { enabled: !current.enabled })
    fill(saved.settings)
    refreshStatus()
    toast.saved(saved.settings.enabled ? 'Fleet mode is on.' : 'Fleet mode is off.')
  } catch (error) {
    const message = planLimitMessage(error)
    if (message) refused.value = message
    else toast.error(error)
  } finally {
    toggling.value = false
  }
}

const scaleAfterSeconds = computed(() => toSeconds(scaleAfter.value, scaleAfterUnit.value))
const cooldownSeconds = computed(() => toSeconds(cooldown.value, cooldownUnit.value))

async function saveScale() {
  const current = settings.value
  if (!current) return
  savingScale.value = true
  scaleErrors.value = {}
  const body: UpdateFleetBody = {
    scaleWebhookUrl: webhookUrl.value.trim(),
    scaleAfterSeconds: scaleAfterSeconds.value,
    scaleCooldownSeconds: cooldownSeconds.value,
  }
  try {
    const saved = await updateFleetSettings(slug.value, body)
    fill(saved.settings)
    if (saved.webhookSecret) webhookSecret.value = saved.webhookSecret
    toast.saved(
      saved.settings.scaleWebhookUrl ? 'Scale from zero saved.' : 'Scale from zero is off.',
    )
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      scaleErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    savingScale.value = false
  }
}

async function rotateSecret() {
  busy.value = 'rotate-secret'
  try {
    const issued = await rotateFleetWebhookSecret(slug.value)
    webhookSecret.value = issued.secret
    copied.value = null
    if (settings.value) settings.value = { ...settings.value, hasWebhookSecret: true }
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = null
  }
}

async function mintToken() {
  busy.value = 'mint-token'
  try {
    const issued = await rotateFleetEnrollToken(slug.value)
    enrollToken.value = issued.secret
    copied.value = null
    if (settings.value) settings.value = { ...settings.value, enrollTokenDisplay: issued.display }
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = null
  }
}

async function revokeToken() {
  busy.value = 'revoke-token'
  try {
    await revokeFleetEnrollToken(slug.value)
    enrollToken.value = null
    if (settings.value) settings.value = { ...settings.value, enrollTokenDisplay: null }
    toast.success('Enrollment token revoked. Runners already enrolled keep working.')
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = null
  }
}

async function copy(what: 'secret' | 'token') {
  const value = what === 'secret' ? webhookSecret.value : enrollToken.value
  if (!value) return
  try {
    await navigator.clipboard.writeText(value)
    copied.value = what
  } catch {
    toast.error(new Error('Could not copy - select the text and copy it manually.'))
  }
}

const selectClass =
  'border-border bg-background focus-visible:ring-ring h-8 rounded-lg border px-2 text-sm focus-visible:ring-2 focus-visible:outline-none'
</script>

<template>
  <EmptyState
    v-if="!mayManage"
    title="Fleet mode is managed by Admins"
    description="It decides which machines take which runs, so only organization Owners and Admins can change it."
    icon="◇"
  />

  <UiPageState v-else-if="loading" state="loading" />

  <EmptyState
    v-else-if="unavailable || !settings"
    title="Fleet mode is not available here"
    description="This instance does not offer fleet mode."
    icon="◇"
  />

  <div v-else class="space-y-10">
    <SettingsSection
      title="Fleet mode"
      description="For organizations that run many runners as one pool."
    >
      <div class="border-border space-y-3 rounded-lg border p-4">
        <div class="flex items-start justify-between gap-4">
          <ul class="text-muted-foreground list-disc space-y-1 pl-4 text-xs">
            <li>
              Runners carry tags: the ones you give them here, the ones they start with (<code
                class="font-mono"
                >--tag</code
              >), and their <code class="font-mono">os:</code> and
              <code class="font-mono">arch:</code>.
            </li>
            <li>
              Rules, playbooks and Start run can require tags; a run only goes to a runner that has
              every one of them.
            </li>
            <li>A run any runner may take goes to the least loaded one.</li>
            <li>
              Scale from zero calls your webhook when runs wait, so your own infrastructure can
              start an ephemeral runner.
            </li>
          </ul>
          <div class="flex shrink-0 items-center gap-2">
            <label id="fleet-switch-label" for="fleet-switch" class="text-sm font-medium">
              {{ settings.enabled ? 'On' : 'Off' }}
            </label>
            <button
              id="fleet-switch"
              type="button"
              role="switch"
              :aria-checked="settings.enabled"
              aria-label="Fleet mode"
              :disabled="switchDisabled"
              data-testid="fleet-switch"
              class="focus-visible:ring-ring inline-flex h-5 w-9 shrink-0 items-center rounded-full border px-0.5 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
              :class="settings.enabled ? 'bg-primary border-primary' : 'bg-muted border-border'"
              @click="toggle"
            >
              <span
                aria-hidden="true"
                class="bg-background inline-block size-4 rounded-full shadow transition-transform"
                :class="settings.enabled ? 'translate-x-4' : 'translate-x-0'"
              />
            </button>
            <Loader2
              v-if="toggling"
              class="text-muted-foreground size-4 animate-spin"
              aria-hidden="true"
            />
          </div>
        </div>

        <p
          v-if="settings.availability === 'upgrade' && !settings.enabled"
          class="text-xs"
          data-testid="fleet-upgrade"
        >
          Fleet mode is part of the paid Hosted plan.
          <RouterLink
            :to="orgSettingsPath(slug, 'billing')"
            class="font-medium underline underline-offset-2"
            data-testid="fleet-upgrade-link"
          >
            Upgrade this organization
          </RouterLink>
          to turn it on.
        </p>
        <FleetDowngradedNotice v-if="settings.downgraded" :slug="slug" />
        <p v-if="refused" role="alert" class="text-destructive text-xs" data-testid="fleet-refused">
          {{ refused }}
        </p>
      </div>
    </SettingsSection>

    <template v-if="settings.active">
      <SettingsSection
        title="Scale from zero"
        description="When a run any runner may take has waited this long, Aictiq calls your webhook with what the waiting runs need. Your own Docker, KVM or Kubernetes starts a machine; Aictiq starts nothing itself."
      >
        <form
          class="space-y-5"
          novalidate
          data-testid="fleet-scale-form"
          @submit.prevent="saveScale"
        >
          <div class="space-y-1.5">
            <label for="fleet-webhook-url" class="text-sm font-medium">Webhook URL</label>
            <Input
              id="fleet-webhook-url"
              v-model="webhookUrl"
              type="url"
              placeholder="https://scaler.example.com/aictiq"
              data-testid="fleet-webhook-url"
              :aria-invalid="Boolean(scaleErrors.scaleWebhookUrl)"
            />
            <p class="text-muted-foreground text-xs">
              HTTPS. Leave it empty to turn scale from zero off.
            </p>
            <p
              v-for="message in scaleErrors.scaleWebhookUrl"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label for="fleet-scale-after" class="text-sm font-medium"
                >Call after a wait of</label
              >
              <div class="flex gap-2">
                <Input
                  id="fleet-scale-after"
                  v-model.number="scaleAfter"
                  type="number"
                  min="1"
                  data-testid="fleet-scale-after"
                  :aria-invalid="Boolean(scaleErrors.scaleAfterSeconds)"
                />
                <select
                  v-model="scaleAfterUnit"
                  aria-label="Wait unit"
                  :class="selectClass"
                  data-testid="fleet-scale-after-unit"
                >
                  <option value="s">seconds</option>
                  <option value="min">minutes</option>
                </select>
              </div>
              <p class="text-muted-foreground text-xs">30 seconds to 24 hours.</p>
              <p
                v-for="message in scaleErrors.scaleAfterSeconds"
                :key="message"
                class="text-destructive text-xs"
              >
                {{ message }}
              </p>
            </div>
            <div class="space-y-1.5">
              <label for="fleet-scale-cooldown" class="text-sm font-medium">
                At most once every
              </label>
              <div class="flex gap-2">
                <Input
                  id="fleet-scale-cooldown"
                  v-model.number="cooldown"
                  type="number"
                  min="1"
                  data-testid="fleet-scale-cooldown"
                  :aria-invalid="Boolean(scaleErrors.scaleCooldownSeconds)"
                />
                <select
                  v-model="cooldownUnit"
                  aria-label="Cooldown unit"
                  :class="selectClass"
                  data-testid="fleet-scale-cooldown-unit"
                >
                  <option value="s">seconds</option>
                  <option value="min">minutes</option>
                </select>
              </div>
              <p class="text-muted-foreground text-xs">1 minute to 24 hours.</p>
              <p
                v-for="message in scaleErrors.scaleCooldownSeconds"
                :key="message"
                class="text-destructive text-xs"
              >
                {{ message }}
              </p>
            </div>
          </div>

          <div
            v-if="settings.scaleWebhookUrl"
            class="text-muted-foreground space-y-1 text-xs"
            data-testid="fleet-scale-status"
          >
            <p data-testid="fleet-scale-last-call">
              <template v-if="settings.lastScaleCallAt">
                Last called
                <span :title="new Date(settings.lastScaleCallAt).toLocaleString()">{{
                  since(settings.lastScaleCallAt)
                }}</span
                >.
              </template>
              <template v-else>
                Not called yet. It is called once a run has waited
                {{ durationLabel(settings.scaleAfterSeconds) }}.
              </template>
            </p>
            <p
              v-if="settings.lastScaleCallError"
              class="text-destructive"
              data-testid="fleet-scale-last-error"
            >
              The last call failed: {{ settings.lastScaleCallError }}
            </p>
          </div>

          <div class="flex flex-wrap justify-end gap-2">
            <Button
              v-if="settings.hasWebhookSecret"
              type="button"
              variant="outline"
              :disabled="busy !== null"
              data-testid="fleet-webhook-rotate"
              @click="rotateSecret"
            >
              <Loader2 v-if="busy === 'rotate-secret'" class="animate-spin" aria-hidden="true" />
              New signing secret
            </Button>
            <Button type="submit" :disabled="savingScale" data-testid="fleet-scale-save">
              <Loader2 v-if="savingScale" class="animate-spin" aria-hidden="true" />
              Save
            </Button>
          </div>
        </form>

        <div
          v-if="webhookSecret"
          class="border-border mt-4 space-y-2 rounded-lg border p-3"
          data-testid="fleet-webhook-secret"
        >
          <p class="text-xs">
            <span class="font-medium">Signing secret.</span> Copy it now - this is the only time it
            is shown. Every call carries
            <code class="font-mono">X-Aictiq-Signature: sha256=&lt;hmac of body&gt;</code>, the
            HMAC-SHA256 of the request body with this secret; check it before starting anything.
          </p>
          <div class="flex items-center gap-2">
            <code
              class="bg-background border-border min-w-0 flex-1 truncate rounded border px-2 py-1 font-mono text-[11px]"
              data-testid="fleet-webhook-secret-value"
              >{{ webhookSecret }}</code
            >
            <Button
              variant="ghost"
              size="icon"
              aria-label="Copy the signing secret"
              @click="copy('secret')"
            >
              <Check v-if="copied === 'secret'" class="size-4" aria-hidden="true" />
              <Copy v-else class="size-4" aria-hidden="true" />
            </Button>
          </div>
        </div>
      </SettingsSection>

      <SettingsSection
        title="Ephemeral runners"
        description="A machine your webhook starts enrolls itself with this token, takes runs until it has been idle for a while, then removes itself. One token serves every such machine."
      >
        <div class="space-y-3" data-testid="fleet-enroll">
          <p class="text-muted-foreground text-xs" data-testid="fleet-enroll-current">
            <template v-if="settings.enrollTokenDisplay">
              Current token: <span class="font-mono">{{ settings.enrollTokenDisplay }}</span>
            </template>
            <template v-else>No enrollment token. Create one to enroll ephemeral runners.</template>
          </p>

          <div
            v-if="enrollToken"
            class="border-border space-y-2 rounded-lg border p-3"
            data-testid="fleet-enroll-token"
          >
            <p class="text-xs">Copy it now - this is the only time the token is shown.</p>
            <div class="flex items-center gap-2">
              <code
                class="bg-background border-border min-w-0 flex-1 truncate rounded border px-2 py-1 font-mono text-[11px]"
                data-testid="fleet-enroll-token-value"
                >{{ enrollToken }}</code
              >
              <Button
                variant="ghost"
                size="icon"
                aria-label="Copy the enrollment token"
                @click="copy('token')"
              >
                <Check v-if="copied === 'token'" class="size-4" aria-hidden="true" />
                <Copy v-else class="size-4" aria-hidden="true" />
              </Button>
            </div>
          </div>

          <div class="text-muted-foreground text-xs">
            On the new machine, with a harness signed in:
            <CommandBlock
              :command="enrollCommand"
              label="ephemeral runner command"
              data-testid="fleet-enroll-command"
            />
            <p class="mt-1">
              <code class="font-mono">--tag</code> is what required tags match; repeat it for more.
              <code class="font-mono">--idle-exit 10</code> removes the runner after 10 idle
              minutes.
            </p>
          </div>

          <div class="flex flex-wrap justify-end gap-2">
            <Button
              v-if="settings.enrollTokenDisplay"
              variant="outline"
              :disabled="busy !== null"
              data-testid="fleet-enroll-revoke"
              @click="revokeToken"
            >
              <Loader2 v-if="busy === 'revoke-token'" class="animate-spin" aria-hidden="true" />
              Revoke
            </Button>
            <Button :disabled="busy !== null" data-testid="fleet-enroll-mint" @click="mintToken">
              <Loader2 v-if="busy === 'mint-token'" class="animate-spin" aria-hidden="true" />
              {{ settings.enrollTokenDisplay ? 'Replace token' : 'Create enrollment token' }}
            </Button>
          </div>
        </div>
      </SettingsSection>
    </template>
  </div>
</template>
