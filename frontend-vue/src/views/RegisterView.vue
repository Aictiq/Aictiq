<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import { describeExternalError } from '@/api/auth'
import AuthCard from '@/components/AuthCard.vue'
import ExternalProviderButtons from '@/components/ExternalProviderButtons.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useToast } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

const session = useSessionStore()
const router = useRouter()
const route = useRoute()
const toast = useToast()

const firstName = ref('')
const lastName = ref('')
const email = ref('')
const password = ref('')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})
const formError = ref<string | null>(describeExternalError(route.query.error))

const next = computed(() => (typeof route.query.next === 'string' ? route.query.next : undefined))

async function submit() {
  submitting.value = true
  fieldErrors.value = {}
  formError.value = null

  try {
    await session.register({
      firstName: firstName.value,
      lastName: lastName.value,
      email: email.value,
      password: password.value,
    })
    // Same `next` contract as the login page. An invitation link sends people here
    // first, and dropping them on the app root afterwards would lose the invitation.
    await router.replace(typeof route.query.next === 'string' ? route.query.next : '/onboarding')
  } catch (error) {
    if (error instanceof ApiError) {
      fieldErrors.value = error.fieldErrors
      formError.value = Object.keys(error.fieldErrors).length === 0 ? error.title : null
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <AuthCard title="Create an account" description="Start tracking work in Aictiq.">
    <form class="space-y-4" novalidate @submit.prevent="submit">
      <div class="grid grid-cols-2 gap-3">
        <div class="space-y-1.5">
          <label for="firstName" class="text-sm font-medium">First name</label>
          <Input
            id="firstName"
            v-model="firstName"
            autocomplete="given-name"
            required
            :aria-invalid="Boolean(fieldErrors.firstName)"
          />
          <p
            v-for="message in fieldErrors.firstName"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="lastName" class="text-sm font-medium">Last name</label>
          <Input
            id="lastName"
            v-model="lastName"
            autocomplete="family-name"
            required
            :aria-invalid="Boolean(fieldErrors.lastName)"
          />
          <p
            v-for="message in fieldErrors.lastName"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>
      </div>

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
        <label for="password" class="text-sm font-medium">Password</label>
        <Input
          id="password"
          v-model="password"
          type="password"
          autocomplete="new-password"
          required
          :aria-invalid="Boolean(fieldErrors.password)"
        />
        <!-- The API enforces the policy (12+ chars, no composition rules); this only says so. -->
        <p class="text-muted-foreground text-xs">At least 12 characters.</p>
        <p v-for="message in fieldErrors.password" :key="message" class="text-destructive text-xs">
          {{ message }}
        </p>
      </div>

      <p v-if="formError" role="alert" class="text-destructive text-sm">{{ formError }}</p>

      <Button type="submit" class="w-full" :disabled="submitting">
        <Loader2 v-if="submitting" class="size-4 animate-spin" />
        Create account
      </Button>
    </form>

    <!-- Signing up with a provider and signing in with one are the same handshake: the
         callback creates the account if there is not one yet. -->
    <ExternalProviderButtons :next="next ?? '/onboarding'" />

    <template #footer>
      Already have an account?
      <!-- Carries `next` across: switching form must not lose the invitation they arrived with. -->
      <RouterLink
        :to="{ name: 'login', query: route.query }"
        class="text-foreground font-medium underline underline-offset-4"
      >
        Sign in
      </RouterLink>
    </template>
  </AuthCard>
</template>
