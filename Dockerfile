# EventForge — HTTP job queue + WebSocket events (single ECS task; in-memory queue).
FROM node:22-bookworm-slim AS web-build
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --silent
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
COPY --from=web-build /src/wwwroot ./wwwroot
RUN dotnet publish EventForge.csproj -c Release -r linux-x64 --self-contained -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates libicu72 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish/ ./
COPY agent/ ./
COPY appsettings.Production.json ./appsettings.Production.json
COPY appsettings.json ./appsettings.json

ENV ASPNETCORE_ENVIRONMENT=Production
ENV EventForge__ListenUrl=http://0.0.0.0:8090

EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -sf http://127.0.0.1:8090/health || exit 1

ENTRYPOINT ["./EventForge"]
