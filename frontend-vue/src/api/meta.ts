import { apiFetch } from '@/utils/api'

export interface ReleaseMetadata {
  version: string
  updateCheck: {
    enabled: boolean
    repository: string | null
  }
}

interface GitHubRelease {
  tag_name: string
  html_url: string
  draft: boolean
  prerelease: boolean
}

export interface AvailableRelease {
  version: string
  url: string
}

/** Public and intentionally tiny: it is safe before sign-in and has no tenant data. */
export function getReleaseMetadata() {
  return apiFetch<ReleaseMetadata>('/meta')
}

/**
 * This request is made only when a self-hosting operator enabled it in the API's
 * Release:UpdateCheck configuration. Keeping it in the browser avoids adding a polling
 * worker or a GitHub credential to every installation.
 */
export async function getLatestGitHubRelease(repository: string): Promise<AvailableRelease | null> {
  const response = await fetch(`https://api.github.com/repos/${repository}/releases/latest`, {
    headers: { Accept: 'application/vnd.github+json' },
  })
  if (!response.ok) throw new Error(`GitHub update check failed (${response.status})`)

  const release = (await response.json()) as GitHubRelease
  if (release.draft || release.prerelease) return null

  const version = release.tag_name.replace(/^v/, '')
  return version ? { version, url: release.html_url } : null
}

/** SemVer comparison for release tags. Unknown/dev versions never claim an update. */
export function isNewerRelease(current: string, candidate: string): boolean {
  const parse = (value: string) => /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$/.exec(value)
  const installed = parse(current)
  const available = parse(candidate)
  if (!installed || !available || installed[4] || available[4]) return false

  for (let index = 1; index <= 3; index += 1) {
    const difference = Number(available[index]) - Number(installed[index])
    if (difference !== 0) return difference > 0
  }
  return false
}
