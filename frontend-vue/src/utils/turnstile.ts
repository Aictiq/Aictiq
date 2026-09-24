import { ref } from 'vue'

import { apiFetch } from '@/utils/api'

/**
 * Cloudflare Turnstile, on the anonymous forms of an instance that has it configured.
 *
 * Whether it is on is the API's call, not a build flag: the site key comes from
 * `/auth/challenge` at runtime, so one image serves a public deployment that challenges
 * and a private one that does not. The widget only produces a token - the API checks it
 * with Cloudflare before it lets the form through, which is the part that stops a bot.
 */

/** The header the API reads the solved token from. */
export const TURNSTILE_HEADER = 'X-Turnstile-Token'

const SCRIPT_URL = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'

export interface TurnstileRenderOptions {
  sitekey: string
  action?: string
  theme?: 'auto' | 'light' | 'dark'
  size?: 'normal' | 'flexible' | 'compact'
  callback?: (token: string) => void
  'expired-callback'?: () => void
  'error-callback'?: () => void
  'timeout-callback'?: () => void
}

export interface TurnstileApi {
  render(container: HTMLElement, options: TurnstileRenderOptions): string
  reset(widgetId: string): void
  remove(widgetId: string): void
}

declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

/**
 * The public site key, or null when this instance does not challenge. `undefined` only
 * until the first answer arrives. Shared, so every form on the page asks once.
 */
export const turnstileSiteKey = ref<string | null | undefined>(undefined)

let siteKeyRequest: Promise<string | null> | null = null

export function loadTurnstileSiteKey(): Promise<string | null> {
  siteKeyRequest ??= apiFetch<{ turnstileSiteKey: string | null }>('/auth/challenge')
    .then((info) => info.turnstileSiteKey)
    .catch(() => {
      // Asked again next time rather than cached as "off": a blip here must not leave a
      // form without the widget the API is going to insist on.
      siteKeyRequest = null
      return null
    })
    .then((key) => {
      turnstileSiteKey.value = key
      return key
    })

  return siteKeyRequest
}

let scriptRequest: Promise<TurnstileApi> | null = null

/** Loads Cloudflare's script once per page, however many widgets ask for it. */
export function loadTurnstileScript(): Promise<TurnstileApi> {
  if (window.turnstile) return Promise.resolve(window.turnstile)

  scriptRequest ??= new Promise<TurnstileApi>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SCRIPT_URL
    script.async = true
    script.onload = () =>
      window.turnstile ? resolve(window.turnstile) : reject(new Error('Turnstile did not load.'))
    script.onerror = () => {
      scriptRequest = null
      script.remove()
      reject(new Error('Turnstile could not be loaded.'))
    }
    document.head.appendChild(script)
  })

  return scriptRequest
}

/** Request headers carrying a solved token, or none when there is nothing to send. */
export function turnstileHeaders(token: string | null | undefined): Record<string, string> {
  return token ? { [TURNSTILE_HEADER]: token } : {}
}

/**
 * Whether a form may be submitted as far as the challenge is concerned: always on an
 * instance without Turnstile, and only with a solved token on one that has it. Still
 * false while the site key is being fetched, so a fast click cannot beat the widget.
 */
export function turnstileReady(token: string | null | undefined): boolean {
  return turnstileSiteKey.value === null || Boolean(token)
}
