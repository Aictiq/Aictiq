<script setup lang="ts">
import { KeyRound, Loader2 } from '@lucide/vue'
import { computed, ref, useTemplateRef } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import { describeExternalError, EMAIL_UNCONFIRMED, SIGN_IN_EXPIRED } from '@/api/auth'
import AuthCard from '@/components/AuthCard.vue'
import ExternalProviderButtons from '@/components/ExternalProviderButtons.vue'
import ResendConfirmation from '@/components/ResendConfirmation.vue'
import TurnstileWidget from '@/components/TurnstileWidget.vue'
import { Button } from '@/components/ui/button'
import { Input, PasswordInput } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'
import { turnstileReady } from '@/utils/turnstile'
import { isCancelled, passkeysSupported } from '@/utils/webauthn'

const session = useSessionStore()
const router = useRouter()
const route = useRoute()
const toast = useToast()

// Prefilled when they arrive from a confirmation link, which knows whose address it was.
const email = ref(typeof route.query.email === 'string' ? route.query.email : '')
const password = ref('')
const captcha = ref<string | null>(null)
const widget = useTemplateRef<InstanceType<typeof TurnstileWidget>>('widget')
/**
 * The address whose password was right but which has not been confirmed yet. Only the
 * password's holder ever gets this answer, so offering to resend reveals nothing.
 */
const unconfirmed = ref<string | null>(null)
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})
/**
 * Either the API's own refusal, or the reason an external sign-in bounced back here -
 * the OAuth callback has no body to put a message in, so it redirects with a code.
 */
const formError = ref<string | null>(describeExternalError(route.query.error))

/** Where the provider round trip should return to. Absent, the callback lands on the app root. */
const next = computed(() => (typeof route.query.next === 'string' ? route.query.next : undefined))

/**
 * Set once the password checked out on an account with two-factor on. The form then asks
 * for the code instead; no session exists until it is accepted.
 */
const ticket = ref<string | null>(null)
const code = ref('')
const useRecoveryCode = ref(false)
const passkeyBusy = ref(false)
const canUsePasskey = passkeysSupported()

/** `next` is where the guard bounced them from; default to the app root. */
async function proceed() {
  await router.replace(next.value ?? '/')
}

