/**
 * Exit codes are part of the CLI's contract (03-API-MCP-CLI.md §4): a script or an agent
 * decides what to do next from the code alone, without parsing prose. 3 (conflict) is the
 * one worth acting on - refetch and retry with the fresh version - which is why it is
 * distinct from 2 (the request was wrong and retrying it will fail again).
 */
export const ExitCode = {
  Ok: 0,
  Error: 1,
  Validation: 2,
  Conflict: 3,
  NotFound: 4,
  Auth: 5,
} as const

export type ExitCodeValue = (typeof ExitCode)[keyof typeof ExitCode]

/** RFC 9457 problem+json, as the API emits it. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}

export class CliError extends Error {
  readonly exitCode: ExitCodeValue
  readonly problem: ProblemDetails | undefined

  constructor(message: string, exitCode: ExitCodeValue = ExitCode.Error, problem?: ProblemDetails) {
    super(message)
    this.name = 'CliError'
    this.exitCode = exitCode
    this.problem = problem
  }
}

export function exitCodeForStatus(status: number): ExitCodeValue {
  if (status === 401) return ExitCode.Auth
  if (status === 403) return ExitCode.Auth
  if (status === 404) return ExitCode.NotFound
  if (status === 409) return ExitCode.Conflict
  if (status >= 400 && status < 500) return ExitCode.Validation
  return ExitCode.Error
}

/** The human rendering of a failure: the problem title, then each field error. */
export function formatProblem(problem: ProblemDetails | undefined, fallback: string): string {
  const lines: string[] = [problem?.title?.trim() || fallback]
  if (problem?.detail && problem.detail !== problem.title) lines.push(problem.detail)
  for (const [field, messages] of Object.entries(problem?.errors ?? {})) {
    for (const message of messages) lines.push(`  ${field}: ${message}`)
  }
  return lines.join('\n')
}
