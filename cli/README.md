# @aictiq/cli

The command-line client for [Aictiq](https://github.com/blagoculjak/aictiq) — project
management for software teams and their AI agents. It talks to the same `/api/v1` surface
the web app uses, prints tables for people and `--json` for scripts, and carries a stdio
MCP server so a coding agent can reach the instance without a bearer token in its
configuration file.

```bash
npm install -g @aictiq/cli
aictiq auth login --url https://aictiq.example.com
aictiq item list -p ACME --filter "state:active assignee:@me"
aictiq item claim ACME-123 && aictiq item comment ACME-123 -m "starting"
aictiq run start ACME-124 --playbook implement && aictiq run logs <runId> --follow
```

Full documentation, including exit codes, the configuration precedence and the MCP bridge:
[`docs/cli.md`](../docs/cli.md).

Licensed AGPL-3.0-only.
