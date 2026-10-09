/**
 * The browser half of WebAuthn, in JSON on both sides.
 *
 * The API speaks the WebAuthn Level 3 JSON forms (`PublicKeyCredentialCreationOptionsJSON`
 * in, `PublicKeyCredential.toJSON()` out). Current browsers convert those natively; the
 * fallbacks below do the same base64url-to-bytes work by hand for the ones that do not
 * yet, so nothing beyond `navigator.credentials` is assumed.
 */

type Json = Record<string, unknown>

export function passkeysSupported(): boolean {
  return typeof window !== 'undefined' && typeof window.PublicKeyCredential === 'function'
}

/**
 * True when the person dismissed the browser's prompt or it timed out. Not an error worth
 * a toast: they chose not to, and the page should simply stay as it was.
 */
export function isCancelled(error: unknown): boolean {
  return (
    error instanceof DOMException &&
    (error.name === 'NotAllowedError' || error.name === 'AbortError')
  )
}

export async function createCredential(options: Json): Promise<unknown> {
  const publicKey =
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function'
      ? PublicKeyCredential.parseCreationOptionsFromJSON(
          options as unknown as PublicKeyCredentialCreationOptionsJSON,
        )
      : decodeCreationOptions(options)

  const credential = (await navigator.credentials.create({
    publicKey,
  })) as PublicKeyCredential | null
  if (!credential) throw new DOMException('No passkey was created.', 'NotAllowedError')
  return toJson(credential)
}

export async function getCredential(options: Json): Promise<unknown> {
  const publicKey =
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function'
      ? PublicKeyCredential.parseRequestOptionsFromJSON(
          options as unknown as PublicKeyCredentialRequestOptionsJSON,
        )
      : decodeRequestOptions(options)

  const credential = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null
  if (!credential) throw new DOMException('No passkey was chosen.', 'NotAllowedError')
  return toJson(credential)
}

function toJson(credential: PublicKeyCredential): unknown {
  if (typeof credential.toJSON === 'function') return credential.toJSON()

  const response = credential.response as AuthenticatorAttestationResponse &
    AuthenticatorAssertionResponse
  const encoded: Json = { clientDataJSON: encode(response.clientDataJSON) }
  if ('attestationObject' in response && response.attestationObject) {
    encoded.attestationObject = encode(response.attestationObject)
    encoded.transports = response.getTransports?.() ?? []
  } else {
    encoded.authenticatorData = encode(response.authenticatorData)
    encoded.signature = encode(response.signature)
    encoded.userHandle = response.userHandle ? encode(response.userHandle) : null
  }

  return {
    id: credential.id,
    rawId: encode(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment ?? undefined,
    clientExtensionResults: credential.getClientExtensionResults(),
    response: encoded,
  }
}

function decodeCreationOptions(options: Json): PublicKeyCredentialCreationOptions {
  const user = options.user as Json
  return {
    ...(options as unknown as PublicKeyCredentialCreationOptions),
    challenge: decode(options.challenge as string),
    user: { ...(user as unknown as PublicKeyCredentialUserEntity), id: decode(user.id as string) },
    excludeCredentials: decodeDescriptors(options.excludeCredentials),
  }
}

function decodeRequestOptions(options: Json): PublicKeyCredentialRequestOptions {
  return {
    ...(options as unknown as PublicKeyCredentialRequestOptions),
    challenge: decode(options.challenge as string),
    allowCredentials: decodeDescriptors(options.allowCredentials),
  }
}

function decodeDescriptors(value: unknown): PublicKeyCredentialDescriptor[] | undefined {
  if (!Array.isArray(value)) return undefined
  return value.map((descriptor: Json) => ({
    ...(descriptor as unknown as PublicKeyCredentialDescriptor),
    id: decode(descriptor.id as string),
  }))
}

function encode(buffer: ArrayBuffer): string {
  let binary = ''
  for (const byte of new Uint8Array(buffer)) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function decode(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/')
  const binary = atob(base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '='))
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes.buffer
}
