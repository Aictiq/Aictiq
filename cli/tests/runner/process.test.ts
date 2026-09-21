import { describe, expect, it } from 'vitest'
import { spawnSupervised } from '../../src/runner/process.js'

const node = process.execPath

describe('spawnSupervised', () => {
  it('delivers stdout and stderr line by line, including a final unterminated line, and feeds stdin', async () => {
    const lines: string[] = []
    const child = spawnSupervised({
      command: node,
      args: [
        '-e',
        "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{process.stdout.write('one\\ntwo:'+s+'\\n');process.stderr.write('err\\n');process.stdout.write('tail')})",
      ],
      cwd: process.cwd(),
      env: process.env,
      stdin: 'prompt',
      onLine: (stream, line) => lines.push(`${stream}:${line}`),
    })
    expect(await child.exited).toBe(0)
    expect(lines).toEqual(
      expect.arrayContaining(['stdout:one', 'stdout:two:prompt', 'stderr:err', 'stdout:tail']),
    )
  })

  it('reports a missing executable as exit 127', async () => {
    const lines: string[] = []
    const child = spawnSupervised({
      command: 'definitely-not-a-harness-xyz',
      args: [],
      cwd: process.cwd(),
      env: process.env,
      onLine: (_s, line) => lines.push(line),
    })
    expect(await child.exited).toBe(127)
    expect(lines[0]).toMatch(/Could not start/)
  })

  it.skipIf(process.platform === 'win32')(
    'kills a process that ignores SIGTERM after the grace period',
    async () => {
      const lines: string[] = []
      const child = spawnSupervised({
        command: node,
        args: [
          '-e',
          "process.on('SIGTERM',()=>console.log('ignoring'));console.log('ready');setInterval(()=>{},1000)",
        ],
        cwd: process.cwd(),
        env: process.env,
        onLine: (_s, line) => lines.push(line),
      })
      while (!lines.includes('ready')) await new Promise((resolve) => setTimeout(resolve, 10))
      const started = Date.now()
      expect(await child.stop(300)).toBeNull()
      expect(Date.now() - started).toBeGreaterThanOrEqual(250)
      expect(lines).toContain('ignoring')
    },
  )
})
