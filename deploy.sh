#!/usr/bin/env bash
# deploy.sh — ship the SiriusXM proxy to the home server and bring it up in Docker.
#
# Mirrors deploy-samo.sh: ships the working tree, builds ON the server, and
# verifies before declaring success. Building remotely is deliberate — the
# server is x86_64 and dev machines here are arm64, so a locally-built image
# would be the wrong architecture. Compiling there sidesteps cross-building
# entirely.
#
# It is safe to re-run: every run is a rebuild + restart. Credentials already on
# the server are reused if you don't have them locally.
#
#   SXM_HOST=10.0.0.5 ./deploy.sh
#   SXM_SSH_USER=ubuntu ./deploy.sh
#   SXM_PORT=7788 ./deploy.sh
#
# Requirements: ssh/scp locally; ssh access + interactive sudo on the server.

set -euo pipefail

# ---- knobs -------------------------------------------------------------------

HOST="${SXM_HOST:-192.168.1.10}"
USER_NAME="${SXM_SSH_USER:-jake}"
SRC="${SXM_SRC:-$HOME/Developer/sxm-proxy}"
REMOTE_TMP="${SXM_REMOTE_TMP:-/tmp/sxm-deploy}"
PROJECT_DIR="${SXM_PROJECT_DIR:-/opt/sxm-proxy}"

# Deliberately not 8080 — too commonly taken on a general-purpose box.
PORT="${SXM_PORT:-7717}"

# ---- pretty printing ---------------------------------------------------------

if [ -t 1 ]; then
  C_STEP='\033[1;33m'; C_DIM='\033[2m'; C_OK='\033[1;32m'; C_ERR='\033[1;31m'; C_OFF='\033[0m'
else
  C_STEP=''; C_DIM=''; C_OK=''; C_ERR=''; C_OFF=''
fi
say()  { printf "\n${C_STEP}==>${C_OFF} %s\n" "$*"; }
note() { printf "    ${C_DIM}%s${C_OFF}\n" "$*"; }
fail() { printf "\n${C_ERR}xx ${C_OFF}%s\n" "$*" >&2; exit 1; }

# ---- sanity ------------------------------------------------------------------

[ -d "$SRC" ] || fail "sxm-proxy source not found at $SRC (set SXM_SRC to override)"
[ -f "$SRC/Dockerfile" ] || fail "no Dockerfile at $SRC — is this the right source tree?"
[ -d "$SRC/sxm-player" ] || fail "upstream checkout missing at $SRC/sxm-player — run: git clone https://github.com/yob15662/sxm-player.git"
[ -f "$SRC/extras/SamoExtras.cs" ] || fail "extras/SamoExtras.cs missing — /channels and /ui would be absent"
command -v ssh >/dev/null || fail "ssh not found locally"

# Credentials are optional locally ONLY if the server already has them, which we
# check after connecting. Never bake them into the source bundle.
HAVE_LOCAL_CREDS=0
if [ -f "$SRC/.env" ] && [ -f "$SRC/sxm_password.txt" ]; then
  grep -q '^SXM_USERNAME=' "$SRC/.env" || fail "$SRC/.env has no SXM_USERNAME= line"
  [ -s "$SRC/sxm_password.txt" ] || fail "$SRC/sxm_password.txt is empty"
  HAVE_LOCAL_CREDS=1
fi

# ---- verify SSH --------------------------------------------------------------

say "Checking SSH to ${USER_NAME}@${HOST}"
if ! ssh -o ConnectTimeout=5 -o BatchMode=yes "${USER_NAME}@${HOST}" 'true' 2>/dev/null; then
  ssh -o ConnectTimeout=10 "${USER_NAME}@${HOST}" 'true' || fail "cannot SSH to ${USER_NAME}@${HOST}"
fi

# Building on the server means its architecture is what we get. Warn loudly on a
# surprise rather than failing 10 minutes into a build.
REMOTE_ARCH="$(ssh "${USER_NAME}@${HOST}" 'uname -m' 2>/dev/null || echo unknown)"
note "server architecture: ${REMOTE_ARCH}"
case "$REMOTE_ARCH" in
  x86_64|amd64) ;;
  unknown) note "could not determine architecture — continuing" ;;
  *) note "note: expected x86_64; building natively for ${REMOTE_ARCH} anyway" ;;
esac

# If we have no local credentials, the server must already have them.
if [ "$HAVE_LOCAL_CREDS" -eq 0 ]; then
  if ! ssh "${USER_NAME}@${HOST}" "sudo test -f ${PROJECT_DIR}/sxm_password.txt" 2>/dev/null; then
    fail "no credentials locally and none on the server.
    Create them here first:
      printf 'SXM_USERNAME=you@example.com\\n' > $SRC/.env
      printf 'your-password' > $SRC/sxm_password.txt && chmod 600 $SRC/sxm_password.txt"
  fi
  note "using credentials already present on the server"
