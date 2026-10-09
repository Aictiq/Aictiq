<script setup lang="ts">
import { KeyRound, Loader2 } from '@lucide/vue'
import { onMounted, ref } from 'vue'

import SettingsSection from '@/components/settings/SettingsSection.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  addPasskey,
  listPasskeys,
  removePasskey,
  renamePasskey,
  type Passkey,
} from '@/api/security'
import { useToast } from '@/composables/useToast'
import { ApiError } from '@/utils/api'
import { isCancelled, passkeysSupported } from '@/utils/webauthn'

/**
 * Passkeys: sign in with a fingerprint, a face, a device PIN or a security key instead of
 * the password. A passkey sign-in is complete on its own - it does not also ask for an
 * authenticator code - so this is a way in, not a second factor.
 */
const toast = useToast()

const supported = passkeysSupported()
const passkeys = ref<Passkey[]>([])
const loaded = ref(false)
const adding = ref(false)
const newName = ref('')
const nameErrors = ref<string[]>([])
const busy = ref<string | null>(null)
const editing = ref<string | null>(null)
const editName = ref('')

async function load() {
  try {
    passkeys.value = await listPasskeys()
  } catch (error) {
    toast.error(error)
  } finally {
    loaded.value = true
  }
}

onMounted(load)

/** What the browser calls this device, as a starting name the person can change. */
function suggestedName(): string {
  const agent = navigator.userAgent
  if (/iPhone|iPad/.test(agent)) return 'iPhone'
  if (/Android/.test(agent)) return 'Android phone'
  if (/Mac OS X/.test(agent)) return 'Mac'
  if (/Windows/.test(agent)) return 'Windows PC'
  if (/Linux/.test(agent)) return 'Linux computer'
  return 'Passkey'
}

async function add() {
  const name = (newName.value.trim() || suggestedName()).slice(0, 100)
  busy.value = 'add'
  nameErrors.value = []
  try {
    await addPasskey(name)
    newName.value = ''
    adding.value = false
    toast.success('Passkey added.', 'You can now sign in with it from the sign-in page.')
    await load()
  } catch (error) {
    if (isCancelled(error)) return
    if (error instanceof ApiError && error.fieldErrors.name)
      nameErrors.value = error.fieldErrors.name
    else if (error instanceof ApiError && error.fieldErrors.credential)
      toast.error(error, error.fieldErrors.credential[0])
    else toast.error(error)
  } finally {
    busy.value = null
  }
}

function startRename(passkey: Passkey) {
  editing.value = passkey.id
  editName.value = passkey.name
}

async function saveRename(passkey: Passkey) {
  const name = editName.value.trim()
  if (!name || name === passkey.name) {
    editing.value = null
    return
  }
  busy.value = passkey.id
  try {
    await renamePasskey(passkey.id, name)
    editing.value = null
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = null
  }
}

async function remove(passkey: Passkey) {
  busy.value = passkey.id
  try {
    await removePasskey(passkey.id)
    toast.success(`Removed "${passkey.name}".`)
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busy.value = null
  }
}

const formatted = (value: string) => new Date(value).toLocaleDateString()
</script>

<template>
  <SettingsSection
    title="Passkeys"
    description="Sign in with your fingerprint, face, device PIN or a security key instead of your password."
  >
    <div v-if="loaded" class="space-y-3" data-testid="passkeys">
      <p v-if="!supported" class="text-muted-foreground text-xs">
        This browser does not support passkeys. Try a current version of Chrome, Edge, Firefox or
        Safari.
      </p>

      <ul v-if="passkeys.length > 0" class="space-y-2">
        <li
          v-for="passkey in passkeys"
          :key="passkey.id"
          class="border-border flex items-center justify-between gap-3 rounded-lg border px-3 py-2"
        >
          <form
            v-if="editing === passkey.id"
            class="flex flex-1 items-center gap-2"
            @submit.prevent="saveRename(passkey)"
          >
            <Input
              v-model="editName"
              :aria-label="`New name for ${passkey.name}`"
              maxlength="100"
            />
            <Button type="submit" size="sm" :disabled="busy === passkey.id">Save</Button>
            <Button type="button" variant="ghost" size="sm" @click="editing = null">Cancel</Button>
          </form>
          <template v-else>
            <div class="flex items-center gap-2.5">
              <KeyRound class="text-muted-foreground size-4" aria-hidden="true" />
              <div>
                <p class="text-sm">{{ passkey.name }}</p>
                <p class="text-muted-foreground text-xs">
                  Added {{ formatted(passkey.createdAt) }}
                  <span v-if="passkey.isBackedUp"> · synced</span>
                </p>
              </div>
            </div>
            <div class="flex gap-1">
              <Button variant="ghost" size="sm" @click="startRename(passkey)">Rename</Button>
              <Button
                variant="ghost"
                size="sm"
                :disabled="busy === passkey.id"
                :aria-label="`Remove ${passkey.name}`"
                @click="remove(passkey)"
              >
                <Loader2 v-if="busy === passkey.id" class="animate-spin" aria-hidden="true" />
                Remove
              </Button>
            </div>
          </template>
        </li>
      </ul>

      <form
        v-if="adding"
        class="border-border space-y-3 rounded-lg border p-4"
        novalidate
        @submit.prevent="add"
      >
        <div class="space-y-1.5">
          <label for="passkey-name" class="text-sm font-medium">Name</label>
          <Input
            id="passkey-name"
            v-model="newName"
            maxlength="100"
            :placeholder="suggestedName()"
            :aria-invalid="nameErrors.length > 0"
          />
          <p class="text-muted-foreground text-xs">
            So you can tell your passkeys apart later. Your browser will ask you to confirm.
          </p>
          <p v-for="message in nameErrors" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>
        <div class="flex justify-end gap-2">
          <Button type="button" variant="ghost" @click="adding = false">Cancel</Button>
          <Button type="submit" :disabled="busy === 'add'">
            <Loader2 v-if="busy === 'add'" class="animate-spin" aria-hidden="true" />
            Create passkey
          </Button>
        </div>
      </form>

      <div v-else-if="supported" class="flex justify-end">
        <Button variant="outline" @click="adding = true">Add a passkey</Button>
      </div>
    </div>
  </SettingsSection>
</template>
