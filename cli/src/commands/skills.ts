import { Command } from 'commander'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'
import { print, printJson, renderTable } from '../output.js'
import { bundledSkillPath, installSkill } from '../skills/install.js'
import { packageRoot, version } from '../version.js'

/**
 * `aictiq skills` - the Aictiq skill for coding harnesses (Claude Code, Codex, Cursor, Copilot,
 * OpenCode), which tells an agent how to reach Aictiq. The CLI installs it on its own after an
 * install or upgrade; this command does it by hand, for installs that skipped scripts.
 */
export function skillsCommand(globals: () => GlobalOptions): Command {
  const skills = new Command('skills').description('The Aictiq skill for coding harnesses')

  skills
    .command('install')
    .description(
      'Install or update the Aictiq skill in the user-level skills folder of each harness on this machine',
    )
    .option('--force', 'Replace a skill file that was edited by hand')
    .action((options: { force?: boolean }) => {
      let results
      try {
        results = installSkill({ packageRoot, version, force: options.force === true })
      } catch (error) {
        throw new CliError(
          `Cannot read the bundled skill (${bundledSkillPath(packageRoot)}): ${error instanceof Error ? error.message : String(error)}`,
          ExitCode.Error,
        )
      }
      if (globals().json) return printJson(results)
      const found = results.filter((r) => r.status !== 'not-installed')
      if (found.length === 0) {
        print(
          'No supported harness found (Claude Code, Codex, Cursor, Copilot or OpenCode); nothing installed.',
        )
        return
      }
      print(
        renderTable(found, [
          { header: 'HARNESS', value: (r) => r.harness },
          { header: 'STATUS', value: (r) => r.status },
          { header: 'PATH', value: (r) => r.path },
        ]),
      )
      for (const result of found) {
        if (result.status === 'edited') {
          process.stderr.write(
            `Warning: ${result.path} was edited by hand and was left alone; pass --force to replace it.\n`,
          )
        }
        if (result.status === 'failed') {
          process.stderr.write(`Warning: could not write ${result.path}: ${result.error}\n`)
        }
      }
      if (found.some((r) => r.status === 'failed')) process.exitCode = ExitCode.Error
    })

  return skills
}
