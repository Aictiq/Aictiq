<script setup lang="ts">
import { Loader2, Trash2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import {
  deleteOrganization,
  hasOrgRole,
  updateOrganization,
  type Organization,
  type WeekStart,
} from '@/api/organizations'
import DeleteConfirmDialog from '@/components/common/DeleteConfirmDialog.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useDirtyGuard } from '@/composables/useDirtyGuard'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * What the whole organization shares: its name, its permanent address, and the two
 * settings every date in it is read against. The way out is here too, because deleting is
 * a general-settings decision rather than a members or billing one.
 */
const org = useOrgScope()
const organizations = useOrganizationsStore()
const router = useRouter()
const toast = useToast()

/**
 * The layout mounts a tab only once the record has loaded, and unmounts it again while
 * reloading - so within this component the organization is always there.
 */
const record = computed(() => org.record.value as Organization)
const mayEdit = computed(() => hasOrgRole(record.value.role, 'admin'))

const saving = ref(false)
const deleting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const name = ref('')
const timeZone = ref('')
const weekStart = ref<WeekStart>('monday')
const membersCanCreateProjects = ref(true)

const weekDays: WeekStart[] = [
  'sunday',
  'monday',
  'tuesday',
  'wednesday',
  'thursday',
  'friday',
  'saturday',
]

/** Whatever the browser can offer, so nobody has to remember the IANA spelling. */
const timeZones =
  typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []

/**
 * The form follows the record rather than being seeded once. That covers the save (the
 * API hands back a new `version` the next write has to echo) and the refetch after a
 * conflict with the same code, and it is what makes the dirty check below settle down
 * again afterwards.
 */
watch(
  () => org.record.value,
  (loaded) => {
    if (!loaded) return
    name.value = loaded.name
    timeZone.value = loaded.timeZone
    weekStart.value = loaded.weekStart
    membersCanCreateProjects.value = loaded.membersCanCreateProjects
  },
  { immediate: true },
)

// The tabs are one page as far as the browser is concerned, so a click on "Members" would
// otherwise take a half-typed rename with it and say nothing.
useDirtyGuard(
  () =>
    name.value !== record.value.name ||
    timeZone.value !== record.value.timeZone ||
    weekStart.value !== record.value.weekStart ||
    membersCanCreateProjects.value !== record.value.membersCanCreateProjects,
)

async function save() {
  saving.value = true
  fieldErrors.value = {}

  try {
    const updated = await updateOrganization(record.value.slug, {
      name: name.value.trim(),
      timeZone: timeZone.value,
      weekStart: weekStart.value,
      membersCanCreateProjects: membersCanCreateProjects.value,
      // The xmin we read. The API rejects the write if anyone saved in between.
      version: record.value.version,
    })
    // Publishing it here is what gives the other tabs - and the switcher - the new name
    // and the new version, without a second round trip.
    org.set(updated)
    toast.success('Saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      // Someone else got there first. Show what they saved rather than retrying with a
      // version the server has already moved past.
      toast.error(error)
      await org.reload()
    } else if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}

// ── Delete ──────────────────────────────────────────────────────────────────────────
// Everything goes, and the dialog says so item by item. Only the address survives, retired,
// so an old link can never land on somebody else's organization.
const deleteOpen = ref(false)
const deleteError = ref<string | null>(null)
const deleteConsequences = [
  'every project, with all of its work items, wiki pages, files, sprints and boards',
  'every membership, invitation, team and the audit log',
  'its agents and every access token bound to it',
  'webhooks, GitHub connections, dashboards and notifications',
  'its subscription, which is cancelled immediately',
]

function openDelete() {
  deleteError.value = null
  deleteOpen.value = true
}

async function destroy() {
  deleting.value = true
  deleteError.value = null
  try {
    const { slug, name: deleted } = record.value
    await deleteOrganization(slug, deleted)
    deleteOpen.value = false
    organizations.remove(slug)
    toast.success(`${deleted} was deleted.`)
    await router.replace('/')
  } catch (error) {
    if (error instanceof ApiError)
      deleteError.value =
        Object.values(error.fieldErrors).flat()[0] ?? error.problem?.detail ?? error.title
    else toast.error(error)
  } finally {
    deleting.value = false
  }
}
</script>

<template>
  <div class="space-y-10">
    <SettingsSection
      title="General"
      description="The name everyone here sees, and the two settings that decide when a day starts and ends for all of them."
    >
      <form class="space-y-5" novalidate @submit.prevent="save">
        <div class="space-y-1.5">
          <label for="org-name" class="text-sm font-medium">Name</label>
          <Input
            id="org-name"
            v-model="name"
            required
            :disabled="!mayEdit"
            :aria-invalid="Boolean(fieldErrors.name)"
          />
          <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="org-slug" class="text-sm font-medium">Address</label>
          <Input id="org-slug" :model-value="record.slug" readonly disabled />
          <p class="text-muted-foreground text-xs">
            Permanent. It is in every link your team has bookmarked, pasted into chat, or put into
            an agent's configuration.
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="org-timezone" class="text-sm font-medium">Time zone</label>
          <select
            id="org-timezone"
            v-model="timeZone"
            :disabled="!mayEdit"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
          >
            <option v-if="!timeZones.includes(timeZone)" :value="timeZone">{{ timeZone }}</option>
            <option v-for="zone in timeZones" :key="zone" :value="zone">{{ zone }}</option>
          </select>
          <p class="text-muted-foreground text-xs">
            Decides when "today" ends for due dates and sprint boundaries - one answer for everyone,
            rather than each member's browser.
          </p>
          <p
            v-for="message in fieldErrors.timeZone"
            :key="message"
            class="text-destructive text-xs"
          >
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="org-week-start" class="text-sm font-medium">Week starts on</label>
          <select
            id="org-week-start"
            v-model="weekStart"
            :disabled="!mayEdit"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm capitalize focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
          >
            <option v-for="day in weekDays" :key="day" :value="day">{{ day }}</option>
          </select>
        </div>

        <div class="space-y-1.5">
          <label class="flex items-start gap-2 text-sm">
            <input
              v-model="membersCanCreateProjects"
              type="checkbox"
              class="mt-0.5"
              :disabled="!mayEdit"
            />
            <span>
              <span class="block font-medium">Members can create projects</span>
              <span class="text-muted-foreground text-xs">
                Off, only admins and owners can. Guests never can.
              </span>
            </span>
          </label>
        </div>

        <div v-if="mayEdit" class="flex justify-end">
          <Button type="submit" :disabled="saving || !name.trim()">
            <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
            Save changes
          </Button>
        </div>
        <p v-else class="text-muted-foreground text-xs">
          You are a {{ record.role }} here. Only admins and owners can change these.
        </p>
      </form>
    </SettingsSection>

    <SettingsSection
      v-if="hasOrgRole(record.role, 'owner')"
      title="Delete organization"
      description="Permanently deletes the organization and everything in it. People keep their accounts. The address is retired rather than released, so links to it stay dead instead of pointing at someone else."
    >
      <div
        class="border-destructive/30 flex items-center justify-between gap-4 rounded-lg border p-4"
      >
        <p class="text-muted-foreground text-xs">There is no restore.</p>
        <Button variant="destructive" @click="openDelete">
          <Trash2 aria-hidden="true" />
          Delete organization
        </Button>
      </div>
    </SettingsSection>

    <DeleteConfirmDialog
      v-model:open="deleteOpen"
      :name="record.name"
      :summary="`Deleting removes ${record.name} (${record.slug}), along with:`"
      :consequences="deleteConsequences"
      confirm-label="Delete organization"
      :pending="deleting"
      :error="deleteError"
      @confirm="destroy"
    />
  </div>
</template>
