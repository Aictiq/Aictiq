<script setup lang="ts">
import { Loader2, ShieldCheck } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { renderSVG } from 'uqr'

import SettingsSection from '@/components/settings/SettingsSection.vue'
import { Button } from '@/components/ui/button'
import { Input, PasswordInput } from '@/components/ui/input'
import {
  disableTwoFactor,
  enableTwoFactor,
  getTwoFactor,
  regenerateRecoveryCodes,
  startTwoFactorSetup,
  type TwoFactorSetup,
  type TwoFactorStatus,
} from '@/api/security'
import { useToast } from '@/composables/useToast'
import { ApiError } from '@/utils/api'

/**
 * Two-factor authentication with an authenticator app. Off until someone turns it on here:
 * nothing at registration asks for it.
 *
 * One section, several moments - set up (QR code and a confirming code), the recovery codes
 * shown once, and the on state with its two actions. Turning it off and replacing the codes
 * both ask for the password again (or a code, for an account without one), because a
 * session left open on a shared computer should not be enough to undo a second factor.
 */
const props = defineProps<{ hasPassword: boolean }>()

const toast = useToast()

const status = ref<TwoFactorStatus | null>(null)
const setup = ref<TwoFactorSetup | null>(null)
const setupCode = ref('')
const setupErrors = ref<Record<string, string[]>>({})
/** Shown once, straight after they are minted. Nothing can fetch them again. */
const codes = ref<string[] | null>(null)
/** Which action the proof form below is confirming, if any. */
const confirming = ref<'disable' | 'regenerate' | null>(null)
const proofPassword = ref('')
const proofCode = ref('')
const proofErrors = ref<Record<string, string[]>>({})
const busy = ref(false)

/** Rendered here, never by a third party: the URI carries the secret. */
const qrCode = computed(() =>
  setup.value
    ? `data:image/svg+xml;utf8,${encodeURIComponent(renderSVG(setup.value.authenticatorUri, { border: 2 }))}`
    : null,
)

async function load() {
  try {
    status.value = await getTwoFactor()
  } catch (error) {
    toast.error(error)
  }
}

onMounted(load)

function fieldErrorsOf(error: unknown): Record<string, string[]> | null {
  return error instanceof ApiError && Object.keys(error.fieldErrors).length > 0
    ? error.fieldErrors
    : null
}

async function beginSetup() {
  busy.value = true
  try {
    setup.value = await startTwoFactorSetup()
    setupCode.value = ''
    setupErrors.value = {}
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = false
  }
}

async function confirmSetup() {
  busy.value = true
  setupErrors.value = {}
  try {
    const { codes: minted } = await enableTwoFactor(setupCode.value.trim())
    codes.value = minted
    setup.value = null
    toast.success('Two-factor authentication is on.', 'Save your recovery codes before you leave.')
    await load()
  } catch (error) {
    const fields = fieldErrorsOf(error)
    if (fields) setupErrors.value = fields
    else toast.error(error)
  } finally {
    busy.value = false
  }
}

function startConfirming(action: 'disable' | 'regenerate') {
  confirming.value = action
  proofPassword.value = ''
  proofCode.value = ''
  proofErrors.value = {}
}

async function submitProof() {
  busy.value = true
  proofErrors.value = {}
  const proof = props.hasPassword
    ? { currentPassword: proofPassword.value }
    : { code: proofCode.value.trim() }

  try {
    if (confirming.value === 'disable') {
      await disableTwoFactor(proof)
      codes.value = null
      toast.success('Two-factor authentication is off.')
    } else {
      codes.value = (await regenerateRecoveryCodes(proof)).codes
      toast.success('New recovery codes.', 'The old ones no longer work.')
    }
    confirming.value = null
    await load()
  } catch (error) {
    const fields = fieldErrorsOf(error)
    if (fields) proofErrors.value = fields
    else toast.error(error)
  } finally {
    busy.value = false
  }
}

async function copyCodes() {
  if (!codes.value) return
  try {
    await navigator.clipboard.writeText(codes.value.join('\n'))
    toast.success('Recovery codes copied.')
  } catch (error) {
    toast.error(error, 'The codes could not be copied. Select and copy them instead.')
  }
}

function downloadCodes() {
  if (!codes.value) return
  const blob = new Blob([`Aictiq recovery codes\n\n${codes.value.join('\n')}\n`], {
    type: 'text/plain',
  })
  const link = document.createElement('a')
  link.href = URL.createObjectURL(blob)
  link.download = 'aictiq-recovery-codes.txt'
  link.click()
  URL.revokeObjectURL(link.href)
}
</script>