async function submit() {
  submitting.value = true
  fieldErrors.value = {}
  formError.value = null
  unconfirmed.value = null

  try {
    const outcome = await session.login(
      { email: email.value.trim(), password: password.value },
      captcha.value,
    )
    if (outcome.status === 'two-factor') {
      ticket.value = outcome.ticket
      return
    }
    await proceed()
  } catch (error) {
    if (error instanceof ApiError && error.problem?.type === EMAIL_UNCONFIRMED) {
      unconfirmed.value = email.value.trim()
    } else if (error instanceof ApiError) {
      fieldErrors.value = error.fieldErrors
      // 401 carries no field errors - the API refuses to say which half was wrong.
      formError.value = Object.keys(error.fieldErrors).length === 0 ? error.title : null
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
    // A token is spent by the attempt, whatever its outcome.
    widget.value?.reset()
  }
}

async function submitCode() {
  if (!ticket.value) return
  submitting.value = true
  fieldErrors.value = {}
  formError.value = null

  try {
    const answer = code.value.trim()
    await session.completeTwoFactor(
      ticket.value,
      useRecoveryCode.value ? { recoveryCode: answer } : { code: answer },
    )
    await proceed()
  } catch (error) {
    if (error instanceof ApiError && error.problem?.type === SIGN_IN_EXPIRED) {
      // Too slow, or used already: the password step has to be done again.
      backToPassword()
      formError.value = 'That sign-in took too long. Enter your password again.'
    } else if (error instanceof ApiError) {
      fieldErrors.value = error.fieldErrors
      formError.value = Object.keys(error.fieldErrors).length === 0 ? describe(error) : null
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

/** Title and, when the API gave one, the hint on what to do about it. */
function describe(error: ApiError): string {
  return error.problem?.detail ? `${error.title} ${error.problem.detail}` : error.title
}

function toggleRecoveryCode() {
  useRecoveryCode.value = !useRecoveryCode.value
  code.value = ''
}

function backToPassword() {
  ticket.value = null
  code.value = ''
  useRecoveryCode.value = false
  password.value = ''
}

async function signInWithPasskey() {
  passkeyBusy.value = true
  formError.value = null
  unconfirmed.value = null

  try {
    await session.loginWithPasskey()
    await proceed()
  } catch (error) {
    if (isCancelled(error)) return
    if (error instanceof ApiError) {
      formError.value = describe(error)
    } else {
      toast.error(error)
    }
  } finally {
    passkeyBusy.value = false
  }
}
</script>

<template>
  <AuthCard
    title="Sign in"
    :description="ticket ? 'One more step to confirm it is you.' : 'Welcome back to Aictiq.'"
  >
    <form v-if="ticket" class="space-y-4" novalidate @submit.prevent="submitCode">
      <div class="space-y-1.5">
        <label for="code" class="text-sm font-medium">
          {{ useRecoveryCode ? 'Recovery code' : 'Authentication code' }}
        </label>
        <Input
          id="code"
          v-model="code"
          :inputmode="useRecoveryCode ? 'text' : 'numeric'"
          autocomplete="one-time-code"
          autofocus
          required
          :placeholder="useRecoveryCode ? 'XXXXX-XXXXX' : '123456'"
          :aria-invalid="Boolean(fieldErrors.code)"
        />
        <p class="text-muted-foreground text-xs">
          {{
            useRecoveryCode
              ? 'One of the codes you saved when you turned on two-factor authentication. Each works once.'
              : 'Open your authenticator app and enter the 6-digit code for Aictiq.'
          }}
        </p>
        <p v-for="message in fieldErrors.code" :key="message" class="text-destructive text-xs">
          {{ message }}
        </p>
      </div>

      <p v-if="formError" role="alert" class="text-destructive text-sm">{{ formError }}</p>

      <Button type="submit" class="w-full" :disabled="submitting || code.trim().length === 0">
        <Loader2 v-if="submitting" class="size-4 animate-spin" />
        Verify
      </Button>

      <div class="flex items-center justify-between text-xs">
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground"
          @click="toggleRecoveryCode"
        >
          {{ useRecoveryCode ? 'Use your authenticator app' : 'Use a recovery code' }}
        </button>
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground"
          @click="backToPassword"
        >
          Back
        </button>
      </div>
    </form>

    <template v-else>
      <form class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="email" class="text-sm font-medium">Email</label>
          <Input
            id="email"
            v-model="email"
            type="email"
            autocomplete="email"
            required
            :aria-invalid="Boolean(fieldErrors.email)"
          />
          <p v-for="message in fieldErrors.email" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <div class="flex items-center justify-between">
            <label for="password" class="text-sm font-medium">Password</label>
            <RouterLink
              to="/forgot-password"
              class="text-muted-foreground hover:text-foreground text-xs"
            >
              Forgot password?
            </RouterLink>
          </div>
          <PasswordInput
            id="password"
            v-model="password"
            autocomplete="current-password"
            required
            :aria-invalid="Boolean(fieldErrors.password)"
          />
          <p
            v-for="message in fieldErrors.password"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <TurnstileWidget ref="widget" v-model:token="captcha" action="login" />

        <p v-if="formError" role="alert" class="text-destructive text-sm">{{ formError }}</p>

        <div v-if="unconfirmed" role="alert" class="bg-muted space-y-3 rounded-md p-3">
          <p class="text-sm">
            Confirm your email address first. Follow the link we sent to
            <span class="font-medium">{{ unconfirmed }}</span> when you registered, then sign in.
          </p>
          <ResendConfirmation :key="unconfirmed" :email="unconfirmed" />
        </div>

        <Button type="submit" class="w-full" :disabled="submitting || !turnstileReady(captcha)">
          <Loader2 v-if="submitting" class="size-4 animate-spin" />
          Sign in
        </Button>
      </form>

      <!-- Hidden where the browser has no WebAuthn at all; a passkey is then no option. -->
      <Button
        v-if="canUsePasskey"
        type="button"
        variant="outline"
        class="mt-3 w-full"
        :disabled="passkeyBusy"
        @click="signInWithPasskey"
      >
        <Loader2 v-if="passkeyBusy" class="size-4 animate-spin" />
        <KeyRound v-else class="size-4" aria-hidden="true" />
        Sign in with a passkey
      </Button>

      <!-- Renders nothing when the instance has no provider credentials. -->
      <ExternalProviderButtons :next="next" />
    </template>

    <template #footer>
      No account?
      <!-- Carries `next` across: switching form must not lose the invitation they arrived with. -->
      <RouterLink
        :to="{ name: 'register', query: route.query }"
        class="text-foreground font-medium underline underline-offset-4"
      >
        Create one
      </RouterLink>
    </template>
  </AuthCard>
</template>
