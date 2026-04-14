#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

# Create .env from sample if it doesn't exist
if [ ! -f .env ]; then
    cp .env.sample .env
    sed -i 's/^POSTGRES_PASSWORD=$/POSTGRES_PASSWORD=localdev/' .env
    sed -i 's/^JWT_SECRET=$/JWT_SECRET=local-dev-jwt-secret-not-for-production/' .env
    sed -i 's/^ADMIN_API_KEY=$/ADMIN_API_KEY=local-dev-admin-key/' .env
    sed -i 's/^ASPNETCORE_ENVIRONMENT=Production$/ASPNETCORE_ENVIRONMENT=Development/' .env
    sed -i 's/^GRAFANA_ADMIN_PASSWORD=$/GRAFANA_ADMIN_PASSWORD=admin/' .env
    echo "Created .env with dev defaults. Edit if needed."
fi

# Start PostgreSQL and Redis (worker runs locally below)
docker compose -f docker-compose.dev.yml up -d db redis
echo "Waiting for PostgreSQL..."
until docker exec arrowthing-dev pg_isready -U arrowthing -q 2>/dev/null; do
    sleep 1
done
echo "PostgreSQL ready."

echo "Waiting for Redis..."
until docker exec arrowthing-redis-dev redis-cli ping 2>/dev/null | grep -q PONG; do
    sleep 1
done
echo "Redis ready."

# Load .env for local process configuration
set -a
source .env
set +a

LOCAL_CONN="Host=localhost;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"

cleanup() {
    echo "Shutting down..."
    kill "$WORKER_PID" 2>/dev/null
    wait "$WORKER_PID" 2>/dev/null
}
trap cleanup EXIT

# Build solution once so both processes can use --no-build (avoids file-lock
# collisions on the shared Server output directory now that Worker references
# Server).
echo "Building solution..."
dotnet build ArrowThing.sln

# Run the worker in the background
ConnectionStrings__Default="$LOCAL_CONN" \
Redis__ConnectionString="localhost:6379" \
dotnet run --no-build --project ArrowThing.Worker &
WORKER_PID=$!
echo "Worker started (PID $WORKER_PID)."

# Run the API in the foreground (applies migrations on startup)
dotnet run --no-build --project ArrowThing.Server
