import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'

import { getLatestGitHubRelease, getReleaseMetadata, isNewerRelease } from '@/api/meta'

/**
 * The running version and, when the operator allows it, whether a newer release exists.
 * The queries are shared through vue-query's cache, so the About page and the sidebar's
 * menu ask the API and GitHub once between them.
 */
export function useRelease() {
  const metadata = useQuery({
    queryKey: ['release-metadata'],
    queryFn: getReleaseMetadata,
    staleTime: Infinity,
    retry: false,
  })

  // The server has to explicitly opt in before a browser contacts GitHub. Failed checks
  // are silent: an unavailable update service must never make the application noisy.
  // The sidebar runs this on every page, so a /meta answer of the wrong shape (an older
  // API, a proxy's error page) must read as "no update check" rather than throw.
  const repository = computed(() => {
    const check = metadata.data.value?.updateCheck
    return check?.enabled === true && typeof check.repository === 'string'
      ? check.repository
      : null
  })
  const latest = useQuery({
    queryKey: computed(() => ['release-update', repository.value]),
    queryFn: () => getLatestGitHubRelease(repository.value!),
    enabled: computed(() => repository.value !== null),
    staleTime: 24 * 60 * 60_000,
    retry: false,
  })

  const version = computed(() => {
    const value = metadata.data.value?.version
    return typeof value === 'string' ? value : null
  })
  const update = computed(() => {
    const current = version.value
    const candidate = latest.data.value
    return current !== null && candidate && isNewerRelease(current, candidate.version)
      ? candidate
      : null
  })

  return { version, update }
}
