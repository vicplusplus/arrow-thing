#!/bin/bash
# Sets up /home/deploy/arrow-thing/ from the repo deploy configs.
# Run from the repo root: ./server/deploy/setup.sh
# Secrets (.env, origin certs) must be placed manually afterward.

set -euo pipefail

DEPLOY_DIR="/home/deploy/arrow-thing"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

# --- Pull latest ---

echo "Pulling latest from origin..."
git -C "$REPO_DIR" pull

# --- Copy deploy configs ---

mkdir -p "$DEPLOY_DIR/nginx/certs"
mkdir -p "$DEPLOY_DIR/loki"
mkdir -p "$DEPLOY_DIR/prometheus"
mkdir -p "$DEPLOY_DIR/grafana/provisioning/datasources"
mkdir -p "$DEPLOY_DIR/grafana/provisioning/dashboards"

cp "$SCRIPT_DIR/docker-compose.yml" "$DEPLOY_DIR/"
cp "$SCRIPT_DIR/init-db.sh" "$DEPLOY_DIR/"
chmod +x "$DEPLOY_DIR/init-db.sh"
cp "$SCRIPT_DIR/nginx/nginx.conf" "$DEPLOY_DIR/nginx/"
cp "$SCRIPT_DIR/loki/loki-config.yml" "$DEPLOY_DIR/loki/"
cp "$SCRIPT_DIR/prometheus/prometheus.yml" "$DEPLOY_DIR/prometheus/"
cp "$SCRIPT_DIR/grafana/provisioning/datasources/datasources.yml" \
  "$DEPLOY_DIR/grafana/provisioning/datasources/"
cp "$SCRIPT_DIR/grafana/provisioning/dashboards/"*.yml \
  "$DEPLOY_DIR/grafana/provisioning/dashboards/"
cp "$SCRIPT_DIR/grafana/provisioning/dashboards/"*.json \
  "$DEPLOY_DIR/grafana/provisioning/dashboards/"
echo "Downloading Cloudflare Authenticated Origin Pull CA certificate..."
curl -fsSL "https://developers.cloudflare.com/ssl/static/authenticated_origin_pull_ca.pem" \
  -o "$DEPLOY_DIR/nginx/certs/cloudflare-origin-pull.pem"
echo "Certificate saved to $DEPLOY_DIR/nginx/certs/cloudflare-origin-pull.pem"

echo "Deploy configs copied to $DEPLOY_DIR"

# --- Validate nginx config ---

echo ""
echo "Validating nginx config..."
# Upstream 'api' won't resolve outside Docker Compose network, so
# this test may fail with "host not found" — that's expected.
if docker run --rm \
  -v "$DEPLOY_DIR/nginx/nginx.conf:/etc/nginx/nginx.conf:ro" \
  -v "$DEPLOY_DIR/nginx/certs:/etc/nginx/certs:ro" \
  nginx:alpine nginx -t 2>&1; then
  echo "Nginx config OK."
else
  echo "Nginx validation failed (upstream DNS errors are expected outside Docker Compose)."
fi

# --- Set up backup cron ---

mkdir -p /home/deploy/backups

CRON_MARKER="# arrow-thing-managed"
if ! crontab -l 2>/dev/null | grep -q "$CRON_MARKER"; then
  echo ""
  echo "Installing cron jobs..."
  (crontab -l 2>/dev/null || true; cat <<EOF

$CRON_MARKER
# Daily DB backup at 04:00 UTC
0 4 * * * cd $DEPLOY_DIR && docker compose exec -T db pg_dump -U arrowthing arrowthing | gzip > /home/deploy/backups/arrowthing_\$(date +\%F).sql.gz
# Delete backups older than 14 days
10 4 * * * find /home/deploy/backups -name "*.sql.gz" -mtime +14 -delete
# Disk usage alert every 6 hours
0 */6 * * * df -h / | awk 'NR==2 && int(\$5) > 80 {print "DISK WARNING: " \$5 " used"}' >> /home/deploy/disk-alerts.log
EOF
  ) | crontab -
  echo "Cron jobs installed."
else
  echo ""
  echo "Cron jobs already installed (skipping)."
fi

# --- Check for secrets ---

echo ""
MISSING=0
[ ! -f "$DEPLOY_DIR/.env" ] && echo "MISSING: $DEPLOY_DIR/.env (see server/.env.sample)" && MISSING=1
[ ! -f "$DEPLOY_DIR/nginx/certs/origin.pem" ] && echo "MISSING: $DEPLOY_DIR/nginx/certs/origin.pem" && MISSING=1
[ ! -f "$DEPLOY_DIR/nginx/certs/origin-key.pem" ] && echo "MISSING: $DEPLOY_DIR/nginx/certs/origin-key.pem" && MISSING=1

# Check .env has required keys
if [ -f "$DEPLOY_DIR/.env" ]; then
  for KEY in POSTGRES_PASSWORD JWT_SECRET ADMIN_API_KEY GRAFANA_ADMIN_PASSWORD; do
    if ! grep -q "^${KEY}=.\+" "$DEPLOY_DIR/.env" 2>/dev/null; then
      echo "MISSING: $KEY is not set in $DEPLOY_DIR/.env"
      MISSING=1
    fi
  done
fi

if [ "$MISSING" -eq 0 ]; then
  echo "All files in place."
else
  echo ""
  echo "Place the missing files above before running docker compose."
fi

echo ""
echo "Done."
