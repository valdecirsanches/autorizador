#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Teste ponta a ponta da emissao de NF-e nesta API.
#
# Uso:
#   chmod +x testar-nfe.sh
#   ./testar-nfe.sh                      # usa http://127.0.0.1:5240 e o CNPJ de exemplo
#   BASE_URL=https://nfe-api.x-star.com.br CNPJ=67570036000181 NUMERO=273 ./testar-nfe.sh
#
# Requisitos: curl e jq (sudo apt-get install -y jq)
# ---------------------------------------------------------------------------
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5240}"
CNPJ="${CNPJ:-11222333000181}"          # apenas digitos, 14 caracteres
AMBIENTE="${AMBIENTE:-2}"               # 1=Producao, 2=Homologacao (vai em configuracao.ambiente)
EMITENTE_RAZAO_SOCIAL="${EMITENTE_RAZAO_SOCIAL:-EMPRESA EXEMPLO LTDA}"
EMITENTE_IE="${EMITENTE_IE:-123456789}" # use ISENTO quando aplicavel
EMITENTE_REGIME="${EMITENTE_REGIME:-3}" # 1=Simples, 2=Simples sublimite, 3=Normal, 4=MEI
NUMERO="${NUMERO:-$(date +%s | tail -c 10)}" # numero controlado pelo cliente
IDEMPOTENCY_KEY="${IDEMPOTENCY_KEY:-teste-$(date +%s)}" # somente auditoria

azul()  { printf "\n\033[1;34m== %s ==\033[0m\n" "$1"; }
falha() { printf "\033[1;31mFALHOU: %s\033[0m\n" "$1"; exit 1; }

command -v jq >/dev/null || falha "instale o jq: sudo apt-get install -y jq"
[[ "$NUMERO" =~ ^[1-9][0-9]{0,8}$ ]] || falha "NUMERO deve ser um inteiro positivo de até 9 dígitos."

# ---------------------------------------------------------------------------
azul "1) Health check em $BASE_URL"
curl -fsS "$BASE_URL/health" | jq . || falha "API nao respondeu em $BASE_URL"

# ---------------------------------------------------------------------------
azul "2) Resolver o Id da empresa pelo CNPJ $CNPJ"
EMPRESA_JSON="$(curl -fsS "$BASE_URL/api/empresas/por-cnpj/$CNPJ")" \
  || falha "CNPJ nao encontrado. Cadastre a empresa no painel primeiro."
echo "$EMPRESA_JSON" | jq .
EMPRESA_ID="$(echo "$EMPRESA_JSON" | jq -r '.id')"
TEM_CERT="$(echo "$EMPRESA_JSON" | jq -r '.temCertificadoAtivo')"
echo "-> EMPRESA_ID = $EMPRESA_ID"
[ "$TEM_CERT" = "true" ] || echo "AVISO: empresa sem certificado ativo; a emissao vai falhar ate cadastrar o A1."

# ---------------------------------------------------------------------------
azul "3) Emitir a NF-e (ambiente=$AMBIENTE, numero=$NUMERO, Idempotency-Key de auditoria=$IDEMPOTENCY_KEY)"
HTTP_E_CORPO="$(curl -sS -w $'\n%{http_code}' \
  -X POST "$BASE_URL/api/empresas/$EMPRESA_ID/nfe" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $IDEMPOTENCY_KEY" \
  -d "$(cat <<JSON
  {
    "configuracao": { "ambiente": $AMBIENTE, "contingencia": false },
    "emitente": {
      "cnpj": "$CNPJ",
      "razaoSocial": "$EMITENTE_RAZAO_SOCIAL",
      "inscricaoEstadual": "$EMITENTE_IE",
      "regimeTributario": $EMITENTE_REGIME,
      "logradouro": "Rua Exemplo",
      "numero": "100",
      "bairro": "Centro",
      "codigoMunicipio": "3550308",
      "nomeMunicipio": "Sao Paulo",
      "uf": "SP",
      "cep": "01001000"
    },
    "serie": 1,
    "numero": $NUMERO,
    "naturezaOperacao": "VENDA DE MERCADORIA",
    "consumidorFinal": true,
    "indicadorPresenca": 1,
    "destinatario": {
      "documento": "11144477735",
      "nome": "CONSUMIDOR FINAL",
      "uf": "SP"
    },
    "itens": [
      {
        "codigo": "PROD-001",
        "descricao": "PRODUTO DE TESTE",
        "ncm": "22030000",
        "cfop": "5102",
        "unidadeComercial": "UN",
        "quantidade": 1,
        "valorUnitario": 10.00,
        "valorBruto": 10.00,
        "origemMercadoria": 0,
        "cstIcms": "102"
      }
    ],
    "pagamentos": [
      { "meio": "01", "valor": 10.00 }
    ]
  }
JSON
  )")"

CORPO="$(echo "$HTTP_E_CORPO" | sed '$d')"
HTTP="$(echo "$HTTP_E_CORPO" | tail -n1)"
echo "HTTP $HTTP"
echo "$CORPO" | jq . 2>/dev/null || echo "$CORPO"

DOC_ID="$(echo "$CORPO" | jq -r '.id // empty' 2>/dev/null || true)"

case "$HTTP" in
  200) azul "AUTORIZADA! chave: $(echo "$CORPO" | jq -r '.chaveAcesso')" ;;
  422) azul "REJEITADA pela SEFAZ (veja motivoRetorno acima). O fluxo funcionou." ;;
  404) falha "Empresa nao encontrada." ;;
  *)   falha "Resposta inesperada (HTTP $HTTP)." ;;
esac

# ---------------------------------------------------------------------------
if [ -n "$DOC_ID" ]; then
  azul "4) Detalhe do documento + trilha de eventos"
  curl -fsS "$BASE_URL/api/nfe/$DOC_ID" | jq '{id, chaveAcesso, status, statusDescricao, protocolo, motivoRetorno, eventos}'

  azul "5) Baixar o XML (so existe se autorizada)"
  curl -sS -o "/tmp/nfe-$DOC_ID.xml" -w "HTTP %{http_code} -> /tmp/nfe-$DOC_ID.xml\n" \
    "$BASE_URL/api/nfe/$DOC_ID/xml" || true
fi

azul "6) Concluido. Cada POST e uma nova tentativa; a aplicacao cliente controla repeticoes e numeracao."
