# A self-contained source build for `docker compose up --build`. The runtime images are
# deliberately separate targets so Compose starts the same API/worker topology used by
# the published-image deployment.

FROM node:24-bookworm-slim AS web-build
WORKDIR /src/frontend-vue

# pnpm-workspace.yaml carries Aictiq's explicit allow-list for dependency install
# scripts; it is part of the reproducible dependency input, not an optional workspace
# convenience.
COPY frontend-vue/package.json frontend-vue/pnpm-lock.yaml frontend-vue/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY frontend-vue/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY backend/ ./backend/

# The API project normally invokes pnpm itself. The web build above is intentional here:
# it keeps Node and the web dependency cache out of the .NET build stage, while this copy
# preserves the exact wwwroot layout that the API serves in production.
RUN dotnet publish backend/src/Api/Aictiq.Api.csproj \
      --configuration Release \
      --output /out/api \
      -p:SkipWebBuild=true

# Copy the prebuilt SPA after publish. It is kept as a separate instruction so a web-only
# edit does not force NuGet restore or compilation of the backend.
COPY --from=web-build /src/frontend-vue/dist/ /out/api/wwwroot/

RUN dotnet publish backend/src/Workers/Aictiq.Workers.csproj \
      --configuration Release \
      --output /out/workers \
      -p:SkipWebBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api/ ./
ENTRYPOINT ["dotnet", "Aictiq.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS workers
WORKDIR /app
COPY --from=build /out/workers/ ./
ENTRYPOINT ["dotnet", "Aictiq.Workers.dll"]
