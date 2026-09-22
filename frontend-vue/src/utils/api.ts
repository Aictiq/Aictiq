/**
 * The one way the SPA talks to the backend.
 *
 * The app is same-origin with the API (Vite proxies `/api` in dev, the API serves the
 * built SPA in production), so authentication is entirely cookie-based and invisible to
 * JavaScript: `credentials: 'include'` is all this client does about it. Never read,
 * write or store a token here - there is nothing to store.
 *
 * `X-Aictiq-Request: 1` is the CSRF guard the API requires on every non-GET request: a
 * cross-site form post cannot set a custom header, and the header alone is not a
 * credential, so it costs nothing to send it on every request.
 *
 * Errors are normalised to ApiError carrying the backend's RFC 9457 problem details.
 */

import { ofetch, type FetchOptions } from 'ofetch'

/** RFC 9457 problem+json, plus the extensions the API adds. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  traceId?: string
  /** Validation failures keyed by request field, camelCase, as the API emits them. */
  errors?: Record<string, string[]>
  [key: string]: unknown
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly title: string,
    public readonly problem?: ProblemDetails,
  ) {
    super(title)
    this.name = 'ApiError'
  }

  /** Validation errors keyed by request field (camelCase), for inline display. */
  get fieldErrors(): Record<string, string[]> {
    return this.problem?.errors ?? {}
  }

  /** 401: the session is gone or was never there. Route guards send these to /login. */
  get isUnauthenticated() {
    return this.status === 401
  }
}

/** 400/422 with per-field messages. Bind `fieldErrors` straight onto the form. */
export class ValidationError extends ApiError {
  constructor(status: number, title: string, problem?: ProblemDetails) {
    super(status, title, problem)
    this.name = 'ValidationError'
  }
}

/**
 * 409: someone else saved first, or a referenced record moved. Refetch and show the
 * server's state - never retry the write with the stale `version`.
 */
export class ConflictError extends ApiError {
  constructor(status: number, title: string, problem?: ProblemDetails) {
    super(status, title, problem)
    this.name = 'ConflictError'
  }
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error

  const fetchError = error as { status?: number; statusCode?: number; data?: ProblemDetails }
  const problem = fetchError?.data
  const status = problem?.status ?? fetchError?.status ?? fetchError?.statusCode ?? 0
  const title = problem?.title ?? (status ? `Request failed (${status})` : 'Network error')

  if (status === 409) return new ConflictError(status, title, problem)
  if ((status === 400 || status === 422) && problem?.errors) {
    return new ValidationError(status, title, problem)
  }
  return new ApiError(status, title, problem)
}

export const CSRF_HEADER = 'X-Aictiq-Request'

const client = ofetch.create({
  baseURL: '/api/v1',
  credentials: 'include',
  headers: { [CSRF_HEADER]: '1' },
  retry: false,
})

export type ApiRequestOptions = Omit<FetchOptions<'json'>, 'baseURL' | 'credentials'>

/**
 * Endpoints where a 401 is the answer, not a stale-token symptom. Refreshing after a
 * wrong password or a refusal to refresh would just loop.
 */
const NO_REFRESH = ['/auth/refresh', '/auth/login', '/auth/register', '/auth/logout']

let refreshInFlight: Promise<boolean> | null = null

/**
 * Rotates the session cookies, at most once at a time. Several requests can 401 together
 * when the 15-minute access cookie expires; without the single flight each would spend a
 * refresh token, and the API revokes a whole family when a spent token is replayed - so
 * a burst of parallel refreshes would log the user out rather than keep them in.
 */
function refreshSession(): Promise<boolean> {
  refreshInFlight ??= client('/auth/refresh', { method: 'POST' })
    .then(
      () => true,
      () => false,
    )
    .finally(() => {
      refreshInFlight = null
    })

  return refreshInFlight
}

type UnauthenticatedHandler = () => void

let handleUnauthenticated: UnauthenticatedHandler = () => {}

/**
 * Called when a request is still 401 after a refresh - the session is genuinely over.
 * Registered in `main.ts` so this module never imports the router or a store.
 */
export function setUnauthenticatedHandler(handler: UnauthenticatedHandler) {
  handleUnauthenticated = handler
}

/** Calls `/api/v1{path}`. Throws {@link ApiError} for anything that is not 2xx. */
export async function apiFetch<T = unknown>(
  path: string,
  options: ApiRequestOptions = {},
): Promise<T> {
  try {
    return await client<T>(path, options)
  } catch (error) {
    const apiError = toApiError(error)

    if (!apiError.isUnauthenticated || NO_REFRESH.some((p) => path.startsWith(p))) {
      throw apiError
    }

    if (!(await refreshSession())) {
      handleUnauthenticated()
      throw apiError
    }

    // One retry only: a second 401 means the new cookies do not help either.
    try {
      return await client<T>(path, options)
    } catch (retryError) {
      const retried = toApiError(retryError)
      if (retried.isUnauthenticated) {
        handleUnauthenticated()
      }
      throw retried
    }
  }
}
