<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import { listAgents, type Agent } from '@/api/agents'
import { listGitHubBindings, type RepoBinding } from '@/api/github'
import {
  getFactorySettings,
  updateFactorySettings,
  type FactorySettings,
  type RepositorySource,
} from '@/api/playbooks'
import { listProjectMembers } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { projectSettingsPath } from '@/router/paths'
import { ApiError, ConflictError } from '@/utils/api'

const project = useProjectScope()
const toast = useToast()

const settings = ref<FactorySettings | null>(null)
const bindings = ref<RepoBinding[]>([])
const agents = ref<Agent[]>([])
const loading = ref(true)
const failed = ref(false)
const saving = ref(false)

const repoSource = ref<RepositorySource>(1)
const repoFullName = ref('')
const defaultBranch = ref('main')
const localPathHint = ref('')
const defaultAgentId = ref('')

const record = computed(() => project.record.value)
const mayRead = computed(() => record.value?.role !== 'guest')
const mayManage = computed(() => record.value?.role === 'admin' && !record.value.isArchived)
const validRepository = computed(
  () => repoSource.value === 1 || repoFullName.value.trim().length > 0,
)

async function availableBindings() {
  try {
    return await listGitHubBindings(project.slug.value, project.projectKey.value)
  } catch (error) {
    // An instance without a configured GitHub App does not map these routes.
    if (error instanceof ApiError && error.status === 404) return []
    throw error
  }
}

function apply(value: FactorySettings) {
  settings.value = value
  repoSource.value = value.repoSource
  repoFullName.value = value.repoFullName ?? ''
  defaultBranch.value = value.defaultBranch
  localPathHint.value = value.localPathHint ?? ''
  defaultAgentId.value = value.defaultAgentId ?? ''
}

async function load() {
  if (!mayRead.value) {
    loading.value = false
    return
  }
  loading.value = true
  failed.value = false
  try {
    const [loadedSettings, loadedBindings, members, organizationAgents] = await Promise.all([
      getFactorySettings(project.slug.value, project.projectKey.value),
      availableBindings(),
      listProjectMembers(project.slug.value, project.projectKey.value),
      listAgents(project.slug.value),
    ])
    bindings.value = loadedBindings
    const assignable = new Set(
      members.filter((member) => member.isAgent).map((member) => member.userId),
    )
    agents.value = organizationAgents
      .filter((agent) => agent.isActive && assignable.has(agent.userId))
      .sort((left, right) => left.displayName.localeCompare(right.displayName))
    apply(loadedSettings)
  } catch (error) {
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([project.slug, project.projectKey], load, { immediate: true })

async function save() {
  if (!settings.value || !mayManage.value || !validRepository.value) return
  saving.value = true
  try {
    const saved = await updateFactorySettings(project.slug.value, project.projectKey.value, {
      repoSource: repoSource.value,
      repoFullName: repoSource.value === 0 ? repoFullName.value.trim() : null,
      defaultBranch: defaultBranch.value.trim() || 'main',
      localPathHint: repoSource.value === 1 ? localPathHint.value.trim() || null : null,
      defaultAgentId: defaultAgentId.value || null,
      version: settings.value.version,
    })
    apply(saved)
    toast.success('Factory settings saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      toast.error(new Error('Someone changed these settings first. The latest values are shown.'))
      await load()
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <SettingsSection
    title="Factory"
    description="Tell runners where this project's code lives and which agent to use by default."
  >
    <UiPageState v-if="loading" state="loading" />
    <EmptyState
      v-else-if="!mayRead"
      title="Factory settings are for project Members"
      description="Ask a project Admin to raise your project role if you need these settings."
      icon="◇"
    />
    <UiPageState
      v-else-if="failed"
      state="error"
      title="Could not load factory settings"
      description="Try the request again."
    >
      <Button variant="secondary" @click="load">Try again</Button>
    </UiPageState>

    <form v-else-if="settings" class="space-y-5" @submit.prevent="save">
      <fieldset class="space-y-2">
        <legend class="text-sm font-medium">Repository source</legend>
        <label class="border-border flex cursor-pointer gap-3 rounded-lg border p-3 text-sm">
          <input v-model="repoSource" type="radio" :value="0" :disabled="!mayManage" />
          <span>
            <span class="block font-medium">GitHub binding</span>
            <span class="text-muted-foreground mt-0.5 block text-xs">
              Clone a repository already connected to this project.
            </span>
          </span>
        </label>
        <label class="border-border flex cursor-pointer gap-3 rounded-lg border p-3 text-sm">
          <input v-model="repoSource" type="radio" :value="1" :disabled="!mayManage" />
          <span>
            <span class="block font-medium">Runner-local checkout</span>
            <span class="text-muted-foreground mt-0.5 block text-xs">
              Use a repository that is already present on the runner machine.
            </span>
          </span>
        </label>
      </fieldset>

      <div v-if="repoSource === 0" class="space-y-1.5">
        <label for="factory-repository" class="text-sm font-medium">Repository</label>
        <select
          id="factory-repository"
          v-model="repoFullName"
          class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
          :disabled="!mayManage"
        >
          <option value="">Choose a bound repository</option>
          <option v-for="binding in bindings" :key="binding.id" :value="binding.fullName">
            {{ binding.fullName }}
          </option>
        </select>
        <p v-if="bindings.length === 0" class="text-muted-foreground text-xs">
          No repository is bound yet.
          <RouterLink
            class="text-primary hover:underline"
            :to="projectSettingsPath(project.slug.value, project.projectKey.value, 'integrations')"
          >
            Connect one under Integrations.
          </RouterLink>
        </p>
      </div>

      <div v-else class="space-y-1.5">
        <label for="factory-local-path" class="text-sm font-medium">Path hint</label>
        <Input
          id="factory-local-path"
          v-model="localPathHint"
          :disabled="!mayManage"
          placeholder="/srv/repos/aictiq"
        />
        <p class="text-muted-foreground text-xs">
          Runners use this path when it lies inside one of their repository roots
          (<code>aictiq runner root</code>); a runner's own <code>aictiq runner map</code> wins.
        </p>
      </div>

      <div class="space-y-1.5">
        <label for="factory-default-branch" class="text-sm font-medium">Default branch</label>
        <Input
          id="factory-default-branch"
          v-model="defaultBranch"
          :disabled="!mayManage"
          placeholder="main"
        />
      </div>

      <div class="space-y-1.5">
        <label for="factory-default-agent" class="text-sm font-medium">Default agent</label>
        <select
          id="factory-default-agent"
          v-model="defaultAgentId"
          class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
          :disabled="!mayManage"
        >
          <option value="">Choose at run time</option>
          <option v-for="agent in agents" :key="agent.userId" :value="agent.userId">
            {{ agent.displayName }}
          </option>
        </select>
        <p class="text-muted-foreground text-xs">
          Only active agents that can open this project are available.
        </p>
      </div>

      <p v-if="record?.isArchived" class="text-muted-foreground text-xs">
        This project is archived. Restore it under General to change factory settings.
      </p>
      <p v-else-if="record?.role !== 'admin'" class="text-muted-foreground text-xs">
        Project Admins manage factory settings.
      </p>
      <div v-else class="flex justify-end">
        <Button type="submit" :disabled="saving || !validRepository">
          <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
          Save factory settings
        </Button>
      </div>
    </form>
  </SettingsSection>
</template>
