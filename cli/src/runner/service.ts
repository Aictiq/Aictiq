import { join } from 'node:path'

/**
 * What `aictiq runner install-service` prints: a definition that keeps `aictiq runner start`
 * running as the current user, for the service manager of each supported OS. Nothing here
 * writes or enables anything; the operator reads the output and installs it.
 *
 * Every variant runs as the person who generated it, with their harness sign-ins and git
 * credentials: one runner is one trust domain. Every variant also stops for good on exit 5
 * (the runner secret was revoked), because restarting cannot fix that.
 */
export type ServicePlatform = 'linux' | 'macos' | 'windows'

export interface ServiceOptions {
  /** Absolute path of the Node.js binary. */
  node: string
  /** Absolute path of the CLI entry script. */
  entry: string
  parallel: string
  /** The PATH the service runs with, so it finds git and every harness. */
  path: string
  /** The home directory; macOS logs go below it. */
  home: string
}

export const RevokedExitCode = 5

export function servicePlatform(platform: NodeJS.Platform = process.platform): ServicePlatform {
  if (platform === 'darwin') return 'macos'
  if (platform === 'win32') return 'windows'
  return 'linux'
}

export function serviceDefinition(platform: ServicePlatform, options: ServiceOptions): string {
  switch (platform) {
    case 'macos':
      return launchdAgent(options)
    case 'windows':
      return scheduledTaskInstaller(options)
    default:
      return systemdUnit(options)
  }
}

export function systemdUnit({ node, entry, parallel, path }: ServiceOptions): string {
  return `# Save as ~/.config/systemd/user/aictiq-runner.service, then:
#   systemctl --user daemon-reload
#   systemctl --user enable --now aictiq-runner
#   loginctl enable-linger "$USER"   # keep it running after you log out
#
# The runner executes agents as this user, with this user's harness sign-ins and git
# credentials: one runner is one trust domain. SIGTERM lets runs in flight finish;
# TimeoutStopSec bounds how long systemd waits before it cancels them.
[Unit]
Description=Aictiq runner
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${node} ${entry} runner start --parallel ${parallel}
Restart=on-failure
RestartSec=10
# Exit ${RevokedExitCode} means the secret was revoked; restarting cannot fix that.
RestartPreventExitStatus=${RevokedExitCode}
KillMode=mixed
TimeoutStopSec=15min
Environment=PATH=${path}

[Install]
WantedBy=default.target
`
}

export const LaunchdLabel = 'com.aictiq.runner'

/**
 * launchd restarts on any non-zero exit (`SuccessfulExit` false) and cannot exempt one code,
 * so a small sh wrapper turns the revoked exit into 0. It also forwards launchd's SIGTERM to
 * the runner - otherwise sh would die on it and launchd would SIGKILL the runs in flight
 * instead of letting them finish. Paths reach the wrapper as arguments, never inside the
 * script text, so no quoting can break.
 */
const LaunchdWrapper = `"$1" "$2" runner start --parallel "$3" &
child=$!
trap 'kill -TERM "$child" 2>/dev/null' TERM INT
wait "$child"
status=$?
while kill -0 "$child" 2>/dev/null; do wait "$child"; status=$?; done
if [ "$status" -eq ${RevokedExitCode} ]; then exit 0; fi
exit "$status"`