fi

# ---- package the source ------------------------------------------------------

say "Packaging source from ${SRC}"
LOCAL_TAR="$(mktemp -t sxm-src.XXXXXX).tgz"
REMOTE_SCRIPT_LOCAL=""
trap 'rm -f "${LOCAL_TAR:-}" "${REMOTE_SCRIPT_LOCAL:-}" 2>/dev/null || true' EXIT
# Same macOS tar hygiene as deploy-samo.sh: COPYFILE_DISABLE and --no-mac-metadata
# stop AppleDouble "._*" files and xattr PAX headers that GNU tar warns about.
# Credentials are excluded here on purpose and shipped separately with 600.
COPYFILE_DISABLE=1 tar --no-mac-metadata --no-xattrs -czf "$LOCAL_TAR" -C "$SRC" \
  --exclude='._*' --exclude='.DS_Store' \
  --exclude='./sxm-player/.git' \
  --exclude='./.env' --exclude='./sxm_password.txt' \
  --exclude='./playlists' \
  Dockerfile docker-compose.yml extras sxm-player \
  $([ -f "$SRC/channels.json" ] && echo channels.json || true)
note "source bundle: $(du -h "$LOCAL_TAR" | awk '{print $1}')"

# ---- the remote deploy script ------------------------------------------------

REMOTE_SCRIPT_LOCAL="$(mktemp -t sxm-remote.XXXXXX).sh"
cat > "$REMOTE_SCRIPT_LOCAL" <<'REMOTE'
#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="__PROJECT_DIR__"
REMOTE_TMP="__REMOTE_TMP__"
PORT="__PORT__"
SRC_TAR="$REMOTE_TMP/sxm-src.tgz"

step() { printf "\n\033[1;33m  ->\033[0m %s\n" "$*"; }
warn() { printf "\033[1;31m  !!\033[0m %s\n" "$*" >&2; }

# --- 1. Docker present? ---------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  step "Installing Docker (get.docker.com)"
  curl -fsSL https://get.docker.com | sh
fi
if ! docker compose version >/dev/null 2>&1; then
  warn "the 'docker compose' plugin is missing; install docker-compose-plugin and re-run"
  exit 1
fi

# --- 2. Port already taken? ----------------------------------------------
# Checked before the build so a conflict costs seconds, not a full compile.
# Our own container holding the port is fine — that is just a redeploy.
if command -v ss >/dev/null 2>&1; then
  if ss -ltn "sport = :$PORT" 2>/dev/null | grep -q LISTEN; then
    if ! docker ps --filter name=sxm-proxy --format '{{.Ports}}' 2>/dev/null | grep -q ":$PORT->"; then
      warn "port $PORT is already in use by something else on this host."
      warn "re-run with a different port:  SXM_PORT=7788 ./deploy.sh"
      exit 1
    fi
  fi
fi

# --- 3. Unpack the freshly-shipped source --------------------------------
step "Unpacking source into $PROJECT_DIR"
mkdir -p "$PROJECT_DIR"
# Preserve credentials across redeploys — they are never in the tarball.
find "$PROJECT_DIR" -mindepth 1 -maxdepth 1 \
  ! -name '.env' ! -name 'sxm_password.txt' -exec rm -rf {} + 2>/dev/null || true
tar xzf "$SRC_TAR" -C "$PROJECT_DIR"
find "$PROJECT_DIR" \( -name '._*' -o -name '.DS_Store' \) -delete 2>/dev/null || true

# --- 4. Install credentials if this run shipped them ----------------------
if [ -f "$REMOTE_TMP/.env" ]; then
  step "Installing credentials (0600)"
  install -m 600 "$REMOTE_TMP/.env" "$PROJECT_DIR/.env"
  install -m 600 "$REMOTE_TMP/sxm_password.txt" "$PROJECT_DIR/sxm_password.txt"
fi
[ -f "$PROJECT_DIR/.env" ] || { warn "no $PROJECT_DIR/.env — cannot start"; exit 1; }
[ -f "$PROJECT_DIR/sxm_password.txt" ] || { warn "no password file — cannot start"; exit 1; }
chmod 600 "$PROJECT_DIR/.env" "$PROJECT_DIR/sxm_password.txt"

# Pin the port into .env so compose and later manual runs agree.
grep -q '^SXM_PORT=' "$PROJECT_DIR/.env" \
  && sed -i "s|^SXM_PORT=.*|SXM_PORT=$PORT|" "$PROJECT_DIR/.env" \
  || echo "SXM_PORT=$PORT" >> "$PROJECT_DIR/.env"

cd "$PROJECT_DIR"

# --- 5. Build and start ---------------------------------------------------
step "Building the image from source (this takes a few minutes the first time)"
docker compose -f docker-compose.yml -f docker-compose.build.yml build

