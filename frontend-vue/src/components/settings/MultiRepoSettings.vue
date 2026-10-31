<script setup lang="ts">
import { Plus, Trash2 } from '@lucide/vue'
import { computed } from 'vue'
import { RouterLink } from 'vue-router'

import type { RepoBinding } from '@/api/github'
import type { MultiRepoAvailability, ProjectRepository } from '@/api/playbooks'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { runnerMapCommand } from '@/lib/runners'
import { orgSettingsPath } from '@/router/paths'
import {
  aliasProblem,
  blankRepository,
  multiRepoDocsUrl,
  workingDirectoryProblem,
} from '@/lib/repositories'

/**
 * The opt-in part of Factory settings for teams whose work spans several repositories or one
 * package of a monorepo. Until someone turns it on it is a single line saying it exists.
 */
const props = defineProps<{
  availability: MultiRepoAvailability
  mayManage: boolean
  bindings: RepoBinding[]
  slug: string
  projectKey: string
  errors: string[]
}>()

const enabled = defineModel<boolean>('enabled', { required: true })
const primaryAlias = defineModel<string>('primaryAlias', { required: true })
const primaryWorkingDirectory = defineModel<string>('primaryWorkingDirectory', { required: true })
const repositories = defineModel<ProjectRepository[]>('repositories', { required: true })

const available = computed(() => props.availability === 'available')
const editable = computed(() => available.value && props.mayManage)

function aliasesBut(index: number): string[] {
  return [primaryAlias.value.trim(), ...repositories.value.map((repo) => repo.alias.trim())].filter(
    (_, position) => position !== index + 1,
  )
}

const primaryAliasProblem = computed(() =>
  aliasProblem(
    primaryAlias.value.trim(),
    repositories.value.map((repo) => repo.alias.trim()),
  ),
)

function repositoryProblems(repo: ProjectRepository, index: number): string[] {
  const problems: string[] = []
  const alias = aliasProblem(repo.alias.trim(), aliasesBut(index))
  if (alias) problems.push(alias)
  if (repo.repoSource === 0 && !repo.repoFullName?.trim()) {
    problems.push('Choose a bound GitHub repository.')
  }
  const directory = workingDirectoryProblem(repo.workingDirectory ?? '')
  if (directory) problems.push(directory)
  return problems
}

/** False while anything entered here would be refused; the form keeps its Save button off. */
const valid = computed(
  () =>
    !enabled.value ||
    !available.value ||
    (!primaryAliasProblem.value &&
      !workingDirectoryProblem(primaryWorkingDirectory.value) &&
      repositories.value.every((repo, index) => repositoryProblems(repo, index).length === 0)),
)
defineExpose({ valid })

/** Maps an additional runner-local repository as `KEY/alias` on one runner. */
function mapCommand(repo: ProjectRepository) {
  const alias = repo.alias.trim() || '<alias>'
  return runnerMapCommand(`${props.projectKey}/${alias}`, repo.localPathHint ?? '', props.slug)
}

function add() {
  repositories.value = [...repositories.value, blankRepository()]
}

function remove(index: number) {
  repositories.value = repositories.value.filter((_, position) => position !== index)
}
</script>

