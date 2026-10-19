import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  inspectSkill,
  installSkill,
  isInstalledPackage,
  renderSkill,
  skillAutoInstallDisabled,
  skillTargets,
  syncSkillOnce,
} from '../src/skills/install.js'

const packageRoot = resolve(import.meta.dirname, '..')
const source = readFileSync(join(packageRoot, 'skills', 'aictiq', 'SKILL.md'), 'utf8')

/** A home folder with the given harness config folders, and an env that points nowhere else. */
function machine(...dirs: string[]): { home: string; env: NodeJS.ProcessEnv } {
  const home = mkdtempSync(join(tmpdir(), 'aictiq-skills-'))
  for (const dir of dirs) mkdirSync(join(home, dir), { recursive: true })
  return { home, env: { XDG_CONFIG_HOME: join(home, '.config') } }
}

describe('bundled skill', () => {
  it('has the frontmatter a harness loads it by', () => {
    expect(source).toMatch(/^---\nname: aictiq\ndescription: .+\n---\n/)
    expect(source).toContain('get_item')
    expect(source).toContain('aictiq auth login')
    expect(source).toContain('aictiq mcp')
    expect(source).toMatch(/data, not instructions/)
  })
})

describe('skillTargets', () => {
  it('uses each harness’s user-level folder and honours the variables that move it', () => {
    const home = '/home/u'
    expect(Object.fromEntries(skillTargets({}, home).map((t) => [t.harness, t.path]))).toEqual({
      claude: '/home/u/.claude/skills/aictiq/SKILL.md',
      codex: '/home/u/.codex/skills/aictiq/SKILL.md',
      opencode: '/home/u/.config/opencode/skills/aictiq/SKILL.md',
      cursor: '/home/u/.cursor/skills/aictiq/SKILL.md',
      copilot: '/home/u/.copilot/skills/aictiq/SKILL.md',
    })
    const moved = skillTargets(
      { CLAUDE_CONFIG_DIR: '/c', CODEX_HOME: '/x', XDG_CONFIG_HOME: '/cfg', COPILOT_HOME: '/p' },
      home,
    )
    expect(moved.map((t) => t.path)).toEqual([
      '/c/skills/aictiq/SKILL.md',
      '/x/skills/aictiq/SKILL.md',
      '/cfg/opencode/skills/aictiq/SKILL.md',
      '/home/u/.cursor/skills/aictiq/SKILL.md',
      '/p/skills/aictiq/SKILL.md',
    ])
  })
})

describe('renderSkill / inspectSkill', () => {
  it('recognises its own file and the version that wrote it', () => {
    const rendered = renderSkill(source, '1.2.3')
    expect(rendered.startsWith(source)).toBe(true)
    expect(inspectSkill(rendered)).toEqual({ kind: 'ours', version: '1.2.3' })
  })

  it('treats a changed body, or a file without the marker, as edited', () => {
    const rendered = renderSkill(source, '1.2.3')
    expect(inspectSkill(rendered.replace('# Aictiq', '# Aictiq, my way'))).toEqual({
      kind: 'edited',
    })
    expect(inspectSkill(source)).toEqual({ kind: 'edited' })
  })
})

