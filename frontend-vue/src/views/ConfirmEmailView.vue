<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

import AuthCard from '@/components/AuthCard.vue'
import UiPageState from '@/components/UiPageState.vue'
import { confirmEmailChange } from '@/api/profile'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * Confirms a new email address from the link mailed to it.
 *
 * It runs on arrival rather than behind a button: the click on the link in the mail *is*
 * the confirmation, and asking for a second one only invites people to close the tab. The
 * link works anonymously, because it is usually opened on a phone that is not signed in.
 */
const route = useRoute()
const session = useSessionStore()

const state = ref<'working' | 'done' | 'failed'>('working')
const email = ref('')
const message = ref('')

onMounted(async () => {
  try {
    const result = await confirmEmailChange(String(route.params.token ?? ''))
    email.value = result.email
    // Matched on the user id, never on the address: the link is usually opened somewhere
    // other than the session that asked for it, and a browser signed in as a colleague
    // must not have their sidebar quietly relabelled with somebody else's new address.
    if (session.user?.id === result.userId) {
      session.apply({ email: result.email })
    }
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
    title="Address confirmed"
    :description="`${email} is now the address you sign in with.`"
  >
    <template #footer>
      <RouterLink to="/settings/security" class="text-foreground font-medium underline underline-offset-4">
        Back to security settings
      </RouterLink>
    </template>
  </AuthCard>

  <AuthCard v-else title="That link did not work" :description="message">
    <template #footer>
      <RouterLink to="/settings/security" class="text-foreground font-medium underline underline-offset-4">
        Ask for another one
      </RouterLink>
    </template>
  </AuthCard>
</template>
