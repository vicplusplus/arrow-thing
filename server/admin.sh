#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOCAL_URL="http://localhost:5043"
PROD_URL="https://api.arrow-thing.com"

# ── Load .env ───────────────────────────────────────────────────────
if [[ -f "$SCRIPT_DIR/.env" ]]; then
    set -a
    source "$SCRIPT_DIR/.env"
    set +a
fi

# ── Parse --prod flag ───────────────────────────────────────────────
PROD=false
if [[ "${1:-}" == "--prod" ]]; then
    PROD=true
    shift
fi

if $PROD; then
    BASE_URL="$PROD_URL"
    KEY="${PROD_ADMIN_API_KEY:-}"
    if [[ -z "$KEY" ]]; then
        echo "Error: PROD_ADMIN_API_KEY not set in .env" >&2
        exit 1
    fi
else
    BASE_URL="$LOCAL_URL"
    KEY="${ADMIN_API_KEY:-}"
    if [[ -z "$KEY" ]]; then
        echo "Error: ADMIN_API_KEY not set in .env" >&2
        exit 1
    fi
fi

# ── Helpers ─────────────────────────────────────────────────────────
pretty() {
    if command -v jq &>/dev/null; then
        jq .
    else
        cat
    fi
}

request() {
    local method="$1" path="$2"
    shift 2
    local url="$BASE_URL$path"
    echo "→ $method $url" >&2
    curl -s -X "$method" "$url" \
        -H "X-Admin-Key: $KEY" \
        -H "Content-Type: application/json" \
        "$@" | pretty
}

usage() {
    cat <<EOF
Usage: $(basename "$0") [--prod] <command> [args]

Commands:
  flagged-users                 List all flagged accounts
  unflag <user-uuid>            Clear flag on an account
  remove-score <score-uuid>     Hard-delete a score
  lock-account <email>          Lock an account
  unlock-account <email>        Unlock an account (sends password reset)

Options:
  --prod    Target production (api.arrow-thing.com).
            Reads PROD_ADMIN_API_KEY from .env.
            Without this flag, targets localhost and uses ADMIN_API_KEY.
EOF
    exit 1
}

# ── Commands ────────────────────────────────────────────────────────
CMD="${1:-}"
shift || true

case "$CMD" in
    flagged-users)
        request GET "/api/admin/flagged-users"
        ;;
    unflag)
        [[ -z "${1:-}" ]] && { echo "Usage: unflag <user-uuid>" >&2; exit 1; }
        request POST "/api/admin/users/$1/unflag"
        ;;
    remove-score)
        [[ -z "${1:-}" ]] && { echo "Usage: remove-score <score-uuid>" >&2; exit 1; }
        request POST "/api/admin/scores/$1/remove"
        ;;
    lock-account)
        [[ -z "${1:-}" ]] && { echo "Usage: lock-account <email>" >&2; exit 1; }
        request POST "/api/admin/lock-account" -d "{\"email\":\"$1\"}"
        ;;
    unlock-account)
        [[ -z "${1:-}" ]] && { echo "Usage: unlock-account <email>" >&2; exit 1; }
        request POST "/api/admin/unlock-account" -d "{\"email\":\"$1\"}"
        ;;
    *)
        usage
        ;;
esac