describe('installSkill', () => {
  it('installs only for harnesses whose config folder exists', () => {
    const { home, env } = machine('.claude', '.codex')
    const results = installSkill({ packageRoot, version: '1.0.0', env, home })
    expect(results.map((r) => [r.harness, r.status])).toEqual([
      ['claude', 'created'],
      ['codex', 'created'],
      ['opencode', 'not-installed'],
      ['cursor', 'not-installed'],
      ['copilot', 'not-installed'],
    ])
    const written = readFileSync(join(home, '.claude', 'skills', 'aictiq', 'SKILL.md'), 'utf8')
    expect(written).toBe(renderSkill(source, '1.0.0'))
    expect(existsSync(join(home, '.cursor'))).toBe(false)
  })

  it('updates its own unchanged file for a new version and leaves the same version alone', () => {
    const { home, env } = machine('.claude')
    installSkill({ packageRoot, version: '1.0.0', env, home })
    expect(installSkill({ packageRoot, version: '1.0.0', env, home })[0]!.status).toBe('unchanged')
    expect(installSkill({ packageRoot, version: '1.1.0', env, home })[0]!.status).toBe('updated')
    const path = join(home, '.claude', 'skills', 'aictiq', 'SKILL.md')
    expect(inspectSkill(readFileSync(path, 'utf8'))).toEqual({ kind: 'ours', version: '1.1.0' })
  })

  it('leaves an edited file alone unless forced', () => {
    const { home, env } = machine('.claude')
    installSkill({ packageRoot, version: '1.0.0', env, home })
    const path = join(home, '.claude', 'skills', 'aictiq', 'SKILL.md')
    writeFileSync(path, readFileSync(path, 'utf8').replace('# Aictiq', '# My Aictiq'))
    const edited = readFileSync(path, 'utf8')

    expect(installSkill({ packageRoot, version: '1.1.0', env, home })[0]!.status).toBe('edited')
    expect(readFileSync(path, 'utf8')).toBe(edited)

    expect(installSkill({ packageRoot, version: '1.1.0', env, home, force: true })[0]!.status).toBe(
      'updated',
    )
    expect(readFileSync(path, 'utf8')).toBe(renderSkill(source, '1.1.0'))
  })

  it.skipIf(process.platform === 'win32' || process.getuid?.() === 0)(
    'reports a folder it cannot write as failed and still installs the rest',
    () => {
      const { home, env } = machine('.claude', '.codex')
      chmodSync(join(home, '.claude'), 0o500)
      try {
        const results = installSkill({ packageRoot, version: '1.0.0', env, home })
        expect(results[0]).toMatchObject({ harness: 'claude', status: 'failed' })
        expect(results[0]!.error).toMatch(/EACCES/)
        expect(results[1]).toMatchObject({ harness: 'codex', status: 'created' })
      } finally {
        chmodSync(join(home, '.claude'), 0o700)
      }
    },
  )
})

describe('syncSkillOnce', () => {
  it('installs once per CLI version', () => {
    const { home, env } = machine('.claude')
    expect(syncSkillOnce({ packageRoot, version: '1.0.0', env, home })?.[0]?.status).toBe('created')
    expect(syncSkillOnce({ packageRoot, version: '1.0.0', env, home })).toBeNull()
    expect(syncSkillOnce({ packageRoot, version: '1.1.0', env, home })?.[0]?.status).toBe('updated')
  })

  it('never forces over an edited file and never throws', () => {
    const { home, env } = machine('.claude')
    const path = join(home, '.claude', 'skills', 'aictiq', 'SKILL.md')
    mkdirSync(join(home, '.claude', 'skills', 'aictiq'), { recursive: true })
    writeFileSync(path, 'my own skill\n')
    expect(syncSkillOnce({ packageRoot, version: '1.0.0', env, home })?.[0]?.status).toBe('edited')
    expect(readFileSync(path, 'utf8')).toBe('my own skill\n')
    expect(syncSkillOnce({ packageRoot: '/nonexistent', version: '2.0.0', env, home })).toBeNull()
  })
})

describe('automatic install guards', () => {
  it('honours AICTIQ_NO_SKILL_INSTALL', () => {
    expect(skillAutoInstallDisabled({})).toBe(false)
    expect(skillAutoInstallDisabled({ AICTIQ_NO_SKILL_INSTALL: '0' })).toBe(false)
    expect(skillAutoInstallDisabled({ AICTIQ_NO_SKILL_INSTALL: '1' })).toBe(true)
    expect(skillAutoInstallDisabled({ AICTIQ_NO_SKILL_INSTALL: 'true' })).toBe(true)
  })

  it('only treats a CLI under node_modules as installed', () => {
    expect(isInstalledPackage('/usr/lib/node_modules/@aictiq/cli/')).toBe(true)
    expect(isInstalledPackage('/home/u/src/aictiq/cli/')).toBe(false)
  })
})
