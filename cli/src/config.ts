import { chmodSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'

/**
 * What `aictiq auth login` writes. The token is a personal access token — the same
 * credential the REST API and `/mcp` take — so the file is written 0600 and the
 * directory 0700. There is deliberately no OS keychain integration: keytar and its
 * successors ship prebuilt native binaries, and a CLI whose job is to hold a bearer
 * token should not widen its install surface that far. See docs/cli.md.
 */
export interface StoredConfig {
  url?: string
  token?: string
  org?: string
}

export function configPath(env: NodeJS.ProcessEnv = process.env): string {
  const base = env.AICTIQ_CONFIG_HOME ?? env.XDG_CONFIG_HOME ?? join(homedir(), '.config')
  return join(base, 'aictiq', 'config.json')
}

export function readConfig(path = configPath()): StoredConfig {
  if (!existsSync(path)) return {}
  try {
    const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
    if (typeof parsed !== 'object' || parsed === null) return {}
    const { url, token, org } = parsed as Record<string, unknown>
    return {
      ...(typeof url === 'string' ? { url } : {}),
      ...(typeof token === 'string' ? { token } : {}),
      ...(typeof org === 'string' ? { org } : {}),
    }
  } catch {
    // A hand-edited or truncated file must not make every command fail; the caller
    // falls back to flags and environment, which is what an unconfigured host does.
    return {}
  }
}

export function writeConfig(config: StoredConfig, path = configPath()): void {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 })
  // The mode argument only applies when the file is created, so an existing file that
  // someone widened is narrowed again explicitly.
  writeFileSync(path, `${JSON.stringify(config, null, 2)}\n`, { mode: 0o600 })
  chmodSync(path, 0o600)
}

export function clearConfig(path = configPath()): void {
  rmSync(path, { force: true })
}
