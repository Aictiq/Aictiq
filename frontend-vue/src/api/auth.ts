import { apiFetch } from '@/utils/api'

/**
 * Sign-in methods other than a password.
 *
 * Starting an OAuth flow is a **top-level navigation**, not a fetch: the first hop is a
 * 302 to another origin, which `fetch` cannot follow and which has to happen in the
 * address bar for the provider's cookies to apply. So these build URLs for
 * `window.location.assign` rather than calling the API.
 */

export interface ExternalProvider {
  /** What goes in the URL: `google`, `github`. */
  name: string
  displayName: string
}

export interface ExternalLogin {
  provider: string
  displayName: string
  /** False when this is the only way into the account — removing it would lock its owner out. */
  canUnlink: boolean
}

/** The refusals the callback can redirect back with, in words a person can act on. */
export const externalSignInErrors: Record<string, string> = {
  'email-unverified':
    'Your provider would not confirm that email address is yours. Verify it with them and try again.',
  'account-exists-link-required':
    'An account already uses that email address. Sign in with your password, then link this provider from your settings.',
  'provider-unavailable': 'That sign-in method is not enabled on this instance.',
  'provider-failed': 'That sign-in did not complete. Please try again.',
}

export function describeExternalError(code: unknown): string | null {
  return typeof code === 'string' ? (externalSignInErrors[code] ?? externalSignInErrors['provider-failed']!) : null
}

export const listProviders = () => apiFetch<ExternalProvider[]>('/auth/providers')

export const listLogins = () => apiFetch<ExternalLogin[]>('/me/logins')

export const unlinkLogin = (provider: string) =>
  apiFetch<void>(`/me/logins/${provider}`, { method: 'DELETE' })

/**
 * Where to send the browser to sign in with a provider. `next` comes back to us after the
 * round trip; the API refuses anything that is not a path on this origin, so a tampered
 * one cannot turn sign-in into an open redirect.
 */
export function externalSignInUrl(
  provider: string,
  options: { next?: string; invite?: string } = {},
): string {
  return `/api/v1/auth/external/${encodeURIComponent(provider)}${query(options)}`
}

/** The same handshake, while already signed in, to attach a provider to this account. */
export function externalLinkUrl(provider: string, next?: string): string {
  return `/api/v1/auth/external/${encodeURIComponent(provider)}/link${query({ next })}`
}

function query(options: { next?: string; invite?: string }): string {
  const parameters = new URLSearchParams()
  if (options.next) parameters.set('next', options.next)
  if (options.invite) parameters.set('invite', options.invite)
  const rendered = parameters.toString()
  return rendered ? `?${rendered}` : ''
}