<template>
  <SettingsSection
    title="Two-factor authentication"
    description="Ask for a code from an authenticator app after your password. Sign-in with a passkey or a provider is not affected, and neither are access tokens."
  >
    <div v-if="status" class="space-y-4" data-testid="two-factor">
      <!-- The recovery codes, shown exactly once after they are minted. -->
      <div v-if="codes" class="border-border space-y-3 rounded-lg border p-4">
        <div>
          <p class="text-sm font-medium">Save your recovery codes</p>
          <p class="text-muted-foreground text-xs">
            Each code signs you in once if you lose your phone. Keep them somewhere safe - they will
            not be shown again.
          </p>
        </div>
        <ul class="grid grid-cols-2 gap-1.5 font-mono text-sm" data-testid="recovery-codes">
          <li v-for="item in codes" :key="item">{{ item }}</li>
        </ul>
        <div class="flex flex-wrap justify-end gap-2">
          <Button variant="outline" size="sm" @click="copyCodes">Copy</Button>
          <Button variant="outline" size="sm" @click="downloadCodes">Download</Button>
          <Button size="sm" @click="codes = null">I have saved them</Button>
        </div>
      </div>

      <!-- Setting up: scan, then prove the app has the key. -->
      <form
        v-else-if="setup"
        class="border-border space-y-4 rounded-lg border p-4"
        novalidate
        @submit.prevent="confirmSetup"
      >
        <p class="text-sm">
          Scan this QR code with an authenticator app such as 1Password, Google Authenticator or
          Microsoft Authenticator, then enter the 6-digit code it shows.
        </p>
        <div class="flex flex-col items-start gap-4 sm:flex-row">
          <img
            v-if="qrCode"
            :src="qrCode"
            alt="QR code for your authenticator app"
            class="size-40 rounded-md bg-white"
          />
          <div class="space-y-1.5">
            <p class="text-muted-foreground text-xs">Can't scan it? Enter this key instead:</p>
            <code
              class="bg-muted block rounded px-2 py-1 font-mono text-sm break-all"
              data-testid="shared-key"
            >
              {{ setup.sharedKey }}
            </code>
          </div>
        </div>

        <div class="space-y-1.5">
          <label for="two-factor-code" class="text-sm font-medium">Code from the app</label>
          <Input
            id="two-factor-code"
            v-model="setupCode"
            inputmode="numeric"
            autocomplete="one-time-code"
            placeholder="123456"
            :aria-invalid="Boolean(setupErrors.code)"
          />
          <p v-for="message in setupErrors.code" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="flex justify-end gap-2">
          <Button type="button" variant="ghost" @click="setup = null">Cancel</Button>
          <Button type="submit" :disabled="busy || setupCode.trim().length === 0">
            <Loader2 v-if="busy" class="animate-spin" aria-hidden="true" />
            Turn on
          </Button>
        </div>
      </form>

      <template v-if="!setup">
        <div
          v-if="status.enabled"
          class="border-border flex items-center justify-between gap-3 rounded-lg border px-3 py-2"
        >
          <div class="flex items-center gap-2.5">
            <ShieldCheck class="size-4 text-emerald-600" aria-hidden="true" />
            <div>
              <p class="text-sm">On</p>
              <p class="text-muted-foreground text-xs">
                {{ status.recoveryCodesLeft }}
                {{ status.recoveryCodesLeft === 1 ? 'recovery code' : 'recovery codes' }} left
              </p>
            </div>
          </div>
          <div class="flex gap-1">
            <Button variant="ghost" size="sm" @click="startConfirming('regenerate')">
              New recovery codes
            </Button>
            <Button variant="ghost" size="sm" @click="startConfirming('disable')">Turn off</Button>
          </div>
        </div>

        <div v-else class="flex justify-end">
          <Button variant="outline" :disabled="busy" @click="beginSetup">
            <Loader2 v-if="busy" class="animate-spin" aria-hidden="true" />
            Set up authenticator app
          </Button>
        </div>

        <form
          v-if="confirming"
          class="border-border space-y-3 rounded-lg border p-4"
          novalidate
          @submit.prevent="submitProof"
        >
          <p class="text-sm">
            {{
              confirming === 'disable'
                ? 'Turning two-factor authentication off removes the code step from your sign-in.'
                : 'New codes replace the old ones straight away.'
            }}
          </p>
          <div v-if="hasPassword" class="space-y-1.5">
            <label for="two-factor-password" class="text-sm font-medium">Your password</label>
            <PasswordInput
              id="two-factor-password"
              v-model="proofPassword"
              autocomplete="current-password"
              :aria-invalid="Boolean(proofErrors.currentPassword)"
            />
            <p
              v-for="message in proofErrors.currentPassword"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>
          <div v-else class="space-y-1.5">
            <label for="two-factor-proof-code" class="text-sm font-medium">
              Code from your authenticator app
            </label>
            <Input
              id="two-factor-proof-code"
              v-model="proofCode"
              inputmode="numeric"
              autocomplete="one-time-code"
              :aria-invalid="Boolean(proofErrors.code)"
            />
            <p v-for="message in proofErrors.code" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>
          <div class="flex justify-end gap-2">
            <Button type="button" variant="ghost" @click="confirming = null">Cancel</Button>
            <Button
              type="submit"
              :variant="confirming === 'disable' ? 'destructive' : 'default'"
              :disabled="busy"
            >
              <Loader2 v-if="busy" class="animate-spin" aria-hidden="true" />
              {{ confirming === 'disable' ? 'Turn off' : 'Generate new codes' }}
            </Button>
          </div>
        </form>
      </template>
    </div>
  </SettingsSection>
</template>
