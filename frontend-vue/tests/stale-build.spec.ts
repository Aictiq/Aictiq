import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'

import {
  installStaleBuildRecovery,
  isChunkLoadError,
  reloadCooldownMs,
  reloadForNewBuild,
} from '@/router/staleBuild'

const assign = vi.fn()

beforeEach(() => {
  sessionStorage.clear()
  assign.mockReset()
  vi.stubGlobal('location', { ...window.location, href: 'http://localhost/items', assign })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('isChunkLoadError', () => {
  it.each([
    'Failed to fetch dynamically imported module: https://app.aictiq.com/assets/BoardView-abc.js',
    'error loading dynamically imported module: https://app.aictiq.com/assets/BoardView-abc.js',
    'Importing a module script failed.',
    "'text/html' is not a valid JavaScript MIME type.",
    'Unable to preload CSS for /assets/BoardView-abc.css',
  ])('recognises %s', (message) => {
    expect(isChunkLoadError(new TypeError(message))).toBe(true)
  })

  it('leaves other errors alone', () => {
    expect(isChunkLoadError(new Error('Cannot read properties of undefined'))).toBe(false)
    expect(isChunkLoadError(undefined)).toBe(false)
  })
})

describe('reloadForNewBuild', () => {
  const now = Date.parse('2026-10-10T06:30:00Z')

  it('reloads once, then stands down inside the cooldown', () => {
    expect(reloadForNewBuild('/board', now)).toBe(true)
    expect(reloadForNewBuild('/board', now + reloadCooldownMs - 1)).toBe(false)
    expect(assign).toHaveBeenCalledTimes(1)
    expect(assign).toHaveBeenCalledWith('/board')
  })

  it('reloads again once the cooldown has passed', () => {
    reloadForNewBuild('/board', now)
    expect(reloadForNewBuild('/board', now + reloadCooldownMs)).toBe(true)
    expect(assign).toHaveBeenCalledTimes(2)
  })
})

describe('installStaleBuildRecovery', () => {
  function routerWithMissingChunk() {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/items', component: { template: '<div />' } },
        {
          path: '/board',
          component: () =>
            Promise.reject(new TypeError('Failed to fetch dynamically imported module: /assets/BoardView-old.js')),
        },
      ],
    })
    installStaleBuildRecovery(router)
    return router
  }

  it('reloads to the target of a navigation whose chunk is gone', async () => {
    const router = routerWithMissingChunk()
    await router.push('/items')

    await router.push('/board?view=sprint').catch(() => {})

    expect(assign).toHaveBeenCalledWith('/board?view=sprint')
  })

  it('does not reload for an ordinary navigation error', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/broken', component: () => Promise.reject(new Error('boom')) }],
    })
    installStaleBuildRecovery(router)

    await router.push('/broken').catch(() => {})

    expect(assign).not.toHaveBeenCalled()
  })

  it('a preload failure in the same navigation does not reload a second time', async () => {
    vi.useFakeTimers()
    try {
      const router = routerWithMissingChunk()
      await router.push('/items')

      window.dispatchEvent(new Event('vite:preloadError'))
      await router.push('/board').catch(() => {})
      vi.runAllTimers()

      expect(assign).toHaveBeenCalledTimes(1)
      expect(assign).toHaveBeenCalledWith('/board')
    } finally {
      vi.useRealTimers()
    }
  })
})
