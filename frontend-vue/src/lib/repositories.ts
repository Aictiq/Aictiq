import type { ProjectRepository } from '@/api/playbooks'

/** The public guide built from `docs/guide/multiple-repositories.md`. */
export const multiRepoDocsUrl = 'https://aictiq.github.io/Aictiq/guide/multiple-repositories'

/** Lower-case letters, digits, `.`, `_` and `-`, up to 40 characters; mirrors the API. */
export const repositoryAliasPattern = /^[a-z0-9][a-z0-9._-]{0,39}$/

/** Why an alias cannot be used, or null when it can. */
export function aliasProblem(alias: string, taken: string[]): string | null {
  if (!repositoryAliasPattern.test(alias)) {
    return 'Use lower-case letters, digits, ".", "_" or "-", up to 40 characters.'
  }
  return taken.includes(alias) ? 'Another repository already uses this alias.' : null
}

/** Why a working directory cannot be used, or null when it can (blank is the checkout's root). */
export function workingDirectoryProblem(value: string): string | null {
  const path = value.trim()
  if (!path) return null
  if (path.length > 500) return 'Use 500 characters or fewer.'
  if (path.startsWith('/') || path.startsWith('\\') || /^[A-Za-z]:/.test(path)) {
    return 'Use a path relative to the repository, such as packages/api.'
  }
  if (path.split(/[\\/]/).includes('..')) return 'The path may not leave the repository.'
  return null
}

export function blankRepository(): ProjectRepository {
  return {
    alias: '',
    repoSource: 0,
    repoFullName: null,
    localPathHint: null,
    defaultBranch: 'main',
    workingDirectory: null,
  }
}

/** The field errors of the multi-repository part of a factory settings save. */
export function multiRepoErrors(fieldErrors: Record<string, string[]>): string[] {
  return Object.entries(fieldErrors)
    .filter(([field]) =>
      /^(multiRepoEnabled|primaryAlias|primaryWorkingDirectory|additionalRepositories)/i.test(
        field,
      ),
    )
    .flatMap(([, messages]) => messages)
}

/** `owner/repo#12` for a GitHub pull request URL, the URL itself otherwise. */
export function pullRequestLabel(url: string): string {
  const match = /github\.com\/([^/]+\/[^/]+)\/pull\/(\d+)/.exec(url)
  return match ? `${match[1]}#${match[2]}` : url
}
