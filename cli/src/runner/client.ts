import type { ProblemDetails } from '../errors.js'
import type {
  ClaimedRun,
  FinishReport,
  LogStream,
  RunAttachment,
  RunnerCapabilities,
  RunnerHello,
} from './types.js'

export interface LogChunk {
  seq: number
  stream: LogStream
  text: string
  at: string
}

/** A refused request: the status and problem type are what the runner decides on. */
export class RunnerHttpError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | undefined

  constructor(status: number, problem: ProblemDetails | undefined, message: string) {
    super(message)
    this.name = 'RunnerHttpError'
    this.status = status
    this.problem = problem
  }

  /** A disabled, deleted or rotated runner: retrying cannot help, so the process must stop. */
  get revoked(): boolean {
    return this.status === 401
  }
}

/** The instance could not be reached at all — the only failure worth a backoff and retry. */
export class RunnerNetworkError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'RunnerNetworkError'
  }
}

export interface RunnerClientOptions {
  baseUrl: string
  token: string
  fetch?: typeof fetch
}

/**
 * The runner protocol (`/api/v1/runner/*`). Not the generated
 * `AictiqClient`: these routes take a `jrn_` secret that no person's command should ever
 * hold, and the item heartbeat below is the one call made with the run's agent token.
 */
export class RunnerClient {
  readonly baseUrl: string
  private readonly token: string
  private readonly doFetch: typeof fetch

  constructor(options: RunnerClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '')
    this.token = options.token
    this.doFetch = options.fetch ?? fetch
  }

  hello(capabilities: RunnerCapabilities): Promise<RunnerHello> {
    return this.send<RunnerHello>('/runner/hello', { capabilities })
  }

  async heartbeat(capabilities: RunnerCapabilities): Promise<void> {
    await this.send('/runner/heartbeat', { capabilities })
  }

  /** Long-polls; null when the server had nothing within its poll timeout. */
  async claim(
    harnesses: string[],
    slots: number,
    signal?: AbortSignal,
  ): Promise<ClaimedRun | null> {
    return (await this.send<ClaimedRun>('/runner/runs/claim', { harnesses, slots }, signal)) ?? null
  }

  async started(runId: string): Promise<void> {
    await this.send(`/runner/runs/${runId}/started`)
  }

  async log(runId: string, chunks: LogChunk[]): Promise<void> {
    await this.send(`/runner/runs/${runId}/log`, { chunks })
  }

  async runHeartbeat(runId: string): Promise<{ cancelRequested: boolean }> {
    return this.send<{ cancelRequested: boolean }>(`/runner/runs/${runId}/heartbeat`)
  }

  async finish(runId: string, report: FinishReport): Promise<void> {
    await this.send(`/runner/runs/${runId}/finish`, report)
  }

  async repoToken(runId: string): Promise<string | null> {
    const response = await this.send<{ token: string | null }>(`/runner/runs/${runId}/repo-token`)
    return response?.token ?? null
  }

  /** Lists committed attachments on an item, including attachments posted in its comments. */
  listAttachments(run: ClaimedRun): Promise<RunAttachment[]> {
    return this.get<RunAttachment[]>(
      `/orgs/${encodeURIComponent(run.organizationSlug)}/items/${encodeURIComponent(run.itemKey)}/attachments?include=comments`,
      run.agentToken,
    )
  }

  /** Downloads attachment bytes using the short-lived agent credential, never the runner secret. */
  async downloadAttachment(run: ClaimedRun, attachmentId: string): Promise<Uint8Array> {
    const path = `/orgs/${encodeURIComponent(run.organizationSlug)}/attachments/${encodeURIComponent(attachmentId)}/download`
    const response = await this.request(path, 'GET', undefined, undefined, run.agentToken)
    if (!response.ok) throw await this.error(path, response)
    return new Uint8Array(await response.arrayBuffer())
  }

  /**
   * Keeps the item's claim fresh with the agent's own token. Without it the claim goes
   * stale after `Claims:StaleAfterMinutes` and another agent may take the item mid-run.
   */
  async itemHeartbeat(run: ClaimedRun): Promise<void> {
    await this.send(
      `/orgs/${encodeURIComponent(run.organizationSlug)}/items/${encodeURIComponent(run.itemKey)}/heartbeat`,
      undefined,
      undefined,
      run.agentToken,
    )
  }

  private async send<T>(
    path: string,
    body?: unknown,
    signal?: AbortSignal,
    token = this.token,
  ): Promise<T> {
    const response = await this.request(path, 'POST', body, signal, token)

    if (response.status === 204) return undefined as T
    if (!response.ok) throw await this.error(path, response)
    const text = await response.text()
    let payload: unknown
    try {
      payload = text.length === 0 ? undefined : JSON.parse(text)
    } catch {
      payload = undefined
    }
    return payload as T
  }

  private async get<T>(path: string, token: string): Promise<T> {
    const response = await this.request(path, 'GET', undefined, undefined, token)
    if (!response.ok) throw await this.error(path, response)
    return (await response.json()) as T
  }

  private async request(path: string, method: 'GET' | 'POST', body?: unknown, signal?: AbortSignal, token = this.token): Promise<Response> {
    try {
      return await this.doFetch(`${this.baseUrl}/api/v1${path}`, {
        method,
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: 'application/json',
          'User-Agent': 'aictiq-runner',
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
        ...(signal ? { signal } : {}),
      })
    } catch (cause) {
      if (signal?.aborted) throw cause
      throw new RunnerNetworkError(`Cannot reach ${this.baseUrl}: ${cause instanceof Error ? cause.message : String(cause)}`)
    }
  }

  private async error(path: string, response: Response): Promise<RunnerHttpError | RunnerNetworkError> {
    // A gateway in front of the instance answering 502 is the network, not a verdict.
    if (response.status >= 502 && response.status <= 504) return new RunnerNetworkError(`${this.baseUrl} answered ${response.status}`)
    const text = await response.text()
    let payload: unknown
    try { payload = text.length === 0 ? undefined : JSON.parse(text) } catch { payload = undefined }
    const problem = typeof payload === 'object' && payload !== null ? (payload as ProblemDetails) : undefined
    return new RunnerHttpError(response.status, problem, `${path}: ${response.status} ${problem?.title ?? response.statusText}${problem?.detail ? ` — ${problem.detail}` : ''}`)
  }
}
