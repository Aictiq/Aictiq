import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { useSessionStore } from '@/stores/session'
import {
  TURNSTILE_HEADER,
  turnstileHeaders,
  turnstileReady,
  turnstileSiteKey,
} from '@/utils/turnstile'

/**
 * The client half of the anonymous-form protections: the solved Turnstile token rides in
 * a header, forms wait for it only when the instance has Turnstile, and registering tells
 * the page whether it signed in or is waiting for a confirmation link.
 */

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

beforeEach(() => {
  setActivePinia(createPinia())
  turnstileSiteKey.value = undefined
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('turnstile helpers', () => {
  it('sends the token only when there is one', () => {
    expect(turnstileHeaders('abc')).toEqual({ [TURNSTILE_HEADER]: 'abc' })
    expect(turnstileHeaders(null)).toEqual({})
    expect(turnstileHeaders(undefined)).toEqual({})
  })

  it('holds a form until the widget solves, but only where Turnstile is on', () => {
    // Still asking the API: not ready, so a fast click cannot beat the widget.
    expect(turnstileReady(null)).toBe(false)

    turnstileSiteKey.value = 'site-key'
    expect(turnstileReady(null)).toBe(false)
    expect(turnstileReady('solved')).toBe(true)

    turnstileSiteKey.value = null
    expect(turnstileReady(null)).toBe(true)
  })
})

describe('session.register', () => {
  it('reports a pending confirmation on 202 without signing in', async () => {
    const fetch = vi.fn(() =>
      Promise.resolve(jsonResponse(202, { email: 'new@example.com', emailConfirmationRequired: true })),
    )
    vi.stubGlobal('fetch', fetch)
    const session = useSessionStore()

    const outcome = await session.register(
      { email: 'new@example.com', password: 'x'.repeat(12), firstName: 'N', lastName: 'P' },
      'solved-token',
    )

    expect(outcome).toEqual({ status: 'confirmation-sent', email: 'new@example.com' })
    expect(session.isAuthenticated).toBe(false)
    const init = (fetch.mock.calls[0] as unknown as [string, RequestInit])[1]
    expect(new Headers(init.headers).get(TURNSTILE_HEADER)).toBe('solved-token')
  })

  it('signs in on 201', async () => {
    const user = {
      id: 'u1',
      email: 'invited@example.com',
      firstName: 'I',
      lastName: 'N',
      fullName: 'I N',
      roles: ['User'],
      isAgent: false,
      avatarKey: null,
      timeZone: null,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(201, { user }))))
    const session = useSessionStore()

    const outcome = await session.register({
      email: 'invited@example.com',
      password: 'x'.repeat(12),
      firstName: 'I',
      lastName: 'N',
      invitationToken: 'invite-token',
    })

    expect(outcome).toEqual({ status: 'signed-in' })
    expect(session.user?.email).toBe('invited@example.com')
  })
})
