# Releasing Aictiq

Aictiq releases are created by pushing a semantic-version tag from the already-tested
`main` commit:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The tag must match `cli/package.json` exactly after removing its leading `v`. The release
workflow validates that relationship before publishing anything, then runs the backend,
web and CLI checks. A successful run publishes multi-architecture (`linux/amd64` and
`linux/arm64`) API and Workers images to GHCR, publishes `@aictiq/cli`, creates a pinned
compose zip, and creates the GitHub Release.

The generated release body groups conventional commits and ticket keys. Conventional
commits marked with `!` or `BREAKING CHANGE:` are repeated under breaking configuration
changes. Add that marker whenever a deployer must change configuration.

## One-time external configuration

The workflow uses GitHub's short-lived `GITHUB_TOKEN` to write GHCR packages and the
GitHub Release. If organization policy restricts it, allow workflow token permissions for
`contents: write` and `packages: write`, and make the `ghcr.io/<owner>/api` and
`ghcr.io/<owner>/workers` packages visible to the intended self-hosters.

Configure npm [trusted publishing](https://docs.npmjs.com/trusted-publishers) for
`@aictiq/cli` with this repository and the `release.yml` workflow. The workflow uses OIDC
(`id-token: write`) and deliberately has no `NPM_TOKEN`; a failed npm publish normally
means trusted publishing has not been configured yet.

## Upgrade safety

Download the compose bundle attached to the GitHub Release and start from its
release-pinned `.env.example`. API and Workers tags are a matched pair. Take a backup
before upgrading. Database migrations only move forward: never start an older API image
against a database already migrated by a newer one; restore the pre-upgrade backup if a
rollback is required.

The optional in-app update check is documented in [the compose guide](../deploy/README.md#version-and-update-checks).
