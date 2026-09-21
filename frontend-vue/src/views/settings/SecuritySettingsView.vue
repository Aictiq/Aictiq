<script setup lang="ts">
import { Loader2, Monitor } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'

import ExternalProviderButtons from '@/components/ExternalProviderButtons.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { listLogins, unlinkLogin, type ExternalLogin } from '@/api/auth'
import {
  cancelEmailChange,
  changePassword,
  describeDevice,
  getProfile,
  listSessions,
  requestEmailChange,
  revokeOtherSessions,
  revokeSession,
  type Profile,
  type Session,
} from '@/api/profile'
import { useToast } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * Everything that decides how someone proves who they are: their password, the address
 * recovery mail goes to, the providers they can sign in with, and where they are signed
 * in right now.
 *
 * They sit on one page because they are one decision — "is my account still mine" — and
 * the answer usually needs two of them at once: sign the stolen session out, then change
 * the password it was riding.
 */
const session = useSessionStore()
const toast = useToast()

const profile = ref<Profile | null>(null)
const sessions = ref<Session[]>([])
const logins = ref<ExternalLogin[]>([])
const loading = ref(true)

const currentPassword = ref('')
const newPassword = ref('')
const savingPassword = ref(false)
const passwordErrors = ref<Record<string, string[]>>({})

const newEmail = ref('')
const emailPassword = ref('')
const savingEmail = ref(false)
const emailErrors = ref<Record<string, string[]>>({})

const busySession = ref<string | null>(null)

const hasPassword = computed(() => profile.value?.hasPassword ?? true)

// Each of these forms is one action rather than an edit of something loaded, so there is
// no dirty guard here: leaving with a half-typed password should clear it.
const passwordHint = computed(() =>
  hasPassword.value
    ? 'Changing it signs out every other device. This one stays.'
    : 'You signed up with a provider, so there is no password yet. Setting one lets you sign in without it.',
)

