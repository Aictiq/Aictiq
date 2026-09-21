<script setup lang="ts">
import { Loader2, Trash2, Upload } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

import UserAvatar from '@/components/common/UserAvatar.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  avatarContentTypes,
  avatarUrl,
  getProfile,
  removeAvatar,
  updateProfile,
  uploadAvatar,
  type Profile,
} from '@/api/profile'
import { useDirtyGuard } from '@/composables/useDirtyGuard'
import { useToast } from '@/composables/useToast'
import { useOnboardingStore } from '@/stores/onboarding'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * Who you are: name, picture, time zone. Anything that changes how you *prove* who you
 * are lives one tab over, under Security — the two are separated because they are guarded
 * differently, not because they are unrelated.
 */
const session = useSessionStore()
const onboarding = useOnboardingStore()
const toast = useToast()

const profile = ref<Profile | null>(null)
const loading = ref(true)
const saving = ref(false)
const uploading = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})
const file = ref<HTMLInputElement | null>(null)

const firstName = ref('')
const lastName = ref('')
const timeZone = ref('')

/** Whatever the browser can offer, so nobody has to remember the IANA spelling. */
const timeZones =
  typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []

const picture = computed(() =>
  profile.value ? avatarUrl(profile.value.id, profile.value.avatarKey) : null,
)

const changed = computed(
  () =>
    profile.value !== null &&
    (firstName.value.trim() !== profile.value.firstName ||
      lastName.value.trim() !== profile.value.lastName ||
      timeZone.value !== (profile.value.timeZone ?? '')),
)

// The tabs are a router link away, so a half-typed name would otherwise vanish on a click
// meant to go and read something. The picture needs no such guard: it is committed the
// moment it is chosen.
useDirtyGuard(() => changed.value)

function fill(loaded: Profile) {
  profile.value = loaded
  firstName.value = loaded.firstName
  lastName.value = loaded.lastName
  timeZone.value = loaded.timeZone ?? ''
  // The shell draws the name and the picture from the session, so it has to hear about
  // every change or the header disagrees with the form below it.
  session.apply({
    firstName: loaded.firstName,
    lastName: loaded.lastName,
    fullName: loaded.fullName,
    avatarKey: loaded.avatarKey,
    timeZone: loaded.timeZone,
  })
}

async function load() {
  loading.value = true
  try {
    fill(await getProfile())
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function save() {
  saving.value = true
  fieldErrors.value = {}

  try {
    fill(
      await updateProfile({
        firstName: firstName.value.trim(),
        lastName: lastName.value.trim(),
        timeZone: timeZone.value,
      }),
    )
    toast.success('Saved.')
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}

async function pick(event: Event) {
  const chosen = (event.target as HTMLInputElement).files?.[0]
  if (!chosen) return

  uploading.value = true
  try {
    fill(await uploadAvatar(chosen))
    toast.success('Picture updated.')
  } catch (error) {
    toast.error(error, 'The picture could not be uploaded.')
  } finally {
    uploading.value = false
    // Cleared so choosing the same file again still fires a change event.
    if (file.value) file.value.value = ''
  }
}

async function clearPicture() {
  uploading.value = true
  try {
    await removeAvatar()
    fill(await getProfile())
  } catch (error) {
    toast.error(error)
  } finally {
    uploading.value = false
  }
}
</script>

<template>
  <UiPageState v-if="loading" state="loading" />

  <div v-else-if="profile" class="space-y-8">
    <SettingsSection
      title="Profile picture"
      description="Shown beside everything you write and everything assigned to you."
    >
      <div class="flex items-center gap-4">
        <UserAvatar
          :name="profile.fullName"
          :src="picture"
          :is-agent="profile.isAgent"
          size="lg"
          class="scale-150"
        />
        <div class="ml-3 space-y-1.5">
          <div class="flex items-center gap-2">
            <Button variant="outline" size="sm" :disabled="uploading" @click="file?.click()">
              <Loader2 v-if="uploading" class="animate-spin" aria-hidden="true" />
              <Upload v-else class="size-3.5" aria-hidden="true" />
              {{ profile.avatarKey ? 'Replace' : 'Upload' }}
            </Button>
            <Button
              v-if="profile.avatarKey"
              variant="ghost"
              size="sm"
              :disabled="uploading"
              @click="clearPicture"
            >
              <Trash2 class="size-3.5" aria-hidden="true" />
              Remove
            </Button>
          </div>
          <p class="text-muted-foreground text-xs">PNG, JPEG, WebP or GIF, up to 2 MB.</p>
        </div>
        <input
          ref="file"
          type="file"
          class="hidden"
          :accept="avatarContentTypes.join(',')"
          @change="pick"
        />
      </div>
    </SettingsSection>

    <SettingsSection title="Your details" description="How you appear to everyone else in Aictiq.">
      <form class="space-y-5" novalidate @submit.prevent="save">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-1.5">
            <label for="profile-first" class="text-sm font-medium">First name</label>
            <Input
              id="profile-first"
              v-model="firstName"
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
            <label for="profile-last" class="text-sm font-medium">Last name</label>
            <Input
              id="profile-last"
              v-model="lastName"
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
          <label for="profile-email" class="text-sm font-medium">Email</label>
          <Input id="profile-email" :model-value="profile.email" readonly disabled />
          <p class="text-muted-foreground text-xs">
            Changed under
            <RouterLink to="/settings/security" class="underline underline-offset-4">
              Security</RouterLink
            >, because a new address has to be confirmed from its own inbox first.
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="profile-timezone" class="text-sm font-medium">Time zone</label>
          <select
            id="profile-timezone"
            v-model="timeZone"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none"
          >
            <option value="">Follow the organization</option>
            <option v-for="zone in timeZones" :key="zone" :value="zone">{{ zone }}</option>
          </select>
          <p class="text-muted-foreground text-xs">
            Yours alone — it decides how times are shown to you, not when a sprint ends.
          </p>
          <p
            v-for="message in fieldErrors.timeZone"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="flex justify-end">
          <Button
            type="submit"
            :disabled="saving || !changed || !firstName.trim() || !lastName.trim()"
          >
            <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
            Save changes
          </Button>
        </div>
      </form>
    </SettingsSection>

    <SettingsSection
      title="Product tour"
      description="Replay the introduction, or pick up setup where you left off."
    >
      <div class="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" @click="onboarding.openChecklist()">
          Open Get started
        </Button>
        <Button type="button" variant="secondary" @click="onboarding.startTour()">
          Replay product tour
        </Button>
      </div>
    </SettingsSection>
  </div>
</template>
