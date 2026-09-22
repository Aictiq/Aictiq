import { Command } from 'commander'
import { attachmentCommand } from './commands/attachment.js'
import { authCommand } from './commands/auth.js'
import { itemCommand } from './commands/item.js'
import { mcpCommand } from './commands/mcp.js'
import { projectCommand } from './commands/project.js'
import { runCommand } from './commands/run.js'
import { runnerCommand } from './commands/runner.js'
import { sprintCommand } from './commands/sprint.js'
import { wikiCommand } from './commands/wiki.js'
import type { GlobalOptions } from './context.js'
import { version } from './version.js'

export function createProgram(): Command {
  const program = new Command('aictiq')
    .description('Aictiq from the command line, for people and for agents')
    .version(version, '-v, --version')
    .option('--url <url>', 'Aictiq base URL (overrides AICTIQ_URL and the config file)')
    .option('--token <token>', 'Personal access token (overrides AICTIQ_TOKEN)')
    .option('--org <slug>', 'Organization slug (overrides AICTIQ_ORG)')
    .option('--json', 'Print machine-readable JSON instead of a table')
    // Subcommands read the globals lazily: commander parses the root options before it
    // hands control to the subcommand's action, so this closure always sees them.
    .showHelpAfterError()

  const globals = () => program.opts<GlobalOptions>()

  program.addCommand(authCommand(globals))
  program.addCommand(attachmentCommand(globals))
  program.addCommand(projectCommand(globals))
  program.addCommand(itemCommand(globals))
  program.addCommand(sprintCommand(globals))
  program.addCommand(wikiCommand(globals))
  program.addCommand(runCommand(globals))
  program.addCommand(mcpCommand(globals))
  program.addCommand(runnerCommand(globals))

  return program
}
