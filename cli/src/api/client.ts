import { CliError, ExitCode, exitCodeForStatus, formatProblem } from '../errors.js'
import type { ProblemDetails } from '../errors.js'
import type { paths } from './schema.js'

export type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete'

/** The documented routes that support a given method - a typo in a path is a type error. */
export type PathFor<M extends HttpMethod> = {
  [P in keyof paths]: paths[P] extends { [K in M]: object } ? P : never
}[keyof paths]

type Operation<P extends keyof paths, M extends HttpMethod> = paths[P] extends {
  [K in M]: infer O
}
  ? O
  : never

/**
 * A C# record's optional members are nullable rather than absent, and the OpenAPI document
 * says nothing about which are required, so every documented property comes through as
 * required-and-nullable. `Partial` is the honest reading: omitting a field is what the API
 * treats as "not mentioned". Excess-property checking still applies, so a renamed or
 * dropped field is a compile error here - which is the point of generating these at all.
 */
type Loosen<B> = B extends object ? Partial<B> : B

/**
 * The request body the API documents for this route. Response bodies are not in the
 * OpenAPI document yet (the endpoints return untyped `IResult`), so
 * callers name the response shape themselves from `views.ts`.
 */
export type BodyFor<P extends PathFor<M>, M extends HttpMethod> =
  Operation<P, M> extends { requestBody: { content: { 'application/json': infer B } } }
    ? Loosen<B>
    : Operation<P, M> extends { requestBody?: { content: { 'application/json': infer B } } }
      ? Loosen<B> | undefined
      : undefined

export interface RequestOptions<P extends PathFor<M>, M extends HttpMethod> {
  path?: Record<string, string>
  query?: Record<string, string | number | boolean | undefined>
  body?: BodyFor<P, M>
}

export interface ClientOptions {
  baseUrl: string
  token: string
  fetch?: typeof fetch
}

/**
 * The whole HTTP surface of the CLI. Bearer credentials are not subject to the cookie
 * CSRF rule, so `X-Aictiq-Request` is deliberately absent: sending it on a bearer request
 * would be harmless but misleading about which transport authenticated the call.
 */
export class AictiqClient {
  readonly baseUrl: string
  private readonly token: string
  private readonly doFetch: typeof fetch

  constructor(options: ClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '')
    this.token = options.token
    this.doFetch = options.fetch ?? fetch
  }

  async request<T, P extends PathFor<M>, M extends HttpMethod = 'get'>(
    method: M,
    path: P,
    options: RequestOptions<P, M> = {},
  ): Promise<T> {
    const url = new URL(this.baseUrl + expand(path as string, options.path ?? {}))
    for (const [key, value] of Object.entries(options.query ?? {})) {
      if (value !== undefined && value !== '') url.searchParams.set(key, String(value))
    }

    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.token}`,
      Accept: 'application/json',
      'User-Agent': 'aictiq-cli',
    }
    if (options.body !== undefined) headers['Content-Type'] = 'application/json'

    let response: Response
    try {
      response = await this.doFetch(url, {
        method: method.toUpperCase(),
        headers,
        ...(options.body === undefined ? {} : { body: JSON.stringify(options.body) }),
      })
    } catch (cause) {
      throw new CliError(
        `Cannot reach ${this.baseUrl}: ${cause instanceof Error ? cause.message : String(cause)}`,
        ExitCode.Error,
      )
    }

    if (response.status === 204) return undefined as T
    const text = await response.text()
    const payload: unknown = text.length === 0 ? undefined : safeJson(text)

    if (!response.ok) {
      const problem = isProblem(payload) ? payload : undefined
      throw new CliError(
        formatProblem(problem, `${response.status} ${response.statusText}`),
        exitCodeForStatus(response.status),
        problem,
      )
    }
    return payload as T
  }

  /**
   * Downloads an authenticated response without trying to interpret it as JSON. Attachments
   * deliberately use the normal API route (rather than a presigned object-store URL), so a
   * PAT's organization and project visibility checks remain in force.
   */
  async download<P extends PathFor<'get'>>(
    path: P,
    options: RequestOptions<P, 'get'> = {},
  ): Promise<Uint8Array> {
    const url = new URL(this.baseUrl + expand(path as string, options.path ?? {}))
    for (const [key, value] of Object.entries(options.query ?? {})) {
      if (value !== undefined && value !== '') url.searchParams.set(key, String(value))
    }
    let response: Response
    try {
      response = await this.doFetch(url, {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${this.token}`,
          Accept: '*/*',
          'User-Agent': 'aictiq-cli',
        },
      })
    } catch (cause) {
      throw new CliError(
        `Cannot reach ${this.baseUrl}: ${cause instanceof Error ? cause.message : String(cause)}`,
        ExitCode.Error,
      )
    }
    if (!response.ok) {
      const text = await response.text()
      const payload: unknown = text.length === 0 ? undefined : safeJson(text)
      const problem = isProblem(payload) ? payload : undefined
      throw new CliError(
        formatProblem(problem, `${response.status} ${response.statusText}`),
        exitCodeForStatus(response.status),
        problem,
      )
    }
    return new Uint8Array(await response.arrayBuffer())
  }
}

/** Fills `{name}` segments; a missing value is a programming error, not a user one. */
function expand(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = values[name]
    if (value === undefined) throw new Error(`Missing path parameter "${name}" for ${template}`)
    return encodeURIComponent(value)
  })
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return { title: text.slice(0, 500) }
  }
}

function isProblem(value: unknown): value is ProblemDetails {
  return typeof value === 'object' && value !== null
}
