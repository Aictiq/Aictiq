import {
  describeSkillResult,
  isInstalledPackage,
  skillAutoInstallDisabled,
  syncSkillOnce,
} from './skills/install.js'
import { packageRoot, version } from './version.js'

/**
 * `postinstall`: installs the Aictiq skill when the CLI is installed globally (and its install
 * scripts are allowed to run). A project that depends on the CLI installs nothing into the
 * user's harness folders. Every failure is a warning; `npm install` never fails because of it.
 */
export function postinstall(env: NodeJS.ProcessEnv = process.env): void {
  try {
    if (env.npm_config_global !== 'true' || skillAutoInstallDisabled(env)) return
    if (!isInstalledPackage(packageRoot)) return
    for (const result of syncSkillOnce({ packageRoot, version, env }) ?? []) {
      const line = describeSkillResult(result)
      if (line) process.stderr.write(`aictiq: ${line}\n`)
    }
  } catch (error) {
    process.stderr.write(
      `aictiq: Warning: could not install the Aictiq skill (${error instanceof Error ? error.message : String(error)}); run \`aictiq skills install\`\n`,
    )
  }
}

postinstall()
