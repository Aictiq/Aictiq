import { apiFetch } from '@/utils/api'
import { createCredential, getCredential } from '@/utils/webauthn'

/**
 * The second ways in, beside a password: an authenticator app (two-factor) and passkeys.
 *
 * Both are opt-in and live in Security settings only - nothing here is part of
 * registration. A passkey is a whole sign-in on its own; an authenticator code is the
 * second half of a password sign-in.
 */

export interface TwoFactorStatus {
  enabled: boolean
  recoveryCodesLeft: number
}

export interface TwoFactorSetup {
  /** The secret grouped in fours, for typing into an app by hand. */
  sharedKey: string
  /** The `otpauth://` URI the QR code encodes. */
  authenticatorUri: string
}

/** Proof for turning two-factor off or replacing the codes: the password, or a current code. */
export interface TwoFactorProof {
  currentPassword?: string
  code?: string
}

export interface Passkey {
  /** The credential id, base64url. */
  id: string
  name: string
  createdAt: string
  /** Synced to the person's other devices by their platform (iCloud Keychain, Google). */
  isBackedUp: boolean
}

interface PasskeyOptions {
  ticket: string
  options: Record<string, unknown>
}

export const getTwoFactor = () => apiFetch<TwoFactorStatus>('/me/two-factor')

export const startTwoFactorSetup = () =>
  apiFetch<TwoFactorSetup>('/me/two-factor/setup', { method: 'POST' })

/** Returns the recovery codes. They are shown once and cannot be fetched again. */
export const enableTwoFactor = (code: string) =>
  apiFetch<{ codes: string[] }>('/me/two-factor/enable', { method: 'POST', body: { code } })

export const disableTwoFactor = (proof: TwoFactorProof) =>
  apiFetch<void>('/me/two-factor/disable', { method: 'POST', body: proof })

export const regenerateRecoveryCodes = (proof: TwoFactorProof) =>
  apiFetch<{ codes: string[] }>('/me/two-factor/recovery-codes', { method: 'POST', body: proof })

export const listPasskeys = () => apiFetch<Passkey[]>('/me/passkeys')

/**
 * The whole ceremony: ask the API for options, let the browser make the credential (this
 * is where the person touches their key or unlocks their phone), and hand it back.
 */
export async function addPasskey(name: string): Promise<Passkey> {
  const { ticket, options } = await apiFetch<PasskeyOptions>('/me/passkeys/options', {
    method: 'POST',
  })
  const credential = await createCredential(options)
  return apiFetch<Passkey>('/me/passkeys', { method: 'POST', body: { ticket, credential, name } })
}

export const renamePasskey = (id: string, name: string) =>
  apiFetch<Passkey>(`/me/passkeys/${encodeURIComponent(id)}`, { method: 'PATCH', body: { name } })

export const removePasskey = (id: string) =>
  apiFetch<void>(`/me/passkeys/${encodeURIComponent(id)}`, { method: 'DELETE' })

/** The sign-in half: options for any passkey this site has, then the browser's answer. */
export async function passkeySignInCredential(): Promise<{ ticket: string; credential: unknown }> {
  const { ticket, options } = await apiFetch<PasskeyOptions>('/auth/passkey/options', {
    method: 'POST',
  })
  return { ticket, credential: await getCredential(options) }
}
