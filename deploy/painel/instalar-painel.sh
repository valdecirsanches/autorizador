#!/usr/bin/env bash
#
# instalar-painel.sh — compila o painel Next.js e registra como servico systemd.
# Idempotente: rode de novo a cada deploy (depois de um `git pull`).
#
# Uso (na raiz do projeto clonado):
#   sudo bash deploy/painel/instalar-painel.sh
#
# Opcionais: SERVICE=aprovador-painel PORT=3000 RUNUSER=ubuntu ENVFILE=/etc/aprovador-painel.env
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APPDIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
SERVICE="${SERVICE:-aprovador-painel}"
PORT="${PORT:-3000}"
ENVFILE="${ENVFILE:-/etc/aprovador-painel.env}"
RUNUSER="${RUNUSER:-${SUDO_USER:-ubuntu}}"

if [ "$(id -u)" -ne 0 ]; then
  echo "!! Rode com sudo:  sudo bash deploy/painel/instalar-painel.sh"
  exit 1
fi

NODE="$(command -v node || true)"
for c in /usr/bin/node /usr/local/bin/node; do
  [ -z "$NODE" ] && [ -x "$c" ] && NODE="$c"
done
if [ -z "$NODE" ]; then
  echo "!! Node.js nao encontrado. Instale o Node 20 ou superior."
  exit 1
fi
MAIOR="$("$NODE" -p 'process.versions.node.split(".")[0]')"
if [ "$MAIOR" -lt 20 ]; then
  echo "!! Node $("$NODE" -v) e antigo. O Next.js 16 exige Node 20 ou superior."
  exit 1
fi
echo "==> node: $NODE ($("$NODE" -v)) | pasta: $APPDIR | usuario: $RUNUSER | porta: $PORT"

# Variaveis: cria na primeira vez a partir do modelo.
if [ ! -f "$ENVFILE" ]; then
  cp "$SCRIPT_DIR/aprovador-painel.env.example" "$ENVFILE"
  chmod 600 "$ENVFILE"
  echo "==> Criei $ENVFILE. PREENCHA os valores TROQUE_AQUI e rode este script de novo."
  exit 0
fi
if grep -q "TROQUE_AQUI" "$ENVFILE"; then
  echo "!! $ENVFILE ainda tem valores TROQUE_AQUI. Preencha e rode de novo."
  exit 1
fi

DATADIR="$(grep -E '^PAINEL_DATA_DIR=' "$ENVFILE" | cut -d= -f2- || true)"
if [ -n "$DATADIR" ]; then
  mkdir -p "$DATADIR"
  chown "$RUNUSER":"$RUNUSER" "$DATADIR"
fi

# Instala dependencias e compila como o usuario do servico (nao como root).
echo "==> Instalando dependencias e compilando (pode levar alguns minutos)..."
chown -R "$RUNUSER":"$RUNUSER" "$APPDIR"
sudo -u "$RUNUSER" -H bash -c "cd '$APPDIR' && npx --yes pnpm@9 install --frozen-lockfile && npx --yes pnpm@9 build"

sed -e "s#__NODE__#$NODE#g" -e "s#__APPDIR__#$APPDIR#g" -e "s#__USER__#$RUNUSER#g" \
    -e "s#__ENVFILE__#$ENVFILE#g" -e "s#__PORT__#$PORT#g" \
    "$SCRIPT_DIR/aprovador-painel.service" > "/etc/systemd/system/$SERVICE.service"

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"
sleep 3
if systemctl is-active --quiet "$SERVICE"; then
  echo "==> Painel no ar em http://127.0.0.1:$PORT (servico $SERVICE)."
  echo "    Logs: journalctl -u $SERVICE -f"
else
  echo "!! O servico nao subiu. Veja: journalctl -u $SERVICE -n 50"
  exit 1
fi
