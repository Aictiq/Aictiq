<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import AuthCard from '@/components/AuthCard.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { resetPassword } from '@/api/profile'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * Spends a reset link on a new password.
 *
 * The link is not validated on arrival, only on submit: an endpoint that answered "is
 * this token real" would let anyone test tokens without spending them, and the honest
 * failure — "this link is no longer valid" — reads the same either way.
 */
const route = useRoute()
const router = useRouter()
const session = useSessionStore()

const password = ref('')
const saving = ref(false)
const done = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const token = String(route.params.token ?? '')

async function submit() {
  saving.value = true
  fieldErrors.value = {}

  try {
    await resetPassword(token, password.value)
    // The reset revoked every session, including one this browser may have been holding.
    session.clear()
    done.value = true
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      fieldErrors.value = { newPassword: [error instanceof ApiError ? error.title : 'Something went wrong.'] }
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <AuthCard
    v-if="done"
    title="Password changed"
    description="Every device that was signed in has been signed out. Sign in again with the new password."
  >
    <Button class="w-full" @click="router.push('/login')">Sign in</Button>
  </AuthCard>

  <AuthCard v-else title="Choose a new password" description="This link works once.">
    <form class="space-y-4" novalidate @submit.prevent="submit">
      <div class="space-y-1.5">
        <label for="reset-password" class="text-sm font-medium">New password</label>
        <Input
          id="reset-password"
          v-model="password"
          type="password"
          autocomplete="new-password"
          required
          :aria-invalid="Boolean(fieldErrors.newPassword)"
        />
        <p class="text-muted-foreground text-xs">At least 12 characters.</p>
        <p
          v-for="message in fieldErrors.newPassword"
          :key="message"
          class="text-destructive text-xs"
        >
          {{ message }}
        </p>
        <p v-for="message in fieldErrors.token" :key="message" class="text-destructive text-xs">
          {{ message }}
        </p>
      </div>

      <Button type="submit" class="w-full" :disabled="saving || password.length === 0">
        <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
        Set the password
      </Button>
    </form>

    <template #footer>
      <RouterLink
        to="/forgot-password"
        class="text-foreground font-medium underline underline-offset-4"
      >
        Ask for another link
      </RouterLink>
    </template>
  </AuthCard>
</template>
