FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy Unity domain sources (compiled by ArrowThing.Domain via relative paths)
COPY Assets/Scripts/Domain/ Assets/Scripts/Domain/

# Restore dependencies layer (cached unless .csproj files change)
COPY server/ArrowThing.sln server/
COPY server/ArrowThing.Domain/ArrowThing.Domain.csproj server/ArrowThing.Domain/
COPY server/ArrowThing.Server/ArrowThing.Server.csproj server/ArrowThing.Server/
COPY server/ArrowThing.Worker/ArrowThing.Worker.csproj server/ArrowThing.Worker/
RUN dotnet restore server/ArrowThing.Server/ArrowThing.Server.csproj

COPY server/ArrowThing.Domain/ server/ArrowThing.Domain/
COPY server/ArrowThing.Server/ server/ArrowThing.Server/
RUN dotnet publish server/ArrowThing.Server/ArrowThing.Server.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

# aspnet:10.0 ships with a non-root `app` user (UID 1654). Switching off
# root reduces the blast radius if a request-handling bug ever lets an
# attacker execute shell commands inside the container.
USER app

# Self-contained liveness check so Docker can restart the container when the
# app deadlocks even if nginx keeps passing traffic. The HTTP timeout on
# /health is ~3s in the endpoint itself; 5s here has headroom.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "ArrowThing.Server.dll"]
