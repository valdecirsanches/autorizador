#!/usr/bin/env bash
#
# run.sh — sobe a API NFe limpando o cache de build corrompido.
#
# Contexto: o `dotnet run` faz build incremental e lê o NOME do executável do
# cache em obj/. Se esse cache ficou corrompido (ex.: um apphost com nome de
# hash como c3ab8ff... = sha256("foo")), o `dotnet run` insiste em executar o
# binário errado mesmo com o .csproj correto. Apagar bin/ e obj/ resolve de vez.
#
# Uso:
#   cd backend
#   bash run.sh              # limpa, builda e roda
#   bash run.sh --clean-only # só limpa o cache, não roda
#
set -euo pipefail

# Vai para a pasta onde este script está (backend/), independente de onde foi chamado.
cd "$(dirname "$0")"

PORTA="${PORTA:-5240}"
URL="http://127.0.0.1:${PORTA}"

echo "==> Limpando cache de build (bin/ e obj/) de todos os projetos..."
# Sem \( \) para não quebrar em nenhuma colagem. Dois finds simples e robustos.
find . -type d -name bin -exec rm -rf {} + 2>/dev/null || true
find . -type d -name obj -exec rm -rf {} + 2>/dev/null || true

RESTANTES="$(find . -type d -name bin -o -type d -name obj | wc -l | tr -d ' ')"
if [ "$RESTANTES" != "0" ]; then
  echo "!! Ainda restaram $RESTANTES pastas bin/obj — verifique permissões:"
  find . -type d -name bin -o -type d -name obj
  exit 1
fi
echo "==> Cache limpo (0 pastas bin/obj restantes)."

if [ "${1:-}" = "--clean-only" ]; then
  echo "==> --clean-only: encerrando sem rodar."
  exit 0
fi

echo "==> Restaurando pacotes..."
dotnet restore

echo "==> Compilando NFe.Api..."
dotnet build src/NFe.Api/NFe.Api.csproj -c Debug

echo "==> Subindo a API em ${URL} ..."
# Executa o DLL diretamente — evita qualquer resolução de nome pelo `dotnet run`.
exec dotnet "src/NFe.Api/bin/Debug/net9.0/NFe.Api.dll" --urls "${URL}"
