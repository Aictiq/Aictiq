<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

import { verifyEmail } from '@/api/auth'
import AuthCard from '@/components/AuthCard.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * The link mailed at registration. Like the email-change link it confirms on arrival: the
 * click in the mail is the confirmation, and a second button would only invite people to
 * close the tab.
 *
 * It does not sign anyone in. The link travels through mail - scanners follow it, people
 * forward it - so it proves the address and nothing more; the password still opens the
 * session. `next` carries on to wherever they were headed, usually an invitation.
 */
const route = useRoute()
const session = useSessionStore()

const state = ref<'working' | 'done' | 'failed'>('working')
const email = ref('')
const message = ref('')

/** Only a path on this origin; anything else is dropped, never followed. */
const next = computed(() => {
  const value = route.query.next
  return typeof value === 'string' && /^\/(?![/\\])/.test(value) ? value : undefined
})

const signIn = computed(() => ({
  name: 'login',
  query: { email: email.value, ...(next.value ? { next: next.value } : {}) },
}))

onMounted(async () => {
  await session.load()
  try {
    const result = await verifyEmail(String(route.params.token ?? ''))
    email.value = result.email
    state.value = 'done'
  } catch (error) {
    message.value =
      error instanceof ApiError
        ? (error.fieldErrors.token?.[0] ?? error.title)
        : 'Something went wrong.'
    state.value = 'failed'
  }
})
</script>

<template>
  <UiPageState v-if="state === 'working'" state="loading" />

  <AuthCard
    v-else-if="state === 'done'"
    title="Email confirmed"
    :description="`${email} is confirmed. Sign in to start using Aictiq.`"
  >
    <Button v-if="!session.isAuthenticated" as-child class="w-full">
      <RouterLink :to="signIn">Continue to sign in</RouterLink>
    </Button>
    <Button v-else as-child class="w-full">
      <RouterLink :to="next ?? '/'">Continue</RouterLink>
    </Button>
  </AuthCard>

  <AuthCard
    v-else
    title="That link did not work"
    :description="`${message} Sign in with your email and password and you will be offered a new link.`"
  >
    <template #footer>
      <RouterLink to="/login" class="text-foreground font-medium underline underline-offset-4">
        Back to sign in
      </RouterLink>
    </template>
  </AuthCard>
</template>
