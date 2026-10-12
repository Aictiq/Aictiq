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
  const latest = useQuery({
    queryKey: computed(() => ['release-update', metadata.data.value?.updateCheck.repository]),
    queryFn: () => getLatestGitHubRelease(metadata.data.value!.updateCheck.repository!),
    enabled: computed(
      () =>
        metadata.data.value?.updateCheck.enabled === true &&
        metadata.data.value.updateCheck.repository !== null,
    ),
    staleTime: 24 * 60 * 60_000,
    retry: false,
  })

  const version = computed(() => metadata.data.value?.version ?? null)
  const update = computed(() => {
    const current = version.value
    const candidate = latest.data.value
    return current !== null && candidate && isNewerRelease(current, candidate.version)
      ? candidate
      : null
  })

  return { version, update }
}