export function launchdAgent({ node, entry, parallel, path, home }: ServiceOptions): string {
  const log = join(home, 'Library', 'Logs', 'aictiq-runner.log')
  const plistPath = `~/Library/LaunchAgents/${LaunchdLabel}.plist`
  const string = (value: string) => `<string>${xml(value)}</string>`
  return `<?xml version="1.0" encoding="UTF-8"?>
<!--
  Save as ${plistPath}, then:
    launchctl bootstrap gui/$(id -u) ${plistPath}
  Check it:   launchctl print gui/$(id -u)/${LaunchdLabel}
  Log:        tail -f ${xmlComment(log)}
  Remove it:  launchctl bootout gui/$(id -u)/${LaunchdLabel}

  The runner executes agents as this user, with this user's harness sign-ins and git
  credentials: one runner is one trust domain. It runs while this user is logged in.
  A stop lets runs in flight finish for up to 15 minutes before launchd kills them.
-->
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  ${string(LaunchdLabel)}
  <key>ProgramArguments</key>
  <array>
    ${string('/bin/sh')}
    ${string('-c')}
    ${string(LaunchdWrapper)}
    ${string('aictiq-runner')}
    ${string(node)}
    ${string(entry)}
    ${string(parallel)}
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>PATH</key>
    ${string(path)}
  </dict>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key>
    <false/>
  </dict>
  <key>ThrottleInterval</key>
  <integer>10</integer>
  <key>ExitTimeOut</key>
  <integer>900</integer>
  <key>ProcessType</key>
  ${string('Background')}
  <key>StandardOutPath</key>
  ${string(log)}
  <key>StandardErrorPath</key>
  ${string(log)}
</dict>
</plist>
`
}

export const ScheduledTaskName = 'Aictiq runner'

/**
 * Windows has no service manager for a script that should run as the signed-in person, so
 * this is a PowerShell installer for a Task Scheduler task that starts at logon. Task
 * Scheduler's own "restart on failure" only covers a task that fails to *start*, not one
 * that exits non-zero, so the task runs a loop script that restarts the runner itself and
 * stops on the revoked exit. The loop also writes the log, since a task has no console.
 */
export function scheduledTaskInstaller({ node, entry, parallel, path }: ServiceOptions): string {
  const loop = `# Written by \`aictiq runner install-service\`; the "${ScheduledTaskName}" task runs it at logon.
$ErrorActionPreference = 'Continue'
$env:PATH = ${ps(path)}
$log = Join-Path $env:LOCALAPPDATA 'aictiq\\runner.log'
while ($true) {
  # "$_" turns stderr's error records back into the plain lines the runner wrote.
  & ${ps(node)} ${ps(entry)} runner start --parallel ${ps(parallel)} 2>&1 |
    ForEach-Object { "$_" } | Add-Content -Path $log -Encoding utf8
  if ($LASTEXITCODE -eq ${RevokedExitCode}) {
    Add-Content -Path $log -Encoding utf8 -Value 'The runner secret was revoked; not restarting.'
    break
  }
  Start-Sleep -Seconds 10
}
`
  return `# Aictiq runner as a Windows scheduled task. Save as install-runner.ps1, then run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-runner.ps1
# Check it:   Get-ScheduledTask -TaskName '${ScheduledTaskName}' | Get-ScheduledTaskInfo
# Log:        Get-Content -Wait "$env:LOCALAPPDATA\\aictiq\\runner.log"
# Remove it:  Unregister-ScheduledTask -TaskName '${ScheduledTaskName}' -Confirm:$false
#
# The task starts at logon and runs while this user is logged in, as this user, with this
# user's harness sign-ins and git credentials: one runner is one trust domain. Stopping the
# task ends the runner at once; runs in flight are cancelled, not finished.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'aictiq'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$script = Join-Path $dir 'runner-service.ps1'
Set-Content -Path $script -Encoding utf8 -Value @'
${loop}'@

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \`"$script\`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName '${ScheduledTaskName}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName '${ScheduledTaskName}'
Write-Host "Installed and started '${ScheduledTaskName}'. Log: $(Join-Path $dir 'runner.log')"
`
}

function xml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/** A comment cannot contain `--`; a path in one is for reading, so a space keeps it valid. */
function xmlComment(value: string): string {
  return value.replace(/--/g, '- -')
}

/** A PowerShell single-quoted literal: nothing inside expands, and `'` doubles. */
function ps(value: string): string {
  return `'${value.replace(/'/g, "''")}'`
}

