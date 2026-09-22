import { apiFetch } from '@/utils/api'

/**
 * A person's own account: profile, picture, password, address, sessions.
 *
 * The picture is the only part that is not a plain JSON round trip. Bytes never pass
 * through the API - `startAvatarUpload` returns a presigned URL, the browser PUTs to the
 * object store directly, and `commitAvatar` tells the API which key to record. The PUT is
 * therefore the one `fetch` in this app that does *not* go through `apiFetch`: it is a
 * different origin, must not carry our cookies, and must send exactly the content type
 * the API signed.
 */

export interface Profile {
  id: string
  email: string
  firstName: string
  lastName: string
  fullName: string
  /** IANA id, or null to follow the organization's. */
  timeZone: string | null
  avatarKey: string | null
  /** Changes whenever the picture does - hang it on the avatar URL so caches turn over. */
  avatarVersion: string | null
  emailConfirmed: boolean
  /** False for an account that has only ever signed in with a provider. */
  hasPassword: boolean
  isAgent: boolean
  roles: string[]
  createdAt: string
  /** An address asked for but not yet confirmed from its own inbox. */
  pendingEmail: string | null
}

export interface Session {
  id: string
  userAgent: string | null
  startedAt: string
  lastSeenAt: string
  expiresAt: string
  isCurrent: boolean
}

export interface AvatarUploadTicket {
  key: string
  uploadUrl: string
  expiresAt: string
  maxBytes: number
}

/** 202 from the flows that send mail. `emailConfigured` false means nothing will arrive. */
export interface RecoveryAccepted {
  emailConfigured: boolean
}

export const avatarContentTypes = ['image/png', 'image/jpeg', 'image/webp', 'image/gif']

export const getProfile = () => apiFetch<Profile>('/me')

export const updateProfile = (body: {
  firstName?: string
  lastName?: string
  /** Empty string clears the override and follows the organization. */
  timeZone?: string
}) => apiFetch<Profile>('/me', { method: 'PATCH', body })

export const changePassword = (body: { currentPassword?: string; newPassword: string }) =>
  apiFetch<void>('/me/password', { method: 'POST', body })

export const requestEmailChange = (body: { newEmail: string; currentPassword?: string }) =>
  apiFetch<RecoveryAccepted>('/me/email', { method: 'POST', body })

export const cancelEmailChange = () => apiFetch<void>('/me/email', { method: 'DELETE' })

export const listSessions = () => apiFetch<Session[]>('/me/sessions')

export const revokeSession = (id: string) =>
  apiFetch<void>(`/me/sessions/${id}`, { method: 'DELETE' })

/** Everywhere except this browser - the one you are pressing the button in stays. */
export const revokeOtherSessions = () => apiFetch<void>('/me/sessions', { method: 'DELETE' })

export const forgotPassword = (email: string) =>
  apiFetch<RecoveryAccepted>('/auth/forgot', { method: 'POST', body: { email } })

export const resetPassword = (token: string, newPassword: string) =>
  apiFetch<void>('/auth/reset', { method: 'POST', body: { token, newPassword } })

export const confirmEmailChange = (token: string) =>
  apiFetch<{ userId: string; email: string }>('/auth/email-change', {
    method: 'POST',
    body: { token },
  })

const startAvatarUpload = (contentType: string, contentLength: number) =>
  apiFetch<AvatarUploadTicket>('/me/avatar', {
    method: 'POST',
    body: { contentType, contentLength },
  })

const commitAvatar = (key: string) => apiFetch<Profile>('/me/avatar', { method: 'PUT', body: { key } })

export const removeAvatar = () => apiFetch<void>('/me/avatar', { method: 'DELETE' })

/**
 * The whole three-step upload. The PUT goes straight to the object store with no cookies
 * and no CSRF header - it is another origin, and the signature is the only credential it
 * accepts. `Content-Type` must match what the API signed exactly, or the store rejects it.
 */
export async function uploadAvatar(file: File): Promise<Profile> {
  const ticket = await startAvatarUpload(file.type, file.size)

  const response = await fetch(ticket.uploadUrl, {
    method: 'PUT',
    body: file,
    headers: { 'Content-Type': file.type },
  })

  if (!response.ok) {
    throw new Error('The picture could not be uploaded. Try again.')
  }

  return commitAvatar(ticket.key)
}

/**
 * Where to point an `<img>` for someone's picture. Null when they have none, so the
 * caller falls back to initials rather than requesting a 404 on every row.
 *
 * The version in the query string is what makes this URL safe to cache for an hour: a new
 * picture has a new key and therefore a different URL.
 */
export function avatarUrl(userId: string, avatarKey: string | null | undefined): string | null {
  if (!avatarKey) return null
  const version = avatarKey.slice(avatarKey.lastIndexOf('/') + 1)
  return `/api/v1/users/${encodeURIComponent(userId)}/avatar?v=${encodeURIComponent(version)}`
}

/** "Firefox on Linux" out of a user-agent string, or null when there is nothing to read. */
export function describeDevice(userAgent: string | null): string {
  if (!userAgent) return 'Unknown device'

  const browser =
    /Edg\//.test(userAgent) ? 'Edge'
    : /OPR\//.test(userAgent) ? 'Opera'
    : /Firefox\//.test(userAgent) ? 'Firefox'
    : /Chrome\//.test(userAgent) ? 'Chrome'
    : /Safari\//.test(userAgent) ? 'Safari'
    : /curl\//i.test(userAgent) ? 'curl'
    : null

  const platform =
    /Android/.test(userAgent) ? 'Android'
    : /iPhone|iPad|iPod/.test(userAgent) ? 'iOS'
    : /Mac OS X/.test(userAgent) ? 'macOS'
    : /Windows/.test(userAgent) ? 'Windows'
    : /Linux/.test(userAgent) ? 'Linux'
    : null

  if (browser && platform) return `${browser} on ${platform}`
  return browser ?? platform ?? userAgent.slice(0, 60)
}