step "Starting the proxy"
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d

# --- 6. Firewall check (READ-ONLY — never modifies your rules) ------------
# Same policy as deploy-samo.sh: your ruleset is yours. We only report.
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
  if ! ufw status 2>/dev/null | grep -qE "(^|[[:space:]])$PORT/tcp"; then
    DEF_IF="$(ip -o -4 route show default 2>/dev/null | awk '{print $5; exit}')"
    LAN_CIDR="$(ip -o -4 route show dev "$DEF_IF" scope link 2>/dev/null | awk '{print $1; exit}')"
    [ -n "$LAN_CIDR" ] || LAN_CIDR="<your-lan-cidr>"
    warn "ufw is active and $PORT/tcp is NOT allowed — samo cannot reach the proxy until YOU allow it:"
    echo "      sudo ufw allow from ${LAN_CIDR} to any port ${PORT} proto tcp   # sxm proxy"
  fi
fi

# --- 7. Verify ------------------------------------------------------------
# Waits for the container's own healthcheck, which hits /channels. That proves
# login succeeded AND the lineup loaded — a port check would prove neither.
step "Waiting for the proxy to become healthy"
CID="$(docker compose ps -q sxm-proxy)"
STATUS=starting
for _ in $(seq 1 40); do
  STATUS="$(docker inspect --format '{{.State.Health.Status}}' "$CID" 2>/dev/null || echo starting)"
  [ "$STATUS" = "healthy" ] && break
  [ "$STATUS" = "unhealthy" ] && break
  sleep 3
done

if [ "$STATUS" != "healthy" ]; then
  warn "proxy did not become healthy (status: $STATUS) — recent logs:"
  docker compose logs --tail 40 sxm-proxy || true
  warn "a login failure here usually means wrong credentials in $PROJECT_DIR/.env / sxm_password.txt"
  exit 1
fi

COUNT="$(curl -fsS "http://localhost:$PORT/channels" 2>/dev/null \
  | grep -o '"id"' | wc -l | tr -d ' ' || echo 0)"
step "Healthy — serving $COUNT channels on port $PORT"
REMOTE

sed -i.bak "s#__PROJECT_DIR__#${PROJECT_DIR}#g; s#__REMOTE_TMP__#${REMOTE_TMP}#g; s#__PORT__#${PORT}#g" "$REMOTE_SCRIPT_LOCAL"
rm -f "${REMOTE_SCRIPT_LOCAL}.bak"

# ---- ship + run --------------------------------------------------------------

say "Staging on ${HOST}:${REMOTE_TMP}"
ssh "${USER_NAME}@${HOST}" "rm -rf ${REMOTE_TMP} && mkdir -p ${REMOTE_TMP} && chmod 700 ${REMOTE_TMP}"

say "Copying source bundle and deploy script"
scp -q "$LOCAL_TAR" "${USER_NAME}@${HOST}:${REMOTE_TMP}/sxm-src.tgz"
scp -q "$REMOTE_SCRIPT_LOCAL" "${USER_NAME}@${HOST}:${REMOTE_TMP}/remote-deploy.sh"

if [ "$HAVE_LOCAL_CREDS" -eq 1 ]; then
  say "Copying credentials (separately, never in the source bundle)"
  scp -q "$SRC/.env" "${USER_NAME}@${HOST}:${REMOTE_TMP}/.env"
  scp -q "$SRC/sxm_password.txt" "${USER_NAME}@${HOST}:${REMOTE_TMP}/sxm_password.txt"
  ssh "${USER_NAME}@${HOST}" "chmod 600 ${REMOTE_TMP}/.env ${REMOTE_TMP}/sxm_password.txt"
fi

say "Building and starting on ${HOST} (sudo may prompt)"
note "the first build compiles the .NET project on the server — a few minutes."
ssh -t "${USER_NAME}@${HOST}" "sudo bash ${REMOTE_TMP}/remote-deploy.sh"

say "Cleaning up ${REMOTE_TMP}"
# Matters more than usual: this directory held the plaintext credentials.
ssh "${USER_NAME}@${HOST}" "rm -rf ${REMOTE_TMP}"

printf "\n${C_OK}done.${C_OFF}\n"
echo "  Channel picker:  http://${HOST}:${PORT}/ui"
echo "  Channel API:     http://${HOST}:${PORT}/channels"
echo "  Stream URL:      http://${HOST}:${PORT}/icecast/<channel-guid>"
echo "  Logs:            ssh ${USER_NAME}@${HOST} 'cd ${PROJECT_DIR} && sudo docker compose logs -f'"
echo "  Restart:         ssh ${USER_NAME}@${HOST} 'cd ${PROJECT_DIR} && sudo docker compose restart'"
note "add to samo as an internet-station source, and put it on a schedule rule"
note "(RoleShow) rather than rotation — a dead proxy then costs one slot, not the channel."
