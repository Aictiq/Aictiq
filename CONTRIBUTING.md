# Contributing to Aictiq

Thanks for helping make Aictiq useful for software teams and their agents. By participating,
you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- Search existing issues and pull requests before opening a new proposal.
- Use a focused branch and one concern per pull request.
- Do not include credentials, production data, access tokens, or `.env` files.
- For a security issue, follow [`SECURITY.md`](SECURITY.md) instead of opening an issue.

## Local development

Prerequisites are .NET 10, Node 22+, pnpm, Docker, and Docker Compose. Start the development
stack with Aspire:

```sh
dotnet run --project backend/src/AppHost
```

The command starts Postgres, Garage, the API, workers, and the Vite app. The Compose bundle
in `deploy/` is the supported path for a production-like local run.

Run the checks relevant to your change before requesting review:

```sh
dotnet build Aictiq.slnx -warnaserror
dotnet test backend/tests/IntegrationTests/Aictiq.IntegrationTests.csproj
(cd frontend-vue && pnpm lint && pnpm typecheck && pnpm test && pnpm build)
(cd cli && pnpm lint && pnpm typecheck && pnpm test && pnpm build)
(cd docs && pnpm build)
```

## Engineering expectations

Read [`docs/invariants.md`](docs/invariants.md) before touching application code. The
important rules are architectural, not stylistic: tenant-scoped data fails closed; database
constraints protect invariants; cross-module writes go through the outbox; state changes use
compare-and-swap; and a forbidden resource normally returns 404 rather than confirming it
exists.

Add an integration test for a changed authorization, persistence, or external-boundary
behavior. Keep public API changes additive in v1 and update the OpenAPI document, CLI, and
docs together. Documentation is part of the product: a self-hoster and an agent author
should be able to understand the change without reading its implementation.

## Pull requests

Explain the problem, the change, and verification in the pull request template. Keep commits
readable and scoped to one concern. Maintainers
may ask for tests, docs, a migration review, or a smaller follow-up before merging.

`main` is protected: every change lands through a pull request, and nothing merges while a
required check is red or a review conversation is unresolved. CodeQL runs on every pull
request and must pass. The branch also has to be current with `main` before merging, so
expect to click "Update branch" if `main` moved while your review was open. Merges are
squashed, so the pull request title becomes the commit subject on `main`: write it as the
commit message you want in the history.

Contributions are licensed under the repository's [AGPL-3.0-only license](LICENSE).
