<script setup lang="ts">
import { computed, ref, watch } from 'vue'

import {
  bindGitHubRepository,
  listGitHubBindings,
  listGitHubInstallations,
  listGitHubRepositories,
  unbindGitHubRepository,
  updateGitHubPullRequestRules,
  type GitHubRepository,
  type RepoBinding,
} from '@/api/github'
import { listWorkflows, type WorkflowState } from '@/api/workflows'
import GitHubTransitionRulesEditor from '@/components/settings/GitHubTransitionRulesEditor.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'

const project = useProjectScope()
const toast = useToast()
const bindings = ref<RepoBinding[]>([])
const repositories = ref<GitHubRepository[]>([])
const states = ref<WorkflowState[]>([])
const loading = ref(true)
// Mirrors OrgWebhooksView: an instance with no GitHub App configured does not map the
// routes at all, so 404 here means "this deployment has no GitHub", not a fault the
// administrator can fix from this screen. Hide the section rather than toast at them.
const githubAvailable = ref(true)
const savingRepoId = ref<number | null>(null)
const query = ref('')

const availableRepositories = computed(() => {
  const bound = new Set(bindings.value.map((binding) => binding.repoId))
  const needle = query.value.trim().toLowerCase()
  return repositories.value.filter(
    (repository) =>
      !bound.has(repository.id) && (!needle || repository.fullName.toLowerCase().includes(needle)),
  )
})

async function load() {
  loading.value = true
  try {
    const [nextBindings, workflows, installations] = await Promise.all([
      listGitHubBindings(project.slug.value, project.projectKey.value),
      listWorkflows(project.slug.value, project.projectKey.value),
      listGitHubInstallations(project.slug.value),
    ])
    bindings.value = nextBindings
    states.value = workflows.flatMap((workflow) => workflow.states)
    const active = installations.filter((installation) => installation.status === 'active')
    repositories.value = (
      await Promise.all(
        active.map((installation) =>
          listGitHubRepositories(project.slug.value, installation.installationId),
        ),
      )
    ).flat()
    githubAvailable.value = true
  } catch (error) {
    bindings.value = []
    repositories.value = []
    if ((error as { status?: number }).status === 404) githubAvailable.value = false
    else toast.error(error)
  } finally {
    loading.value = false
  }
}

async function bind(repository: GitHubRepository) {
  savingRepoId.value = repository.id
  try {
    const binding = await bindGitHubRepository(
      project.slug.value,
      project.projectKey.value,
      repository.id,
    )
    bindings.value = [...bindings.value, binding].sort((left, right) =>
      left.fullName.localeCompare(right.fullName),
    )
    query.value = ''
  } catch (error) {
    toast.error(error)
  } finally {
    savingRepoId.value = null
  }
}

async function unbind(binding: RepoBinding) {
  savingRepoId.value = binding.repoId
  try {
    await unbindGitHubRepository(project.slug.value, project.projectKey.value, binding.repoId)
    bindings.value = bindings.value.filter((candidate) => candidate.repoId !== binding.repoId)
  } catch (error) {
    toast.error(error)
  } finally {
    savingRepoId.value = null
  }
}

async function saveRules(
  binding: RepoBinding,
  rules: Pick<RepoBinding, 'onPullRequestOpenedStateId' | 'onPullRequestMergedStateId'>,
) {
  savingRepoId.value = binding.repoId
  try {
    const saved = await updateGitHubPullRequestRules(
      project.slug.value,
      project.projectKey.value,
      binding.repoId,
      rules,
    )
    bindings.value = bindings.value.map((candidate) =>
      candidate.repoId === saved.repoId ? saved : candidate,
    )
  } catch (error) {
    toast.error(error)
  } finally {
    savingRepoId.value = null
  }
}

watch([project.slug, project.projectKey], load, { immediate: true })
</script>

<template>
  <SettingsSection
    wide
    title="GitHub"
    description="Connect repositories and choose how pull requests move work through this project's workflow."
  >
    <UiPageState v-if="loading" state="loading" />
    <p v-else-if="!githubAvailable" class="text-muted-foreground text-sm">
      This Aictiq instance has no GitHub App configured, so there is nothing to connect here.
    </p>
    <div v-else class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div class="grid gap-3">
        <article
          v-for="binding in bindings"
          :key="binding.id"
          class="border-border rounded-lg border p-4"
        >
          <div class="flex items-start justify-between gap-3">
            <div>
              <p class="font-mono text-sm font-medium">{{ binding.fullName }}</p>
              <p class="text-muted-foreground mt-0.5 text-xs">Repository bound to this project</p>
            </div>
            <Button
              size="sm"
              variant="ghost"
              :disabled="savingRepoId === binding.repoId"
              @click="unbind(binding)"
              >Unbind</Button
            >
          </div>
          <GitHubTransitionRulesEditor
            :binding="binding"
            :states="states"
            :saving="savingRepoId === binding.repoId"
            class="mt-4"
            @save="saveRules(binding, $event)"
          />
        </article>
        <p
          v-if="!bindings.length"
          class="text-muted-foreground rounded-lg border border-dashed p-5 text-sm"
        >
          No GitHub repositories are bound to this project yet.
        </p>
      </div>
      <aside class="border-border h-fit rounded-lg border p-4">
        <h3 class="text-sm font-medium">Bind a repository</h3>
        <p class="text-muted-foreground mt-1 text-xs">
          Search repositories installed for this organization.
        </p>
        <input
          v-model="query"
          class="border-input bg-background mt-3 h-8 w-full rounded border px-2 text-sm"
          placeholder="Search repositories"
          aria-label="Search installed repositories"
        />
        <ul class="mt-3 grid gap-1">
          <li
            v-for="repository in availableRepositories"
            :key="repository.id"
            class="flex items-center justify-between gap-2 py-1"
          >
            <code class="min-w-0 truncate text-xs">{{ repository.fullName }}</code
            ><Button size="xs" :disabled="savingRepoId === repository.id" @click="bind(repository)"
              >Bind</Button
            >
          </li>
        </ul>
        <p v-if="!availableRepositories.length" class="text-muted-foreground mt-3 text-xs">
          No matching installed repositories.
        </p>
      </aside>
    </div>
  </SettingsSection>
</template>
