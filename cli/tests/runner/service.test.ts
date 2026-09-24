import { spawn } from 'node:child_process'
import { chmodSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  launchdAgent,
  scheduledTaskInstaller,
  serviceDefinition,
  servicePlatform,
  systemdUnit,
  type ServiceOptions,
} from '../../src/runner/service.js'

const options: ServiceOptions = {
  node: '/usr/local/bin/node',
  entry: '/opt/aictiq/dist/index.js',
  parallel: '2',
  path: '/usr/local/bin:/usr/bin:/bin',
  home: '/Users/ana',
}

/** The `<string>` values of the plist's ProgramArguments, unescaped. */
function programArguments(plist: string): string[] {
  const array = /<key>ProgramArguments<\/key>\s*<array>([\s\S]*?)<\/array>/.exec(plist)?.[1] ?? ''
  return [...array.matchAll(/<string>([\s\S]*?)<\/string>/g)].map((m) =>
    m[1]!
      .replace(/&quot;/g, '"')
      .replace(/&gt;/g, '>')
      .replace(/&lt;/g, '<')
      .replace(/&amp;/g, '&'),
  )
}

describe('install-service', () => {
  it('picks the definition for this machine', () => {
    expect(servicePlatform('linux')).toBe('linux')
    expect(servicePlatform('darwin')).toBe('macos')
    expect(servicePlatform('win32')).toBe('windows')
    expect(servicePlatform('freebsd')).toBe('linux')
    expect(serviceDefinition('macos', options)).toBe(launchdAgent(options))
    expect(serviceDefinition('windows', options)).toBe(scheduledTaskInstaller(options))
    expect(serviceDefinition('linux', options)).toBe(systemdUnit(options))
  })

  it('keeps the systemd unit: no restart after a revoked secret', () => {
    const unit = systemdUnit(options)
    expect(unit).toContain(
      'ExecStart=/usr/local/bin/node /opt/aictiq/dist/index.js runner start --parallel 2',
    )
    expect(unit).toContain('RestartPreventExitStatus=5')
    expect(unit).toContain('Environment=PATH=/usr/local/bin:/usr/bin:/bin')
  })

  describe('launchd agent', () => {
    it('escapes paths and keeps the comment valid XML', () => {
      const plist = launchdAgent({ ...options, node: '/opt/a&b/<node>', home: '/Users/x--y' })
      const args = programArguments(plist)

      expect(args.slice(0, 2)).toEqual(['/bin/sh', '-c'])
      expect(args.slice(3)).toEqual(['aictiq-runner', '/opt/a&b/<node>', options.entry, '2'])
      const comment = /<!--([\s\S]*?)-->/.exec(plist)?.[1] ?? ''
      expect(comment).not.toContain('--')
      expect(plist).toContain('<string>/Users/x--y/Library/Logs/aictiq-runner.log</string>')
      expect(plist).toContain('<key>SuccessfulExit</key>\n    <false/>')
    })

    // The wrapper is plain POSIX sh, so its behaviour is checked here for real: a fake
    // "node" stands in for the runner and records what it was given and what it received.
    const wrapper = () => programArguments(launchdAgent(options))[2]!
    const fakeNode = (body: string) => {
      const dir = mkdtempSync(join(tmpdir(), 'aictiq-service-'))
      const node = join(dir, 'node')
      writeFileSync(node, `#!/bin/sh\necho "$@" > "${dir}/args"\n${body}\n`)
      chmodSync(node, 0o755)
      return { dir, node }
    }
    const run = (node: string, onStart?: (pid: number) => void) =>
      new Promise<number | null>((resolve) => {
        const child = spawn('/bin/sh', ['-c', wrapper(), 'aictiq-runner', node, '/entry.js', '2'])
        if (onStart) setTimeout(() => onStart(child.pid!), 300)
        child.on('exit', (code) => resolve(code))
      })

    it.skipIf(process.platform === 'win32')('passes the arguments through untouched', async () => {
      const { dir, node } = fakeNode('exit 0')
      expect(await run(node)).toBe(0)
      expect(readFileSync(join(dir, 'args'), 'utf8').trim()).toBe(
        '/entry.js runner start --parallel 2',
      )
    })

    it.skipIf(process.platform === 'win32')(
      'turns a revoked exit into success, so launchd does not restart it',
      async () => {
        expect(await run(fakeNode('exit 5').node)).toBe(0)
        expect(await run(fakeNode('exit 3').node)).toBe(3)
      },
    )

    it.skipIf(process.platform === 'win32')(
      'forwards SIGTERM so runs in flight can finish',
      async () => {
        const { dir, node } = fakeNode(
          `trap 'sleep 0.3; echo drained > "$(dirname "$0")/term"; exit 0' TERM\nwhile :; do sleep 0.1; done`,
        )
        expect(await run(node, (pid) => process.kill(pid, 'SIGTERM'))).toBe(0)
        expect(readFileSync(join(dir, 'term'), 'utf8').trim()).toBe('drained')
      },
    )
  })

  describe('Task Scheduler installer', () => {
    it('quotes paths as PowerShell literals', () => {
      const script = scheduledTaskInstaller({
        ...options,
        node: "C:\\Program Files\\nodejs\\node.exe",
        entry: "C:\\Users\\O'Brien\\AppData\\Roaming\\npm\\node_modules\\@aictiq\\cli\\dist\\index.js",
        path: 'C:\\Windows;C:\\Program Files\\Git\\cmd',
      })

      expect(script).toContain(
        "& 'C:\\Program Files\\nodejs\\node.exe' 'C:\\Users\\O''Brien\\AppData\\Roaming\\npm\\node_modules\\@aictiq\\cli\\dist\\index.js' runner start --parallel '2'",
      )
      expect(script).toContain("$env:PATH = 'C:\\Windows;C:\\Program Files\\Git\\cmd'")
    })

    it('embeds the loop script in one literal here-string and stops on a revoked secret', () => {
      const script = scheduledTaskInstaller(options)
      const start = script.indexOf("@'\n")
      const end = script.indexOf("\n'@")

      expect(start).toBeGreaterThan(0)
      expect(end).toBeGreaterThan(start)
      expect(script.indexOf("\n'@", end + 1)).toBe(-1)
      expect(script.slice(start, end)).toContain('if ($LASTEXITCODE -eq 5)')
      expect(script).toContain("Register-ScheduledTask -TaskName 'Aictiq runner'")
    })
  })
})
