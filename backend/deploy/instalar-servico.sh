#!/usr/bin/env bash
#
# instalar-servico.sh — publica a API NFe e registra como servico systemd.
#
# O que ele faz (idempotente — pode rodar de novo a cada deploy):
#   1. Publica o backend (Release) numa pasta fixa (padrao /opt/aprovador-nfe);
#   2. Cria /etc/aprovador-nfe.env na primeira vez (a partir do exemplo);
#   3. Gera o unit do systemd a partir do modelo, com os caminhos certos;
#   4. Habilita e (re)inicia o servico.
#
# Uso:
#   cd backend
#   sudo bash deploy/instalar-servico.sh
#
# Variaveis para customizar (opcionais):
#   APPDIR=/opt/aprovador-nfe   SERVICE=aprovador-nfe   RUNUSER=ubuntu
#
set -euo pipefail

# ---- Descobre caminhos ------------------------------------------------------
# Pasta 'backend' = pai da pasta 'deploy' onde este script esta.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

SERVICE="${SERVICE:-aprovador-nfe}"
APPDIR="${APPDIR:-/opt/aprovador-nfe}"
ENVFILE="${ENVFILE:-/etc/aprovador-nfe.env}"
# Usuario que roda o servico: quem chamou o sudo, ou 'ubuntu' como padrao.
RUNUSER="${RUNUSER:-${SUDO_USER:-ubuntu}}"
CSPROJ="$BACKEND_DIR/src/NFe.Api/NFe.Api.csproj"

if [ "$(id -u)" -ne 0 ]; then
  echo "!! Rode com sudo:  sudo bash deploy/instalar-servico.sh"
  exit 1
fi

# ---- Localiza o dotnet ------------------------------------------------------
# 'command -v' pode falhar sob sudo (PATH restrito); testa locais comuns.
DOTNET="$(command -v dotnet || true)"
for c in /usr/bin/dotnet /usr/lib/dotnet/dotnet /usr/share/dotnet/dotnet /snap/bin/dotnet; do
  [ -z "$DOTNET" ] && [ -x "$c" ] && DOTNET="$c"
done
if [ -z "$DOTNET" ]; then
  echo "!! Nao encontrei o 'dotnet'. Instale o .NET SDK 9 ou ajuste o PATH."
  exit 1
fi
echo "==> dotnet: $DOTNET"
echo "==> usuario do servico: $RUNUSER"
echo "==> pasta de publicacao: $APPDIR"

# ---- Publica o app ----------------------------------------------------------
echo "==> Publicando a API (Release) em $APPDIR ..."
rm -rf "$APPDIR"
mkdir -p "$APPDIR"
"$DOTNET" publish "$CSPROJ" -c Release -o "$APPDIR"
chown -R "$RUNUSER":"$RUNUSER" "$APPDIR"

# ---- Arquivo de variaveis de ambiente ---------------------------------------
if [ ! -f "$ENVFILE" ]; then
  echo "==> Criando $ENVFILE a partir do exemplo (PREENCHA os segredos depois!)."
  cp "$SCRIPT_DIR/aprovador-nfe.env.example" "$ENVFILE"
  chmod 600 "$ENVFILE"
  chown root:root "$ENVFILE"
  CRIOU_ENV=1
else
  echo "==> $ENVFILE ja existe — mantendo os valores atuais."
  CRIOU_ENV=0
fi

# ---- Gera o unit do systemd a partir do modelo ------------------------------
echo "==> Instalando /etc/systemd/system/$SERVICE.service ..."
sed \
  -e "s#__DOTNET__#$DOTNET#g" \
  -e "s#__APPDIR__#$APPDIR#g" \
  -e "s#__USER__#$RUNUSER#g" \
  -e "s#__ENVFILE__#$ENVFILE#g" \
  "$SCRIPT_DIR/aprovador-nfe.service" > "/etc/systemd/system/$SERVICE.service"

# ---- Sobe o servico ---------------------------------------------------------
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1 || true

if [ "$CRIOU_ENV" -eq 1 ]; then
  echo ""
  echo "*******************************************************************"
  echo "  ATENCAO: preencha os segredos antes de iniciar:"
  echo "    sudo nano $ENVFILE"
  echo "  Depois inicie com:"
  echo "    sudo systemctl start $SERVICE"
  echo "*******************************************************************"
else
  systemctl restart "$SERVICE"
  echo "==> Servico (re)iniciado."
fi

echo ""
echo "==> Pronto. Comandos uteis:"
echo "    sudo systemctl status $SERVICE        # ver estado"
echo "    journalctl -u $SERVICE -f             # ver logs ao vivo"
echo "    sudo systemctl restart $SERVICE       # reiniciar"
echo "    sudo bash deploy/instalar-servico.sh  # re-deploy (republica e reinicia)"