async function load() {
  loading.value = true
  try {
    const [loaded, where, linked] = await Promise.all([getProfile(), listSessions(), listLogins()])
    profile.value = loaded
    sessions.value = where
    logins.value = linked
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function submitPassword() {
  savingPassword.value = true
  passwordErrors.value = {}

  try {
    await changePassword({
      currentPassword: hasPassword.value ? currentPassword.value : undefined,
      newPassword: newPassword.value,
    })
    currentPassword.value = ''
    newPassword.value = ''
    toast.success(
      hasPassword.value ? 'Password changed.' : 'Password set.',
      'Every other device has been signed out.',
    )
    await load()
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      passwordErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    savingPassword.value = false
  }
}

async function submitEmail() {
  savingEmail.value = true
  emailErrors.value = {}

  try {
    const { emailConfigured } = await requestEmailChange({
      newEmail: newEmail.value.trim(),
      currentPassword: hasPassword.value ? emailPassword.value : undefined,
    })
    newEmail.value = ''
    emailPassword.value = ''
    // Without a relay the link is minted and then goes nowhere. Say so rather than let
    // someone wait for mail this instance was never configured to send.
    if (emailConfigured) {
      toast.success('Check the new address.', 'Nothing changes until you open the link we sent it.')
    } else {
      toast.info(
        'This instance cannot send email.',
        'Ask an administrator to change the address for you.',
      )
    }
    await load()
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      emailErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    savingEmail.value = false
  }
}

async function cancelPending() {
  try {
    await cancelEmailChange()
    await load()
  } catch (error) {
    toast.error(error)
  }
}

async function endSession(id: string, isCurrent: boolean) {
  busySession.value = id
  try {
    await revokeSession(id)
    if (isCurrent) {
      // The cookies are already gone server-side; clearing the store sends the guard to
      // the login page rather than letting the app keep failing every request.
      session.clear()
      window.location.assign('/login')
      return
    }
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busySession.value = null
  }
}

async function endOthers() {
  try {
    await revokeOtherSessions()
    toast.success('Signed out everywhere else.')
    await load()
  } catch (error) {
    toast.error(error)
  }
}

async function unlink(provider: string) {
  try {
    await unlinkLogin(provider)
    await load()
  } catch (error) {
    toast.error(error)
  }
}

const formatted = (value: string) => new Date(value).toLocaleString()
</script>

<template>
  <UiPageState v-if="loading" state="loading" />

  <div v-else-if="profile" class="space-y-8">
    <SettingsSection
      :title="hasPassword ? 'Change password' : 'Set a password'"
      :description="passwordHint"
    >
      <form class="space-y-4" novalidate @submit.prevent="submitPassword">
        <div v-if="hasPassword" class="space-y-1.5">
          <label for="security-current" class="text-sm font-medium">Current password</label>
          <Input
            id="security-current"
            v-model="currentPassword"
            type="password"
            autocomplete="current-password"
            :aria-invalid="Boolean(passwordErrors.currentPassword)"
          />
          <p
            v-for="message in passwordErrors.currentPassword"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="security-new" class="text-sm font-medium">New password</label>
          <Input
            id="security-new"
            v-model="newPassword"
            type="password"
            autocomplete="new-password"
            :aria-invalid="Boolean(passwordErrors.newPassword)"
          />
          <p class="text-muted-foreground text-xs">At least 12 characters.</p>
          <p
            v-for="message in passwordErrors.newPassword"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="flex justify-end">
          <Button type="submit" :disabled="savingPassword || newPassword.length === 0">
            <Loader2 v-if="savingPassword" class="animate-spin" aria-hidden="true" />
            {{ hasPassword ? 'Change password' : 'Set password' }}
          </Button>
        </div>
      </form>
    </SettingsSection>

    <SettingsSection
      title="Email address"
      :description="`Currently ${profile.email}. A new one has to be confirmed from its own inbox before it takes effect.`"
    >
      <form class="space-y-4" novalidate @submit.prevent="submitEmail">
        <div
          v-if="profile.pendingEmail"
          class="border-border flex items-center justify-between gap-3 rounded-lg border px-3 py-2"
        >
          <p class="text-xs">
            Waiting for <span class="font-medium">{{ profile.pendingEmail }}</span> to be confirmed.
          </p>
          <Button variant="ghost" size="sm" @click.prevent="cancelPending">Cancel</Button>
        </div>

        <div class="space-y-1.5">
          <label for="security-email" class="text-sm font-medium">New address</label>
          <Input
            id="security-email"
            v-model="newEmail"
            type="email"
            autocomplete="email"
            :aria-invalid="Boolean(emailErrors.newEmail)"
          />
          <p v-for="message in emailErrors.newEmail" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div v-if="hasPassword" class="space-y-1.5">
          <label for="security-email-password" class="text-sm font-medium">Your password</label>
          <Input
            id="security-email-password"
            v-model="emailPassword"
            type="password"
            autocomplete="current-password"
            :aria-invalid="Boolean(emailErrors.currentPassword)"
          />
          <p class="text-muted-foreground text-xs">
            Asked for because this is where password resets are sent.
          </p>
          <p
            v-for="message in emailErrors.currentPassword"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="flex justify-end">
          <Button type="submit" :disabled="savingEmail || newEmail.trim().length === 0">
            <Loader2 v-if="savingEmail" class="animate-spin" aria-hidden="true" />
            Send confirmation
          </Button>
        </div>
      </form>
    </SettingsSection>

    <SettingsSection
      title="Sign-in providers"
      description="Providers linked to this account. The last way in cannot be removed."
    >
      <div class="space-y-3">
        <ul v-if="logins.length > 0" class="space-y-2">
          <li
            v-for="login in logins"
            :key="login.provider"
            class="border-border flex items-center justify-between gap-3 rounded-lg border px-3 py-2"
          >
            <span class="text-sm">{{ login.displayName }}</span>
            <Button
              variant="ghost"
              size="sm"
              :disabled="!login.canUnlink"
              :title="
                login.canUnlink ? undefined : 'Set a password first — this is your only way in.'
              "
              @click="unlink(login.provider)"
            >
              Unlink
            </Button>
          </li>
        </ul>

        <ExternalProviderButtons mode="link" :linked="logins.map((l) => l.provider)" />
      </div>
    </SettingsSection>

    <SettingsSection
      wide
      title="Sessions"
      description="Every browser and device signed in to this account."
    >
      <div class="space-y-3">
        <div v-if="sessions.length > 1" class="flex justify-end">
          <Button variant="outline" size="sm" @click="endOthers">Sign out everywhere else</Button>
        </div>

        <ul class="space-y-2">
          <li
            v-for="item in sessions"
            :key="item.id"
            class="border-border flex items-center justify-between gap-3 rounded-lg border px-3 py-2"
          >
            <div class="flex items-center gap-2.5">
              <Monitor class="text-muted-foreground size-4" aria-hidden="true" />
              <div>
                <p class="text-sm">
                  {{ describeDevice(item.userAgent) }}
                  <span v-if="item.isCurrent" class="text-muted-foreground text-xs">
                    · this device
                  </span>
                </p>
                <p class="text-muted-foreground text-xs">Last used {{ formatted(item.lastSeenAt) }}</p>
              </div>
            </div>
            <Button
              variant="ghost"
              size="sm"
              :disabled="busySession === item.id"
              @click="endSession(item.id, item.isCurrent)"
            >
              <Loader2 v-if="busySession === item.id" class="animate-spin" aria-hidden="true" />
              {{ item.isCurrent ? 'Sign out' : 'Revoke' }}
            </Button>
          </li>
        </ul>
      </div>
    </SettingsSection>
  </div>
</template>
