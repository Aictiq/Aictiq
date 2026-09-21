import { spawn } from 'node:child_process'
import type { ChildProcess } from 'node:child_process'
import { StringDecoder } from 'node:string_decoder'

export interface SupervisedProcess {
  readonly pid: number | undefined
  /** Resolves with the exit code, or null when a signal ended it. */
  readonly exited: Promise<number | null>
  /** SIGTERM to the whole process group, then SIGKILL after `graceMs` if it is still there. */
  stop(graceMs?: number): Promise<number | null>
}

export interface SpawnOptions {
  command: string
  args: string[]
  cwd: string
  env: NodeJS.ProcessEnv
  stdin?: string
  onLine: (stream: 'stdout' | 'stderr', line: string) => void
}

/**
 * Starts a harness in its own process group (`detached`), so that stopping it stops what it
 * started too: a coding agent routinely leaves a dev server or a test watcher behind, and
 * signalling only the harness's pid would orphan them on the runner.
 */
export function spawnSupervised(options: SpawnOptions): SupervisedProcess {
  const child = spawn(options.command, options.args, {
    cwd: options.cwd,
    env: options.env,
    detached: process.platform !== 'win32',
    stdio: ['pipe', 'pipe', 'pipe'],
  })

  const exited = new Promise<number | null>((resolve) => {
    let settled = false
    const settle = (code: number | null) => {
      if (settled) return
      settled = true
      resolve(code)
    }
    // A missing executable arrives as 'error' with no 'close'; 127 is the shell's answer.
    child.on('error', (error: NodeJS.ErrnoException) => {
      options.onLine('stderr', `Could not start ${options.command}: ${error.message}`)
      settle(error.code === 'ENOENT' ? 127 : 1)
    })
    child.on('close', (code) => {
      settle(code)
      signal(child, 'SIGKILL')
    })
    // 'close' waits for stdout to close, which a background process the harness left
    // behind keeps open indefinitely. Its exit is the verdict; the stragglers get two
    // seconds to flush and then go with the group.
    child.on('exit', (code) => {
      setTimeout(() => {
        settle(code)
        signal(child, 'SIGKILL')
      }, 2000)
    })
  })

  splitLines(child, 'stdout', options.onLine)
  splitLines(child, 'stderr', options.onLine)
  child.stdin?.on('error', () => {
    // The harness may exit before reading its prompt; EPIPE is not the runner's failure.
  })
  child.stdin?.end(options.stdin ?? '')

  return {
    pid: child.pid,
    exited,
    async stop(graceMs = 10_000) {
      signal(child, 'SIGTERM')
      const timer = setTimeout(() => signal(child, 'SIGKILL'), graceMs)
      try {
        return await exited
      } finally {
        clearTimeout(timer)
        // The harness may be gone while its children linger; the group goes with it.
        signal(child, 'SIGKILL')
      }
    },
  }
}

function signal(child: ChildProcess, name: NodeJS.Signals): void {
  if (child.pid === undefined) return
  try {
    if (process.platform === 'win32') child.kill(name)
    else process.kill(-child.pid, name)
  } catch {
    // ESRCH: already gone.
  }
}

function splitLines(
  child: ChildProcess,
  stream: 'stdout' | 'stderr',
  onLine: (stream: 'stdout' | 'stderr', line: string) => void,
): void {
  const source = child[stream]
  if (!source) return
  const decoder = new StringDecoder('utf8')
  let buffer = ''
  source.on('data', (data: Buffer) => {
    buffer += decoder.write(data)
    let index: number
    while ((index = buffer.indexOf('\n')) >= 0) {
      onLine(stream, buffer.slice(0, index).replace(/\r$/, ''))
      buffer = buffer.slice(index + 1)
    }
  })
  source.on('end', () => {
    buffer += decoder.end()
    if (buffer.length > 0) onLine(stream, buffer)
    buffer = ''
  })
}