<template>
  <div v-if="availability !== 'hidden'" data-testid="multi-repo">
    <p
      v-if="!enabled && availability === 'upgrade'"
      class="text-muted-foreground text-xs"
      data-testid="multi-repo-note"
    >
      Monorepos and multiple repositories are supported on paid plans.
      <RouterLink
        :to="orgSettingsPath(slug, 'billing')"
        class="text-primary hover:underline"
        data-testid="multi-repo-upgrade-link"
        >Upgrade</RouterLink
      >
      <span class="mx-1" aria-hidden="true">·</span>
      <a
        :href="multiRepoDocsUrl"
        class="text-primary hover:underline"
        target="_blank"
        rel="noopener"
        >Docs</a
      >
    </p>
    <p v-else-if="!enabled" class="text-muted-foreground text-xs" data-testid="multi-repo-note">
      Monorepos and multiple repositories are supported.
      <button
        v-if="mayManage"
        type="button"
        class="text-primary hover:underline"
        data-testid="multi-repo-turn-on"
        @click="enabled = true"
      >
        Turn on
      </button>
      <span class="mx-1" aria-hidden="true">·</span>
      <a
        :href="multiRepoDocsUrl"
        class="text-primary hover:underline"
        target="_blank"
        rel="noopener"
        >Docs</a
      >
    </p>

    <fieldset
      v-else
      class="border-border space-y-4 rounded-lg border p-3"
      data-testid="multi-repo-settings"
    >
      <label class="flex cursor-pointer items-start gap-3 text-sm">
        <input
          v-model="enabled"
          type="checkbox"
          class="mt-0.5"
          :disabled="!editable"
          data-testid="multi-repo-switch"
        />
        <span>
          <span class="block font-medium">Advanced: multiple repositories / monorepo</span>
          <span class="text-muted-foreground mt-0.5 block text-xs">
            Tickets and playbooks can name the repositories a run works in, each checked out at
            <code>repo/&lt;alias&gt;/</code>, and the directory the agent starts in.
            <a
              :href="multiRepoDocsUrl"
              class="text-primary hover:underline"
              target="_blank"
              rel="noopener"
              >How it works</a
            >
          </span>
        </span>
      </label>

      <p
        v-if="!available"
        class="rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-xs"
        data-testid="multi-repo-paused"
      >
        This plan no longer includes multiple repositories, so new runs use the primary repository
        only. The setup below is kept for when it does.
        <RouterLink
          :to="orgSettingsPath(slug, 'billing')"
          class="text-primary hover:underline"
          data-testid="multi-repo-upgrade-link"
          >Upgrade</RouterLink
        >
      </p>

      <div class="space-y-3">
        <p class="text-sm font-medium">Primary repository</p>
        <p class="text-muted-foreground text-xs">
          The repository above. Runs use it when neither the ticket nor the playbook names one, and
          project chat and scheduled runs always do.
        </p>
        <div class="grid gap-3 sm:grid-cols-2">
          <div class="space-y-1.5">
            <label for="multi-repo-primary-alias" class="text-xs font-medium">Alias</label>
            <Input
              id="multi-repo-primary-alias"
              v-model="primaryAlias"
              :disabled="!editable"
              placeholder="backend"
            />
            <p v-if="primaryAliasProblem" class="text-destructive text-xs">
              {{ primaryAliasProblem }}
            </p>
          </div>
          <div class="space-y-1.5">
            <label for="multi-repo-primary-directory" class="text-xs font-medium"
              >Working directory</label
            >
            <Input
              id="multi-repo-primary-directory"
              v-model="primaryWorkingDirectory"
              :disabled="!editable"
              placeholder="packages/api"
            />
            <p
              v-if="workingDirectoryProblem(primaryWorkingDirectory)"
              class="text-destructive text-xs"
            >
              {{ workingDirectoryProblem(primaryWorkingDirectory) }}
            </p>
          </div>
        </div>
      </div>

      <div class="space-y-3">
        <p class="text-sm font-medium">Additional repositories</p>
        <p v-if="repositories.length === 0" class="text-muted-foreground text-xs">
          None yet. Add the other repositories this project's work touches.
        </p>
        <div
          v-for="(repo, index) in repositories"
          :key="index"
          class="border-border space-y-3 rounded-md border p-3"
          data-testid="multi-repo-entry"
        >
          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label :for="`multi-repo-alias-${index}`" class="text-xs font-medium">Alias</label>
              <Input
                :id="`multi-repo-alias-${index}`"
                v-model="repo.alias"
                :disabled="!editable"
                placeholder="frontend"
              />
            </div>
            <div class="space-y-1.5">
              <label :for="`multi-repo-source-${index}`" class="text-xs font-medium">Source</label>
              <select
                :id="`multi-repo-source-${index}`"
                v-model="repo.repoSource"
                class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
                :disabled="!editable"
              >
                <option :value="0">GitHub binding</option>
                <option :value="1">Runner-local checkout</option>
              </select>
            </div>
            <div v-if="repo.repoSource === 0" class="space-y-1.5">
              <label :for="`multi-repo-github-${index}`" class="text-xs font-medium"
                >Repository</label
              >
              <select
                :id="`multi-repo-github-${index}`"
                v-model="repo.repoFullName"
                class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm disabled:opacity-50"
                :disabled="!editable"
              >
                <option :value="null">Choose a bound repository</option>
                <option v-for="binding in bindings" :key="binding.id" :value="binding.fullName">
                  {{ binding.fullName }}
                </option>
              </select>
            </div>
            <div v-else class="space-y-1.5">
              <label :for="`multi-repo-path-${index}`" class="text-xs font-medium">Path hint</label>
              <Input
                :id="`multi-repo-path-${index}`"
                :model-value="repo.localPathHint ?? ''"
                :disabled="!editable"
                placeholder="/srv/repos/frontend"
                @update:model-value="repo.localPathHint = String($event) || null"
              />
            </div>
            <div class="space-y-1.5">
              <label :for="`multi-repo-branch-${index}`" class="text-xs font-medium"
                >Default branch</label
              >
              <Input
                :id="`multi-repo-branch-${index}`"
                v-model="repo.defaultBranch"
                :disabled="!editable"
                placeholder="main"
              />
            </div>
            <div class="space-y-1.5">
              <label :for="`multi-repo-directory-${index}`" class="text-xs font-medium"
                >Working directory</label
              >
              <Input
                :id="`multi-repo-directory-${index}`"
                :model-value="repo.workingDirectory ?? ''"
                :disabled="!editable"
                placeholder="packages/web"
                @update:model-value="repo.workingDirectory = String($event) || null"
              />
            </div>
          </div>
          <p v-if="repo.repoSource === 1" class="text-muted-foreground text-xs">
            Or map it on one runner:
            <code class="font-mono text-[11px]">{{ mapCommand(repo) }}</code>
          </p>
          <ul
            v-if="repositoryProblems(repo, index).length > 0"
            class="text-destructive list-inside list-disc text-xs"
          >
            <li v-for="problem in repositoryProblems(repo, index)" :key="problem">{{ problem }}</li>
          </ul>
          <div v-if="editable" class="flex justify-end">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              :aria-label="`Remove ${repo.alias || 'this repository'}`"
              @click="remove(index)"
            >
              <Trash2 class="size-4" aria-hidden="true" />
              Remove
            </Button>
          </div>
        </div>
        <Button
          v-if="editable"
          type="button"
          variant="secondary"
          size="sm"
          data-testid="multi-repo-add"
          @click="add"
        >
          <Plus class="size-4" aria-hidden="true" />
          Add repository
        </Button>
      </div>

      <ul
        v-if="errors.length > 0"
        class="text-destructive list-inside list-disc text-xs"
        data-testid="multi-repo-errors"
      >
        <li v-for="error in errors" :key="error">{{ error }}</li>
      </ul>
    </fieldset>
  </div>
</template>
