#!/usr/bin/env bash
# Atualiza API e painel a partir do GitHub, em ordem e parando no primeiro erro.
#
#   ~/apps/autorizador/deploy/atualizar.sh            # atualiza o que mudou
#   ~/apps/autorizador/deploy/atualizar.sh --forcar   # recompila e reinicia tudo mesmo sem mudancas
#   ~/apps/autorizador/deploy/atualizar.sh --so-api   # so a API
#   ~/apps/autorizador/deploy/atualizar.sh --so-painel
#
# Seguranca: a API so e reiniciada DEPOIS de compilar sem erro (se o build
# falhar, a versao no ar continua rodando). O mesmo vale para o painel.
# Variaveis opcionais: API_URL (padrao http://127.0.0.1:5240),
# PAINEL_URL (padrao http://127.0.0.1:5241), SERVICO_API (nfe-api),
# SERVICO_PAINEL (nfe-app).
set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_URL="${API_URL:-http://127.0.0.1:5240}"
PAINEL_URL="${PAINEL_URL:-http://127.0.0.1:5241}"
SERVICO_API="${SERVICO_API:-nfe-api}"
SERVICO_PAINEL="${SERVICO_PAINEL:-nfe-app}"

FORCAR=0; FAZ_API=1; FAZ_PAINEL=1
for a in "$@"; do
  case "$a" in
    --forcar) FORCAR=1 ;;
    --so-api) FAZ_PAINEL=0 ;;
    --so-painel) FAZ_API=0 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "Opcao desconhecida: $a (use --help)"; exit 2 ;;
  esac
done

verde() { printf '\033[32m%s\033[0m\n' "$*"; }
amarelo() { printf '\033[33m%s\033[0m\n' "$*"; }
falha() { printf '\033[31mERRO: %s\033[0m\n' "$*" >&2; exit 1; }
passo() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

# node/npx do nvm, quando o shell nao carregou (ex.: rodando via ssh/cron)
if ! command -v npx >/dev/null 2>&1 && [ -s "$HOME/.nvm/nvm.sh" ]; then
  # shellcheck disable=SC1091
  . "$HOME/.nvm/nvm.sh" >/dev/null
fi

cd "$RAIZ"

# Pede a senha do sudo uma vez, no inicio (nao no meio do deploy).
sudo -v || falha "sem permissao de sudo para reiniciar os servicos."

passo "1/5 Baixando do GitHub"
ANTES="$(git rev-parse HEAD)"
git pull --ff-only || falha "git pull falhou (alteracoes locais? rode 'git status')."
DEPOIS="$(git rev-parse HEAD)"
if [ "$ANTES" = "$DEPOIS" ]; then
  echo "  Codigo ja estava atualizado ($(git log -1 --format='%h %s'))."
else
  git log --format='  %h %s' "$ANTES..$DEPOIS"
fi

# Ultimo commit implantado COM SUCESSO (API e painel separados). Comparar com
# ele, e nao com o pull, faz um deploy que falhou no meio ser refeito na
# proxima execucao.
MARCA_API="$RAIZ/.git/deploy-ok-api"
MARCA_PAINEL="$RAIZ/.git/deploy-ok-painel"
BASE_API="$(cat "$MARCA_API" 2>/dev/null || true)"
BASE_PAINEL="$(cat "$MARCA_PAINEL" 2>/dev/null || true)"

# mudou BASE caminhos...: houve mudanca desde BASE (sem BASE ou --forcar = sim)
mudou() {
  local base="$1"; shift
  [ "$FORCAR" -eq 1 ] && return 0
  [ -z "$base" ] && return 0
  git cat-file -e "$base^{commit}" 2>/dev/null || return 0
  ! git diff --quiet "$base" "$DEPOIS" -- "$@"
}

PRECISA_API=0; PRECISA_PAINEL=0
[ "$FAZ_API" -eq 1 ] && mudou "$BASE_API" backend && PRECISA_API=1
[ "$FAZ_PAINEL" -eq 1 ] && mudou "$BASE_PAINEL" . ':(exclude)backend' ':(exclude)docs' && PRECISA_PAINEL=1
if [ "$PRECISA_API" -eq 0 ] && [ "$PRECISA_PAINEL" -eq 0 ]; then
  amarelo "API e painel ja estao no ar nesta versao. Nada a fazer (use --forcar para refazer)."
  exit 0
fi

# ---------------------------------------------------------------- API
if [ "$PRECISA_API" -eq 1 ]; then
  VERSAO="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' backend/src/NFe.Api/NFe.Api.csproj | head -1)"
  passo "2/5 Compilando a API ${VERSAO}"
  (cd backend && dotnet build -c Release -nologo -v q) || falha "build da API falhou; a versao no ar NAO foi alterada."

  passo "3/5 Reiniciando a API (${SERVICO_API})"
  sudo systemctl restart "$SERVICO_API"
  SAUDE=""
  for _ in $(seq 1 30); do
    SAUDE="$(curl -s -m 3 "$API_URL/health" || true)"
    [ -n "$SAUDE" ] && break
    sleep 2
  done
  [ -n "$SAUDE" ] || falha "a API nao respondeu em $API_URL/health. Veja: journalctl -u $SERVICO_API -n 50"
  echo "  $SAUDE"
  case "$SAUDE" in
    *"\"versao\":\"$VERSAO\""*) verde "  API no ar na versao $VERSAO."; echo "$DEPOIS" > "$MARCA_API" ;;
    *) falha "a API respondeu, mas nao na versao $VERSAO. Confira o ExecStart do servico." ;;
  esac
else
  passo "2-3/5 API sem mudancas: nao recompilada"
fi

# ---------------------------------------------------------------- Painel
if [ "$PRECISA_PAINEL" -eq 1 ]; then
  command -v npx >/dev/null 2>&1 || falha "npx nao encontrado (instale o Node ou carregue o nvm)."
  if mudou "$BASE_PAINEL" package.json pnpm-lock.yaml; then
    passo "4/5 Instalando dependencias do painel"
    npx --yes pnpm@9 install --frozen-lockfile
  fi
  passo "4/5 Compilando o painel"
  npx --yes pnpm@9 build || falha "build do painel falhou; a versao no ar NAO foi alterada."

  passo "5/5 Reiniciando o painel (${SERVICO_PAINEL})"
  sudo systemctl restart "$SERVICO_PAINEL"
  CODIGO=""
  for _ in $(seq 1 30); do
    CODIGO="$(curl -s -o /dev/null -m 3 -w '%{http_code}' "$PAINEL_URL/login" || true)"
    [ "$CODIGO" = "200" ] && break
    sleep 2
  done
  [ "$CODIGO" = "200" ] || falha "o painel nao respondeu em $PAINEL_URL/login (HTTP $CODIGO). Veja: journalctl -u $SERVICO_PAINEL -n 50"
  verde "  Painel no ar."
  echo "$DEPOIS" > "$MARCA_PAINEL"
else
  passo "4-5/5 Painel sem mudancas: nao recompilado"
fi

verde "
Deploy concluido: $(git log -1 --format='%h %s')"
