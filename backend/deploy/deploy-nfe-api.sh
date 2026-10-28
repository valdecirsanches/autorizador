#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BACKEND_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd "$BACKEND_DIR/.." && pwd)"
SERVICE="${SERVICE:-nfe-api.service}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Execute com sudo: sudo bash backend/deploy/deploy-nfe-api.sh" >&2
  exit 1
fi

command -v dotnet >/dev/null || {
  echo "dotnet SDK não encontrado no PATH." >&2
  exit 1
}
command -v systemctl >/dev/null || {
  echo "systemctl não encontrado; este script é para servidores systemd." >&2
  exit 1
}

SERVICE_WORKDIR="$(systemctl show "$SERVICE" -p WorkingDirectory --value)"
PUBLISH_DIR="${PUBLISH_DIR:-${SERVICE_WORKDIR:-$BACKEND_DIR/publicado}}"
CSPROJ="$BACKEND_DIR/src/NFe.Api/NFe.Api.csproj"
STAGING_DIR="$(mktemp -d "$BACKEND_DIR/.deploy-nfe-api.XXXXXX")"
WAS_ACTIVE=0
DEPLOY_FINISHED=0

if systemctl is-active --quiet "$SERVICE"; then
  WAS_ACTIVE=1
fi

cleanup() {
  rm -rf "$STAGING_DIR"
  if [[ "$WAS_ACTIVE" -eq 1 && "$DEPLOY_FINISHED" -eq 0 ]]; then
    systemctl start "$SERVICE" || true
  fi
}
trap cleanup EXIT

printf '==> Serviço: %s\n' "$SERVICE"
printf '==> Publicação: %s\n' "$PUBLISH_DIR"
printf '==> Repositório: %s\n' "$REPO_DIR"

SERVICE_ENV_FILE="$(systemctl show "$SERVICE" -p EnvironmentFiles --value | awk 'NR == 1 { sub(/[[:space:]].*$/, ""); sub(/^-/, ""); print; exit }')"
MIGRATION_ENV_ARGS=()
if [[ -n "$SERVICE_ENV_FILE" ]]; then
  [[ -f "$SERVICE_ENV_FILE" ]] || {
    echo "EnvironmentFile do serviço não encontrado: $SERVICE_ENV_FILE" >&2
    exit 1
  }
  MIGRATION_ENV_ARGS=(--env-file "$SERVICE_ENV_FILE")
fi

# Publica em staging e aplica/valida o schema antes de interromper a API.
# Se faltar acesso ao banco ou uma migration falhar, o deploy aborta aqui e a
# versão atualmente ativa continua servindo as requisições.
dotnet publish "$CSPROJ" -c Release -o "$STAGING_DIR"
(
  cd "$STAGING_DIR"
  dotnet "$STAGING_DIR/NFe.Api.dll" "${MIGRATION_ENV_ARGS[@]}" --migrate-database
  dotnet "$STAGING_DIR/NFe.Api.dll" "${MIGRATION_ENV_ARGS[@]}" --ensure-json-recebido-schema
  dotnet "$STAGING_DIR/NFe.Api.dll" "${MIGRATION_ENV_ARGS[@]}" --ensure-api-request-audit-schema
)

if [[ "$WAS_ACTIVE" -eq 1 ]]; then
  systemctl stop "$SERVICE"
fi
mkdir -p "$PUBLISH_DIR"
cp -a "$STAGING_DIR/." "$PUBLISH_DIR/"

SERVICE_USER="$(systemctl show "$SERVICE" -p User --value)"
if [[ -n "$SERVICE_USER" ]]; then
  SERVICE_GROUP="$(id -gn "$SERVICE_USER")"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$PUBLISH_DIR"
fi

systemctl start "$SERVICE"
systemctl is-active --quiet "$SERVICE" || {
  echo "O serviço $SERVICE não ficou ativo após o deploy." >&2
  exit 1
}

DEPLOY_FINISHED=1
printf '==> Deploy concluído; migrations, json_recebido e api_request_log verificados, API ativa.\n'
