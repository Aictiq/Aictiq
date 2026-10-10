import type { Router } from 'vue-router'

/**
 * Every deploy replaces the hashed assets, so a tab still running the previous build
 * asks for chunks that are gone. A lazy route then fails to load, the navigation is
 * rejected and RouterView renders nothing: an empty page until someone reloads by hand.
 * A long-idle tab is the usual case, since it is the one that slept through a deploy.
 *
 * The answer is the reload they would have done: a full page load to where they were
 * going, which fetches the new index.html and the assets it names.
 */

const reloadedAtKey = 'aictiq:stale-build-reload'

/**
 * A reload inside this window means the fresh build failed too - a chunk really is
 * missing, or the network is down - and reloading again would loop. The error is left
 * to surface instead.
 */
export const reloadCooldownMs = 10_000

/** What each engine says when a module script or Vite's preload of one fails. */
const chunkErrorPatterns = [
  /Failed to fetch dynamically imported module/i, // Chromium
  /error loading dynamically imported module/i, // Firefox
  /Importing a module script failed/i, // Safari
  /is not a valid JavaScript MIME type/i,
  /Unable to preload CSS/i, // Vite
]

export function isChunkLoadError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '')
  return chunkErrorPatterns.some((pattern) => pattern.test(message))
}

/**
 * Loads `url` afresh unless this tab already did so moments ago. Returns whether it did.
 * sessionStorage is per tab and survives the reload, which is what the guard needs.
 */
export function reloadForNewBuild(url: string, now = Date.now()): boolean {
  try {
    const last = Number(sessionStorage.getItem(reloadedAtKey) ?? 0)
    if (now - last < reloadCooldownMs) return false
    sessionStorage.setItem(reloadedAtKey, String(now))
  } catch {
    // Storage blocked: without a guard a reload could loop, so leave the error be.
    return false
  }
  window.location.assign(url)
  return true
}

export function installStaleBuildRecovery(router: Router) {
  router.onError((error, to) => {
    if (isChunkLoadError(error)) reloadForNewBuild(to.fullPath)
  })

  // Vite raises this when a chunk's preloaded dependencies fail, outside any navigation
  // too. The error is still thrown, so a navigation hears of it in onError and reloads
  // to its target first; the deferred reload here then finds the guard set and stands
  // down, and only a failure no navigation owns falls through to reloading this page.
  window.addEventListener('vite:preloadError', () => {
    setTimeout(() => reloadForNewBuild(window.location.href), 0)
  })
}
