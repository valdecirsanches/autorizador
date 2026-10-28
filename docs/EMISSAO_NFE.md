# Emissão de NF-e via API

Guia completo para o seu app enviar NF-e para autorização nesta API, em
substituição ao ACBrMonitor.

## Como funciona (visão geral)

Diferente do ACBrMonitor (que você chama por arquivo/TCP e monta o INI da nota),
aqui você faz **uma chamada HTTP JSON** e a API cuida de todo o ciclo:

```
Seu app  --POST JSON-->  API  -->  usa o número que VOCÊ enviou
                                -->  gera o XML 4.00
                                -->  assina com o certificado A1 da empresa
                                -->  valida contra o XSD (opcional)
                                -->  transmite à SEFAZ (lote síncrono, indSinc=1)
                                     (com auto-retry em falha de rede)
                                -->  grava protocolo + trilha de auditoria
         <--JSON + XML + DANFE-->  API
```

Você **não** monta XML nem assina. O JSON informa a configuração fiscal, o
ambiente e o cadastro completo do emitente; a API usa o `empresaId` apenas para
localizar o certificado A1 e continua resolvendo os endpoints da SEFAZ.

### O que mudou nesta versão (importante para o seu app)

1. **A numeração agora é sua.** A API **não** reserva nem incrementa mais o
   `numero` (nNF). Você envia o número, a API usa exatamente esse. Se dois
   documentos colidirem no mesmo número, a SEFAZ rejeita por duplicidade
   (cStat 539) — o controle é 100% do seu app.
2. **Não trava mais em falha de transmissão.** Erros de rede/timeout são
   reenviados automaticamente (até 3x) na mesma chamada. Se ainda assim falhar,
   a nota fica em `ErroTransmissao` (não some) e pode ser reenviada por
   `POST /api/nfe/{id}/reenviar`, reaproveitando o mesmo XML e o mesmo número.
3. **A resposta de autorização já traz o XML e o DANFE em PDF** (Base64), sem
   precisar de chamadas extras. Veja "Respostas" abaixo.

## Autenticação (obrigatória)

A API é **protegida por token**. Toda requisição — exceto `GET /health` — precisa
enviar o cabeçalho:

```
Authorization: Bearer <NFE_API_TOKEN>
```

- O token é o valor da variável de ambiente **`NFE_API_TOKEN`** do servidor da API.
- Sem o cabeçalho (ou com token errado) a API responde **`401`**
  `{ "erro": "Nao autorizado." }` e não processa nada.
- A API **não inicia** se `NFE_API_TOKEN` não estiver definida — ela nunca roda
  aberta por engano.
- A comparação é feita em tempo constante (não vaza o token por tempo de resposta).

Exemplo com `curl`:

```bash
curl -X POST https://SEU_HOST/api/nfe \
  -H "Authorization: Bearer $NFE_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d @nota.json
```

> **Painel web**: o painel Next.js tem login próprio (usuário/senha de admin) e
> injeta o `NFE_API_TOKEN` no servidor — o navegador nunca vê o token. Veja
> "Segurança do painel" no fim deste guia. **Ação necessária:** ao ativar a
> autenticação, atualize o seu app emissor para enviar o header `Bearer`, senão
> as emissões passam a receber `401`.

### Erro ao aprovar/emitir NF-e — como diagnosticar

Duas falhas parecem "erro de token", mas têm causas diferentes. **Olhe o código
HTTP e o campo `detalhe` da resposta**, não a mensagem do seu app:

| Sintoma | Código | Causa real | Solução |
| --- | --- | --- | --- |
| `{ "erro": "Nao autorizado." }` | **401** | Token ausente, errado, ou **diferente** entre o app e a API | Use o **mesmo** `NFE_API_TOKEN` nas duas pontas (veja abaixo) |
| `detalhe: "Falha na verificacao de autenticidade ao decifrar..."` | **500** | **Não é token.** É a `NFE_CERT_MASTER_KEY` diferente da que cifrou o certificado | Reenvie o certificado A1 com a chave mestra atual |

> Repare: o erro `500` de decifragem **não tem nada a ver com token** — a
> requisição já passou pela autenticação. Ele é do certificado. Veja
> "Certificado não decifra" mais abaixo.

#### Cuidado com variáveis duplicadas (`_2`, `_3`, `_4`)

A causa nº 1 de "token não aceito" neste projeto é ter **várias cópias** da
mesma variável. Ao adicionar uma variável que já existe, o valor **não** é
sobrescrito — cria-se uma cópia com sufixo. Hoje existem, por exemplo:

- `NFE_API_TOKEN`, `NFE_API_TOKEN_2`, `NFE_API_TOKEN_3`, `NFE_API_TOKEN_4`
- `NFE_API_URL`, `NFE_API_URL_2`

**O código lê apenas a versão SEM sufixo** (`NFE_API_TOKEN` e `NFE_API_URL`). As
`_2`, `_3`, `_4` são ignoradas pela API e pelo painel. Mas o **seu app emissor**
pode estar lendo uma delas (o teste de conexão chegou a mostrar
`NFE_API_URL_2`). Se o app manda o token de `NFE_API_TOKEN_2` e a API valida
contra `NFE_API_TOKEN`, e os valores diferem, o resultado é `401`.

**Regra:** o token que o app envia (`Authorization: Bearer <valor>`) tem de ser
**idêntico** ao `NFE_API_TOKEN` que a API carregou. Para não errar:

1. Apague as duplicatas, deixando **uma** variável de cada: `NFE_API_TOKEN` e
   `NFE_API_URL` (remova `_2`/`_3`/`_4`).
2. Confirme que o app aponta para `NFE_API_URL` (sem sufixo) e envia o token de
   `NFE_API_TOKEN` (sem sufixo).
3. Reinicie a API e o app após a limpeza.

#### Teste rápido do token (sem o app)

Isole o problema chamando a API direto. Substitua `SEU_TOKEN` pelo valor de
`NFE_API_TOKEN`:

```bash
# Deve responder 200 sem token (health e liberado)
curl -i http://127.0.0.1:5240/health

# Deve responder 200/JSON — token correto
curl -i -H "Authorization: Bearer SEU_TOKEN" http://127.0.0.1:5240/api/empresas

# Deve responder 401 — prova que a protecao esta ativa
curl -i -H "Authorization: Bearer errado" http://127.0.0.1:5240/api/empresas
```

Se a 2ª chamada der `401`, o token que você colou **não** é o que a API
carregou — reveja as duplicatas acima.

## Pré-requisitos

1. **Empresa/carteira de certificado cadastrada** (`POST /api/empresas`) — o `empresaId` (GUID) identifica onde o certificado está armazenado. Os dados fiscais cadastrados ali não são usados para montar a NF-e.
2. **Certificado A1 ativo** para esse `empresaId` (`POST /api/empresas/{id}/certificados`), correspondente ao CNPJ enviado no JSON.
3. A API no ar e alcançável (localmente `http://127.0.0.1:5240`, em produção via seu domínio/túnel).

## Base URL

- Local: `http://127.0.0.1:5240`
- Produção (seu túnel): `https://nfe-api.x-star.com.br`

---

## 1) Emitir (autorizar) uma NF-e

```
POST /api/empresas/{empresaId}/nfe
Content-Type: application/json
Idempotency-Key: <opcional, para auditoria>
```

Não use query params para ambiente ou contingência. Ambos são informados no JSON.

### Corpo da requisição

O objeto `configuracao` e o objeto `emitente` são obrigatórios em toda emissão.
Os campos fiscais enviados no JSON são a fonte usada para gerar a NF-e; o cadastro
persistido da empresa não substitui esses dados.

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `configuracao` | objeto | sim | Ambiente e opções de roteamento; ver abaixo. |
| `emitente` | objeto | sim | Cadastro fiscal completo; ver abaixo. |

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `serie` | int | sim | Série da nota. |
| `numero` | long | **sim** | nNF. **A numeração é controlada pelo seu app** — envie um número positivo. A API não gera mais número automaticamente; enviar `0`/omitido resulta em `400`. A API aceita novas tentativas no mesmo número, inclusive após rejeição; a aplicação consumidora decide como controlar essa numeração. |
| `codigoNumerico` | string(8) | não | cNF. Se vazio, é sorteado. |
| `modelo` | int | não | `55` NF-e (padrão), `65` NFC-e. |
| `tipoOperacao` | int | não | `0` entrada, `1` saída (padrão). |
| `tipoEmissao` | int | não | `1` normal (padrão). |
| `destinoOperacao` | int | não | `1` interna, `2` interestadual, `3` exterior. |
| `indicadorPresenca` | int | não | `0`..`9` (indPres). |
| `naturezaOperacao` | string | sim | Ex.: "VENDA DE MERCADORIA". |
| `dataEmissao` | ISO 8601 | não | Padrão: agora (UTC). |
| `dataSaidaEntrada` | ISO 8601 | não | |
| `finalidade` | int | não | finNFe: `1` normal (padrão), `2` complementar, `3` ajuste, `4` **devolução de mercadoria**. Fora de 1..4 retorna erro. Os valores `2`, `3` e `4` exigem `documentosReferenciados`. |
| `documentosReferenciados` | array | não | NF-e referenciadas (`<NFref><refNFe>` no fim do `ide`), até 500. Cada item: `{ "chaveNFe": "44 dígitos" }` (máscara e espaços são ignorados; DV conferido; chave repetida é recusada). **Obrigatório quando `finalidade` for 2, 3 ou 4.** Sem o campo e com `finalidade=1`, o XML não muda. Ver "Devolução de mercadoria" abaixo. |
| `consumidorFinal` | bool | não | Padrão `true`. |
| `destinatario` | objeto | sim | Ver abaixo. |
| `itens` | array | sim (≥1) | Ver abaixo. |
| `modalidadeFrete` | int | não | `9` sem transporte (padrão). |
| `transportadora` | objeto | não | Gera o grupo `<transporta>` no XML (e aparece no DANFE). Ver abaixo. |
| `veiculo` | objeto | não | Gera `<veicTransp>` no XML (e aparece no DANFE). Ver abaixo. |
| `quantidadeVolumes` | inteiro | não | `qVol`, maior que zero e até 15 dígitos. Obrigatório se `especie` ou `marca` for informado. |
| `especie` | string | não | `esp`, até 60 caracteres. |
| `marca` | string | não | `marca`, até 60 caracteres. |
| `pesoLiquido` | decimal | não | Peso líquido total em kg, serializado como `pesoL`, até 3 casas decimais (XSD `TDec_1203`). |
| `pesoBruto` | decimal | não | Peso bruto total em kg, serializado como `pesoB`, até 3 casas decimais (XSD `TDec_1203`). |
| `valorFrete` | decimal | não | **Frete da nota** (cabeçalho). O layout 4.00 só tem `vFrete` por item e no total, então a API rateia o valor em `det/prod/vFrete` proporcionalmente ao `vProd` de cada item (sobra de centavos na última linha) e soma a eventuais `itens[].valorFrete`. O `vFrete` do total é a soma exata dos itens e entra no `vNF`. Não pode ser negativo e exige `modalidadeFrete` diferente de `9` (sem transporte). |
| `outrasDespesas` | decimal | não | **Despesas acessórias / acréscimo** da nota (não é frete nem seguro). A API rateia esse valor em `vOutro` proporcionalmente ao `vProd` de cada item — **sem alterar o preço unitário** — e joga a sobra de centavos na última linha, de modo que `Σvitens = vOutro` do total e o `vNF` feche exatamente (evita a rejeição **610**). Somado a eventuais `itens[].valorOutro`. |
| `pagamentos` | array | não | Ver abaixo. |
| `informacoesComplementares` | string | não | |

**Devolução de mercadoria (`finalidade` = 4)**

A API gera `finNFe=4` e o grupo `NFref/refNFe` com a chave da nota original; a SEFAZ
rejeita devolução sem documento referenciado, por isso a API recusa a requisição
antes de enviar. O restante é decisão do seu app e **não é inferido pela API**:

- `tipoOperacao`: devolução de venda recebida do cliente é **entrada** (`0`); devolução de compra ao fornecedor é **saída** (`1`).
- `cfop` de cada item: série 1.2xx/2.2xx para entrada (ex.: `1202`, `2202`) e 5.2xx/6.2xx para saída (ex.: `5202`, `6202`).
- `destinoOperacao`, tributos (ICMS, IPI, ST, PIS/COFINS) e IBS/CBS dos itens: informe como na nota original.

```json
{
  "finalidade": 4,
  "tipoOperacao": 0,
  "naturezaOperacao": "DEVOLUCAO DE VENDA",
  "documentosReferenciados": [
    { "chaveNFe": "35240712345678000195550010000000991000000779" }
  ]
}
```

**`configuracao`**

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `ambiente` | int | sim | `1` produção ou `2` homologação. Não há fallback para `AmbientePadrao`. |
| `contingencia` | bool | não | Usa o autorizador SVC; padrão `false`. |
| `ufAutorizadorOverride` | string | não | Sigla da UF do autorizador quando o estado usa autorizador delegado. |
| `orientacaoDanfe` | string | não | Orientação da página A4 do `danfePdfBase64` devolvido: `"retrato"` (padrão) ou `"paisagem"`. Outro valor retorna 400. Não altera o XML. |

**`emitente`**

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `cnpj` | string | sim | 14 dígitos; deve corresponder ao certificado A1 associado ao `empresaId`. |
| `razaoSocial` | string | sim | |
| `nomeFantasia` | string | não | |
| `inscricaoEstadual` | string | sim | Use `ISENTO` quando aplicável. |
| `inscricaoMunicipal`,`cnaeFiscal` | string | não | Dados cadastrais opcionais. |
| `regimeTributario` | int | sim | `1` Simples, `2` Simples sublimite, `3` regime normal, `4` MEI. |
| `logradouro`,`numero`,`bairro` | string | sim | Endereço do emitente. |
| `codigoMunicipio` | string | sim | Código IBGE de 7 dígitos. |
| `nomeMunicipio`,`uf` | string | sim | UF com 2 letras. |
| `cep` | string | sim | 8 dígitos. |
| `complemento`,`telefone` | string | não | |
| `codigoPais`,`nomePais` | string | não | Padrão `1058` / `BRASIL`. |

Os campos de volume são opcionais e ficam na raiz do JSON, no mesmo nível de
`modalidadeFrete`:

```json
{
  "modalidadeFrete": 0,
  "quantidadeVolumes": 2,
  "especie": "CAIXA",
  "marca": "MINHA MARCA",
  "pesoLiquido": 12.34,
  "pesoBruto": 15.678
}
```

`pesoLiquido` e `pesoBruto` são opcionais e informados em quilogramas (kg),
com até 3 casas decimais, conforme o XSD `TDec_1203`. São gravados no XML autorizado como `pesoL` e `pesoB`;
o DANFE lê os valores desses elementos do XML da NF-e.

**`transportadora`** (opcional, grupo `<transporta>`)

| Campo | Tipo | Observação |
|---|---|---|
| `cnpj` ou `cpf` | string | Só dígitos. Informe um dos dois, nunca ambos. |
| `nome` | string | `xNome`. |
| `inscricaoEstadual` | string | `IE`; use `ISENTO` quando aplicável. |
| `endereco` | string | `xEnder`. |
| `municipio` | string | `xMun`. |
| `uf` | string | 2 letras. |

**`veiculo`** (opcional, grupo `<veicTransp>`)

| Campo | Tipo | Observação |
|---|---|---|
| `placa` | string | `placa`, sem hífen. |
| `uf` | string | UF da placa, 2 letras. |
| `rntc` | string | Registro Nacional de Transportador de Carga (opcional). |

```json
{
  "modalidadeFrete": 0,
  "transportadora": {
    "cnpj": "12345678000199",
    "nome": "TRANSPORTES EXEMPLO LTDA",
    "inscricaoEstadual": "123456789",
    "endereco": "RUA DAS CARGAS, 100",
    "municipio": "SAO PAULO",
    "uf": "SP"
  },
  "veiculo": { "placa": "ABC1D23", "uf": "SP" }
}
```

Sem `transportadora` e `veiculo` no JSON, o XML sai como antes (só `modFrete` e
`vol`).

**`destinatario`**

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `documento` | string | sim | CNPJ (14) ou CPF (11), só dígitos. |
| `nome` | string | sim | |
| `indicadorIe` | int | não | `1` contribuinte, `2` isento, `9` não contribuinte (padrão). |
| `inscricaoEstadual` | string | condicional | Obrigatória quando `indicadorIe=1`. |
| `email` | string | não | |
| `logradouro`,`numero`,`bairro`,`codigoMunicipio`,`nomeMunicipio`,`uf`,`cep` | string | sim | Endereço. `codigoMunicipio` = código IBGE. |
| `complemento`,`telefone` | string | não | |
| `codigoPais`,`nomePais` | string | não | Padrão `1058`/`BRASIL`. |

**`itens[]`**

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `codigo` | string | sim | cProd. |
| `descricao` | string | sim | |
| `gtin` | string | não | EAN, ou `"SEM GTIN"` (padrão). |
| `ncm` | string(8) | sim | |
| `cBenef` | string(8 ou 10) | não | Código de benefício fiscal na UF. Sem valor, o elemento `cBenef` não é gerado (comportamento anterior). Fica entre `NCM` e `CFOP` no XML. Ver **`itens[].cBenef`** abaixo. |
| `cfop` | string(4) | sim | |
| `unidadeComercial` | string | não | Padrão `UN`. |
| `quantidade` | decimal | sim | Arredondada para **3 casas decimais** (`qCom`/`qTrib`, no XML e no DANFE). |
| `valorUnitario` | decimal | sim | Arredondado para **3 casas decimais** (`vUnCom`/`vUnTrib`, no XML e no DANFE). O `vProd` calculado usa os valores já arredondados. |
| `valorBruto` | decimal | não | vProd. Se `0`, a API calcula `quantidade*valorUnitario`. |
| `valorDesconto` | decimal | não | |
| `valorFrete` | decimal | não | Frete específico deste item (`vFrete`). Somado à parcela rateada de `valorFrete` do cabeçalho. |
| `valorOutro` | decimal | não | Outras despesas acessórias específicas deste item (vOutro). Somado à parcela rateada de `outrasDespesas` do cabeçalho. |
| `origemMercadoria` | int | não | `0` nacional (padrão). |
| `cstIcms` | string | não | CST (regime normal) **ou** CSOSN (Simples). A API escolhe o grupo pelo `emitente.regimeTributario`. Aceita com ou sem formatação (`"20"`, `"20.0"`, `"020"`). Regime normal: `00` (`ICMS00`), `20` (`ICMS20`, com redução de base) e `40`/`41`/`50` (`ICMS40`). Outros CSTs são rejeitados com mensagem clara, em vez de gerar XML inválido. |
| `aliquotaIcms`,`baseCalculoIcms`,`valorIcms` | decimal | não | |
| `percentualReducaoBcIcms` | decimal | não | `pRedBC`, usado no CST `20`. Se omitido, é derivado de `baseCalculoIcms` em relação ao valor do item (`baseCalculoIcms` deve ser menor que o valor do item). |
| `cstPis`,`cstCofins` | string | não | Padrão `07` (isenta). Suportados: `04`–`09` (não tributado, `PISNT`/`COFINSNT`), `01`/`02` (por alíquota, `PISAliq`/`COFINSAliq`) e `49`, `50`–`56`, `60`–`67`, `70`–`75`, `98`, `99` (outras operações, `PISOutr`/`COFINSOutr`). `03` (por quantidade) ainda não é suportado. |
| `baseCalculoPis`,`aliquotaPis`,`valorPis` | decimal | não | `vBC`, `pPIS` (percentual) e `vPIS`. Usados nos CSTs tributados ou de outras operações; ignorados nos `04`–`09`. Os valores entram em `vPIS` do total. |
| `baseCalculoCofins`,`aliquotaCofins`,`valorCofins` | decimal | não | Mesmo comportamento do PIS, para a COFINS (`vCOFINS` do total). |
| `ibsCbs` | objeto | não | Tributação IBS/CBS da reforma tributária (grupo `IBSCBS`). Ver abaixo. |

**`itens[].cBenef`** (opcional)

Código de benefício fiscal (`cBenef`) do item, definido pela UF do emitente.

- **É obrigatório?** No layout nacional (XSD) não: o campo é opcional. A exigência
  vem da UF: algumas rejeitam a nota quando o item tem benefício fiscal, redução de
  base, isenção, diferimento ou outra situação listada na tabela de benefícios da
  UF e o `cBenef` não é informado. Outras não exigem. Confirme na SEFAZ da sua UF.
- **A API não impõe a exigência.** Quem sabe se o item precisa do código é o app.
  Se a UF exigir e o campo faltar, a SEFAZ rejeita e o motivo volta na resposta.
- **Formato:** exatamente **8 ou 10 caracteres** (sem espaços nas pontas), como no
  XSD. Para informar que o item não tem benefício, use o texto `SEM CBENEF`. Outro
  tamanho resulta em erro de validação antes de qualquer envio à SEFAZ.
- **Sem o campo no JSON**, nada muda: o XML sai sem `<cBenef>`.
- No XML, `<cBenef>` fica entre `NCM` (e `CEST`/`CNPJFab`) e `CFOP`.

```json
{
  "itens": [
    {
      "codigo": "001",
      "descricao": "PRODUTO",
      "ncm": "12345678",
      "cBenef": "SP000001",
      "cfop": "5102",
      "quantidade": 1,
      "valorUnitario": 100,
      "cstIcms": "20"
    }
  ]
}
```

**`itens[].ibsCbs`** (opcional)

O IBS/CBS é **totalmente configurável pelo app**. Se o item não trouxer `ibsCbs`,
nada muda: o XML sai sem o grupo `IBSCBS` e sem `IBSCBSTot` no total. O grupo só
é gerado para os itens que enviarem o objeto, e o `IBSCBSTot` soma apenas esses itens.

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `cst` | string(3) | sim | CST do IBS/CBS, ex.: `"000"`. |
| `classificacaoTributaria` | string(6) | sim | `cClassTrib`, ex.: `"000001"`. |
| `baseCalculo` | decimal | não | `vBC`. Se `0` ou omitido, usa `vProd − vDesc` do item. |
| `aliquotaIbsUf` | decimal | não | Percentual (`0.1` = 0,1%), até 4 casas. |
| `aliquotaIbsMunicipal` | decimal | não | Percentual. |
| `aliquotaCbs` | decimal | não | Percentual. |
| `valorIbsUf`,`valorIbsMunicipal`,`valorCbs` | decimal | não | Se omitidos, a API calcula `base × alíquota / 100`, arredondando a 2 casas. |

Valores negativos, `cst` fora de 3 dígitos ou `classificacaoTributaria` fora de
6 dígitos resultam em erro de validação antes de qualquer envio à SEFAZ.

```json
{
  "itens": [
    {
      "codigo": "001",
      "descricao": "PRODUTO",
      "ncm": "12345678",
      "cfop": "5102",
      "quantidade": 1,
      "valorUnitario": 100,
      "ibsCbs": {
        "cst": "000",
        "classificacaoTributaria": "000001",
        "aliquotaIbsUf": 0.1,
        "aliquotaIbsMunicipal": 0,
        "aliquotaCbs": 0.9
      }
    }
  ]
}
```

> **Schema XSD:** o grupo `IBSCBS` só passa na validação local se o
> `NFE_SCHEMAS_DIR` apontar para o pacote de schemas que já contém o IBS/CBS
> (NT 2025.002 em diante). Com o pacote antigo (PL_009), a validação rejeita o
> grupo. Sem `NFE_SCHEMAS_DIR`, a validação local é ignorada.

**`pagamentos[]`**

| Campo | Tipo | Observação |
|---|---|---|
| `meio` | string | tPag: `01` dinheiro, `03` cartão crédito, `17` PIX, etc. |
| `valor` | decimal | |

> **Troco (rejeição 866):** a NF-e exige `vTroco` sempre que a soma dos pagamentos
> for **maior** que o total da nota (`vNF`). A API calcula e emite `vTroco = Σpagamentos − vNF`
> automaticamente — você não precisa enviar nada. Para um **acréscimo** que deve
> compor o total da nota (e não virar troco), use `outrasDespesas`: assim o `vNF`
> sobe, o pagamento casa com o total e nenhum troco é gerado. Se `pagamentos` for
> omitido, a API assume o total à vista em dinheiro (igual ao `vNF`, já com `outrasDespesas`).

### Identificação da tentativa (`Idempotency-Key`)

`Idempotency-Key` é opcional e serve apenas para identificar a tentativa na
trilha de auditoria. A API **não** usa esse cabeçalho para deduplicar chamadas nem
retorna documentos anteriores: cada `POST` é processado como uma nova tentativa.
A aplicação que consome a API controla o número enviado e decide como tratar
repetições de rede e novas solicitações. Uma rejeição anterior não bloqueia um
novo `POST`, inclusive com o mesmo número; o resultado da nova tentativa depende
da SEFAZ. Não repita automaticamente uma emissão autorizada: essa proteção também
fica a cargo da aplicação cliente.

### Respostas

| HTTP | Significado |
|---|---|
| `200 OK` | **Autorizada.** Corpo traz `chaveAcesso`, `protocolo`, `dataAutorizacao` e, além disso, `xmlBase64` (o nfeProc autorizado) e `danfePdfBase64` (o DANFE em PDF). |
| `422 Unprocessable Entity` | Rejeitada/denegada pela SEFAZ, erro de transmissão, ou problema de certificado/XSD. Veja `status`, `codigoStatusRetorno`, `motivoRetorno` / `ultimoErro`. |
| `400 Bad Request` | Requisição malformada (sem itens, série inválida, **número não informado**, JSON inválido). |
| `404 Not Found` | Empresa não encontrada. |

> **XML e DANFE na resposta.** Quando a nota é autorizada (`200`), o corpo inclui
> dois campos Base64 prontos para salvar em arquivo, **sem chamadas extras**:
>
> | Campo | Conteúdo | Como salvar |
> |---|---|---|
> | `xmlBase64` | XML autorizado (`nfeProc`, com o protocolo) | decodifique Base64 → grave como `.xml` |
> | `danfePdfBase64` | DANFE (folha imprimível) em PDF | decodifique Base64 → grave como `.pdf` |
>
> O DANFE **não** vem da SEFAZ: ele é gerado pela API a partir do XML autorizado.
> Se por algum motivo a geração do PDF falhar, `danfePdfBase64` vem `null` (a nota
> continua autorizada) e você pode buscá-lo depois em `GET /api/nfe/{id}/danfe.pdf`.

O corpo (200 e 422) é o **documento fiscal**:

```json
{
  "id": "b3f1...-...",
  "empresaId": "a1c2...-...",
  "chaveAcesso": "35250712345678000181550010000000011000000017",
  "modelo": 55,
  "serie": 1,
  "numero": 1,
  "ambiente": 2,
  "ambienteDescricao": "Homologacao",
  "status": 100,
  "statusDescricao": "Autorizado",
  "autorizado": true,
  "protocolo": "135250000012345",
  "codigoStatusRetorno": "100",
  "motivoRetorno": "Autorizado o uso da NF-e",
  "dataAutorizacao": "2026-07-30T15:00:00Z",
  "destinatarioDocumento": "11111111111",
  "destinatarioNome": "CONSUMIDOR",
  "destinatarioUf": "SP",
  "valorTotal": 10.0,
  "valorProdutos": 10.0,
  "naturezaOperacao": "VENDA",
  "tentativasEnvio": 1,
  "ultimoErro": null,
  "criadoEm": "2026-07-30T15:00:00Z",
  "atualizadoEm": "2026-07-30T15:00:01Z",
  "eventos": [
    { "statusNovo": 20, "descricao": "documento gerado e assinado", "ocorridoEm": "..." },
    { "statusAnterior": 20, "statusNovo": 100, "descricao": "autorizado pela SEFAZ", "ocorridoEm": "..." }
  ],
  "xmlBase64": "PD94bWwgdmVyc2lvbj0iMS4wIiA...",
  "danfePdfBase64": "JVBERi0xLjQKJeLjz9M..."
}
```

> `xmlBase64` e `danfePdfBase64` só vêm preenchidos na resposta de **emissão** e
> de **reenvio** quando a nota está autorizada. Nos demais endpoints (lista,
> detalhe) eles vêm `null` — use os endpoints de download dedicados.

### Tabela de `status`

| Código | Nome | Significado |
|---|---|---|
| 20 | Assinado | XML assinado, chave definitiva (estado transitório durante a emissão). |
| 100 | Autorizado | Uso autorizado (tem protocolo e XML de distribuição). |
| 200 | Rejeitado | Corrija os dados e envie um novo `POST`; rejeições anteriores não bloqueiam novas solicitações. |
| 300 | Denegado | Irregularidade fiscal; não reenviar. |
| 900 | ErroTransmissao | Falha técnica de envio; **reenvie com `POST /api/nfe/{id}/reenviar`**. |

---

## 2) Reenviar uma nota que não autorizou

Use este endpoint quando a aplicação decidir repetir **o mesmo XML assinado**
(e a mesma tentativa), por exemplo após falha de transmissão. Para corrigir dados
da nota ou iniciar uma nova solicitação após rejeição, faça um novo `POST
/api/empresas/{empresaId}/nfe` com o payload atualizado; a API não reaproveita o
documento rejeitado e não bloqueia essa nova emissão.

Quando o objetivo for reenviar o mesmo XML:

```
POST /api/nfe/{id}/reenviar
```

Query param opcional: `contingencia=true|false`.

- É **idempotente**: se a nota já estiver autorizada, retorna `200` com o
  documento como está, sem reenviar.
- Respostas iguais às da emissão: `200` (autorizada, com `xmlBase64` e
  `danfePdfBase64`) ou `422` (ainda não autorizada, com o motivo).

> A API já tenta reenviar sozinha até 3x em falhas de rede **na própria chamada
> de emissão**. Este endpoint é para quando mesmo assim não deu — ou para
> reprocessar em lote pelo seu app depois.

---

## 3) Consultar e baixar documentos

```
GET /api/empresas/{empresaId}/nfe?limite=50     # lista (mais recentes primeiro)
GET /api/nfe/{id}                               # detalhe + trilha de eventos
GET /api/nfe/{id}/xml                            # baixa o XML autorizado (nfeProc)
GET /api/nfe/{id}/danfe.pdf?orientacao=paisagem  # gera e baixa o DANFE em PDF (orientacao opcional: retrato | paisagem)
GET /api/nfe/{id}/cancelamento.pdf               # comprovante do cancelamento (PDF)
GET /api/nfe/{id}/danfe-cancelamento.pdf         # DANFE carimbada "CANCELADA" (PDF; aceita ?orientacao= igual ao danfe.pdf)
GET /api/nfe/{id}/retorno-sefaz                  # baixa a resposta bruta da SEFAZ
```

### Reconciliação por número (quando o retorno se perdeu)

Se o seu app enviou a nota e perdeu o retorno (timeout, queda, base limpa), ele conhece
apenas o **número** — não o `id` nem a chave de acesso, e por isso não conseguia consultar
nada. Este endpoint fecha essa lacuna:

```
GET /api/empresas/{empresaId}/nfe/por-numero/{numero}?serie=1
```

Responde **sempre HTTP 200**, nunca 404, para o chamador poder distinguir "nota
inexistente" de "rota inexistente" (um 404 é ambíguo entre os dois).

| Resposta | Significado | Ação do seu app |
| --- | --- | --- |
| `existe: false`, `podeEmitir: true` | Nada registrado nesta base | Limpar a trava local e emitir |
| `existe: true`, `status: Autorizado`/`Cancelado` | Já está na SEFAZ | Guardar `id`/`chaveAcesso`; cabe à aplicação cliente evitar duplicação |
| `existe: true`, `status: Rejeitado`, `podeEmitir: true` | A SEFAZ rejeitou a tentativa; ela não bloqueia um novo pedido | Corrija os dados e faça um novo `POST` conforme a decisão da aplicação cliente |
| `existe: true`, `status: ErroTransmissao` | Resultado de rede pode ser incerto | A aplicação decide entre consultar/repetir o XML com `/api/nfe/{id}/reenviar` ou iniciar um novo pedido |

`serie` é opcional; sem ela considera qualquer série da empresa. Havendo mais de um
registro no mesmo número (rejeição seguida de reenvio), `historico` traz todos para
auditoria.

> **Cuidado:** `existe: false` afirma apenas que **esta API** não tem registro da nota.
> Se a base foi limpa, a nota pode ter sido autorizada antes da limpeza — confirme no
> portal da SEFAZ (ou via DistDFe) antes de reemitir, para não duplicar.

---

## Exemplos

### cURL — emitir em homologação

```bash
curl -X POST "https://nfe-api.x-star.com.br/api/empresas/SEU_EMPRESA_ID/nfe" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: pedido-2026-0001" \
  -d '{
    "configuracao": { "ambiente": 2, "contingencia": false },
    "emitente": {
      "cnpj": "12345678000195", "razaoSocial": "Empresa Exemplo Ltda",
      "inscricaoEstadual": "123456789", "regimeTributario": 3,
      "logradouro": "Rua Exemplo", "numero": "100", "bairro": "Centro",
      "codigoMunicipio": "3550308", "nomeMunicipio": "Sao Paulo",
      "uf": "SP", "cep": "01001000"
    },
    "serie": 1,
    "numero": 123,
    "naturezaOperacao": "VENDA DE MERCADORIA",
    "consumidorFinal": true,
    "destinatario": {
      "documento": "11111111111",
      "nome": "NF-E EMITIDA EM AMBIENTE DE HOMOLOGACAO",
      "indicadorIe": 9,
      "logradouro": "Rua Teste", "numero": "100", "bairro": "Centro",
      "codigoMunicipio": "3550308", "nomeMunicipio": "Sao Paulo",
      "uf": "SP", "cep": "01001000"
    },
    "itens": [{
      "codigo": "001", "descricao": "Produto de teste",
      "ncm": "22030000", "cfop": "5102",
      "unidadeComercial": "UN", "quantidade": 1,
      "valorUnitario": 10.00, "valorBruto": 10.00,
      "origemMercadoria": 0, "cstIcms": "102"
    }],
    "pagamentos": [{ "meio": "01", "valor": 10.00 }]
  }'
```

### Node.js

```js
import { writeFile } from "node:fs/promises";

// nota.numero é obrigatório: a numeração é controlada pelo seu app.
const res = await fetch(
  `https://nfe-api.x-star.com.br/api/empresas/${empresaId}/nfe`,
  {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": pedidoId,   // seu id de pedido
    },
    body: JSON.stringify(nota),      // { serie, numero, ... }
  }
);
const doc = await res.json();

if (res.ok && doc.autorizado) {
  console.log("Autorizada:", doc.chaveAcesso, doc.protocolo);

  // XML e DANFE já vêm na resposta (Base64) — basta decodificar e salvar.
  if (doc.xmlBase64)
    await writeFile(`${doc.chaveAcesso}.xml`, Buffer.from(doc.xmlBase64, "base64"));
  if (doc.danfePdfBase64)
    await writeFile(`${doc.chaveAcesso}.pdf`, Buffer.from(doc.danfePdfBase64, "base64"));
} else {
  console.error("Nao autorizada:", doc.status, doc.motivoRetorno ?? doc.ultimoErro);

  // Falha de transmissão (status 900): reenvie sem reemitir nem trocar o número.
  if (doc.status === 900) {
    const r2 = await fetch(`https://nfe-api.x-star.com.br/api/nfe/${doc.id}/reenviar`, { method: "POST" });
    const doc2 = await r2.json();
    console.log("Reenvio:", doc2.statusDescricao);
  }
}
```

### C# (HttpClient)

```csharp
using var http = new HttpClient { BaseAddress = new Uri("https://nfe-api.x-star.com.br") };
http.DefaultRequestHeaders.Add("Idempotency-Key", pedidoId);

var resp = await http.PostAsJsonAsync($"/api/empresas/{empresaId}/nfe", nota);
var doc = await resp.Content.ReadFromJsonAsync<DocumentoFiscalDto>();

if (resp.IsSuccessStatusCode && doc!.Autorizado)
    Console.WriteLine($"Autorizada: {doc.ChaveAcesso} / {doc.Protocolo}");
else
    Console.WriteLine($"Nao autorizada: {doc!.StatusDescricao} - {doc.MotivoRetorno ?? doc.UltimoErro}");
```

---

## Do ACBrMonitor para esta API — equivalências

| ACBrMonitor | Aqui |
|---|---|
| `NFE.CriarEnviarNFe` com INI da nota | `POST /api/empresas/{id}/nfe` com JSON |
| Controlar a numeração no seu sistema | **Igual: você envia o `numero`; a API usa exatamente ele** |
| Configurar certificado no monitor | Certificado cadastrado por empresa (uma vez) |
| Escolher ambiente no `.ini` | `configuracao.ambiente` no JSON (`1` produção / `2` homologação) |
| Ler retorno/`XMotivo` | `motivoRetorno` + `codigoStatusRetorno` no JSON |
| Buscar o XML autorizado no disco | `xmlBase64` na resposta, ou `GET /api/nfe/{id}/xml` |
| Gerar o DANFE | `danfePdfBase64` na resposta, ou `GET /api/nfe/{id}/danfe.pdf` |
| Reprocessar em caso de timeout | Auto-retry 3x + `POST /api/nfe/{id}/reenviar` |

## Boas práticas

- **A numeração é sua responsabilidade.** Controle o `numero` (nNF) por série no
  seu app, de forma sequencial. A API não gera mais número — se você repetir um
  número já autorizado, a SEFAZ rejeita por duplicidade (cStat 539).
- `Idempotency-Key` pode identificar uma tentativa na auditoria, mas a API não
  deduplica chamadas. O cliente deve controlar reintentos de rede, conferir o
  resultado antes de retransmitir e impedir duplicação após autorização.
- Trate `422` inspecionando `status`: `200` (Rejeitado) permite novo `POST` com
  payload corrigido, sem bloqueio pelo histórico; `300` (Denegado) não deve ser
  reenviado. Em `900` (ErroTransmissao), consulte o resultado antes de decidir
  entre `POST /api/nfe/{id}/reenviar` com o mesmo XML ou novo pedido.
- Salve o `xmlBase64` (guarda fiscal obrigatória) e o `danfePdfBase64` assim que
  receber o `200` — sem depender de chamadas extras.
- Teste em **homologação** (`configuracao.ambiente=2`) antes de produção. Em homologação o
  destinatário deve ter razão social "NF-E EMITIDA EM AMBIENTE DE HOMOLOGACAO...".
- Guarde o `id` retornado: é por ele que você reenvia, baixa o XML/DANFE e
  consulta a nota.

## Gestão da empresa (cadastro, ambiente e logo)

Endpoints para a própria empresa se cadastrar e se auto-gerenciar, sem depender do
painel.

| Ação | Endpoint | Corpo |
| --- | --- | --- |
| Cadastrar empresa (JSON) | `POST /api/empresas` | Campos cadastrais em JSON. **Upsert por CNPJ** (veja abaixo). |
| Onboarding (cria empresa + certificado A1) | `POST /api/empresas/onboarding` | `multipart/form-data`: campos cadastrais + `arquivo` (.pfx) + `senha` |
| Atualizar dados cadastrais | `PUT /api/empresas/{id}` | Campos cadastrais + `ativa` |
| Definir ambiente da empresa | `PUT /api/empresas/{id}/ambiente` | `{ "ambiente": 1 }` (1 produção / 2 homologação) |
| **Descadastrar (excluir) empresa** | `DELETE /api/empresas/{id}` | — |
| Enviar/trocar logo | `POST /api/empresas/{id}/logo` | `multipart/form-data`: `arquivo` (PNG/JPEG ≤ 1 MB) |
| Baixar logo | `GET /api/empresas/{id}/logo` | — |
| Remover logo | `DELETE /api/empresas/{id}/logo` | — |

### Cadastro por CNPJ é *upsert* (`POST /api/empresas` e `onboarding`)

Se o **CNPJ já existe**, a API não recusa: ela **compara e atualiza** apenas os campos
divergentes (sem apagar opcionais que vierem vazios) e responde:

```json
{ "atualizado": true, "camposAlterados": ["Razao social", "Telefone"], "empresa": { } }
```

Empresa nova responde `201` com `{ "atualizado": false, "camposAlterados": [], "empresa": { } }`.
No onboarding, além dos dados, o certificado enviado é importado/atualizado (reenviar o
**mesmo** certificado com a senha corrigida apenas o reativa, sem duplicar).

### Descadastrar (excluir) empresa — `DELETE /api/empresas/{id}`

Remove o cadastro e, **em cascata**, o logo, os certificados e as séries de numeração.

| HTTP | Significado |
| --- | --- |
| `200 OK` | Excluída. Corpo: `{ "removido": true, "cnpj": "...", "razaoSocial": "..." }`. |
| `404 Not Found` | Empresa não encontrada. |
| `409 Conflict` | **A empresa já emitiu documentos fiscais.** Não pode ser excluída (guarda fiscal obrigatória). Corpo traz `documentosFiscais` (quantidade). **Desative** a empresa (`PUT /api/empresas/{id}` com `ativa=false`) para tirá-la de operação mantendo o histórico. |

> **Excluir x Desativar.** Excluir é irreversível e só é permitido para empresas
> que **nunca** emitiram. Para uma empresa em uso, desative (`ativa=false`): ela some
> da operação, mas notas, XMLs e a trilha fiscal permanecem consultáveis.

No painel: **Empresas → coluna Ações → ícone de lixeira**, com diálogo de confirmação.

Notas importantes:

- **Onboarding é transacional**: certificado inválido (senha errada, CNPJ divergente,
  vencido) **não** cria a empresa. Uma empresa nunca fica cadastrada sem poder emitir.
- **Ambiente de emissão é informado a cada request** em `configuracao.ambiente`
  (`1` produção / `2` homologação). A API não usa `ambientePadrao` nem aceita
  `?ambiente=...` no endpoint de emissão de NF-e.
- **Logo é opcional**: o DANFE imprime o logotipo quando existe; **sem logo, imprime
  apenas o texto** do emitente. O formato é detectado pelos *magic bytes* (não confia
  no content-type enviado).
- O `EmpresaDto` expõe `temLogo: bool` para o painel decidir se mostra a
  pré-visualiza��ão.

## Cancelamento (evento 110111)

Uma NF-e **autorizada** (status 100) pode ser cancelada dentro do prazo legal
(em regra 24h após a autorização; alguns estados permitem mais). O cancelamento
é um **evento oficial** enviado ao serviço `RecepcaoEvento` da SEFAZ, assinado
com o mesmo certificado da emissão.

```
POST /api/nfe/{id}/cancelar
Content-Type: application/json

{ "justificativa": "Erro nos dados do destinatario da nota" }
```

- **Justificativa obrigatória**: 15 a 255 caracteres. Fora disso a API responde
  `400` sem sequer transmitir.
- **Sucesso (`200`)**: retorna o evento com `homologado: true`, `protocoloEvento`,
  `codigoStatus` (135 = evento vinculado / 155 = fora do prazo mas aceito) e
  `motivo`. A nota passa a status **400 (Cancelada)**.
- **Rejeição (`422`)**: retorna o evento com `homologado: false` e o
  `codigoStatus`/`motivo` da SEFAZ (ex.: 501 = prazo excedido, 573 = duplicidade
  de evento). A nota **permanece autorizada** — nada é alterado.
- **Idempotente**: se a nota já estiver cancelada, um novo `POST` devolve `200`
  com o evento homologado existente, sem transmitir de novo.

Guarda e consulta dos eventos:

| Ação | Como |
| --- | --- |
| Listar eventos fiscais da nota | `GET /api/nfe/{id}/eventos` |
| Baixar o XML do cancelamento homologado (`procEventoNFe`) | `GET /api/nfe/{id}/cancelamento.xml` |
| Comprovante do evento de cancelamento em PDF | `GET /api/nfe/{id}/cancelamento.pdf` |
| DANFE da nota carimbada com "CANCELADA" (PDF) | `GET /api/nfe/{id}/danfe-cancelamento.pdf` |

> O XML do evento é a **guarda fiscal** do cancelamento — armazene-o junto ao
> XML autorizado da nota. Os dois PDFs são **material de apoio** (comunicação
> com o cliente/arquivo interno), não substituem a guarda do XML.

Os dois PDFs só são gerados quando existe um **cancelamento homologado**
(evento 110111); caso contrário respondem `422` (nota não cancelada) ou `404`
(sem XML de origem). Ambos são servidos por **HTTP** pela API — não há WebSocket
envolvido:

- **`cancelamento.pdf`** — comprovante do evento: dados do emitente, número/série
  e chave da nota, protocolo do evento, data/hora do registro, `cStat`/motivo,
  protocolo original da NF-e e a justificativa.
- **`danfe-cancelamento.pdf`** — o DANFE completo da nota com uma marca d'água
  diagonal **CANCELADA** sobreposta.

No painel: abra a nota autorizada em **Notas emitidas → Cancelar NF-e**, informe
a justificativa e confirme. O resultado (homologado ou rejeitado) aparece na
seção **Eventos fiscais** do detalhe da nota, com links para **Baixar XML**,
**Comprovante PDF** e **DANFE cancelada**.

## Carta de correção (evento 110110)

A carta de correção (CC-e) é um **evento oficial** para sanar erros de uma NF-e
**autorizada** (status 100) que **não** alterem: valores/impostos (base de
cálculo, alíquota, preço, quantidade, valor da operação), o remetente ou o
destinatário, nem a data de emissão/saída. Para esses casos, cancele e reemita.

```
POST /api/nfe/{id}/carta-correcao
Content-Type: application/json

{ "correcao": "No campo Informações Complementares, leia-se: pagamento a prazo em 30 dias." }
```

- **Texto obrigatório**: 15 a 1000 caracteres. Fora disso a API responde `400`
  sem transmitir.
- **Sequência automática**: o backend calcula o `nSeqEvento` (1 a 20). Cada nova
  carta **substitui** as anteriores e deve conter o texto **consolidado** de
  todas as correções. Limite de **20 cartas** por nota (`422` ao exceder).
- **`xCondUso`**: o texto legal exigido pela SEFAZ é anexado automaticamente.
- **Sucesso (`200`)**: retorna o evento com `homologado: true`,
  `sequenciaEvento`, `protocoloEvento`, `codigoStatus` (135 = vinculado) e
  `motivo`. A situação da nota **não muda** (continua 100/Autorizada).
- **Rejeição (`422`)**: retorna o evento com `homologado: false` e o
  `codigoStatus`/`motivo` da SEFAZ.

Guarda e consulta:

| Ação | Como |
| --- | --- |
| Listar eventos fiscais da nota (inclui as CC-e) | `GET /api/nfe/{id}/eventos` |
| Baixar o XML da CC-e homologada mais recente (`procEventoNFe`) | `GET /api/nfe/{id}/carta-correcao.xml` |
| Baixar o XML de uma sequência específica | `GET /api/nfe/{id}/carta-correcao.xml?seq=N` |

> Cada CC-e homologada gera um XML de guarda fiscal — armazene-o junto ao XML
> autorizado da nota, como no cancelamento.

No painel: abra a nota autorizada em **Notas emitidas → Carta de correção**,
descreva a correção e confirme. As cartas aparecem na seção **Eventos fiscais**
do detalhe da nota (numeradas), cada uma com link para baixar o XML.

## Inicialização de numeração

A numeração das notas é **sua responsabilidade** (você envia `numero` na
emissão), mas o backend mantém um contador por **empresa + modelo + série +
ambiente** que serve de apoio e evita repetição. Ao **migrar de outro sistema**,
inicialize esse contador para que ele continue de onde a numeração parou.

```
GET  /api/empresas/{id}/series          # lista os contadores da empresa
PUT  /api/empresas/{id}/series          # inicializa/ajusta o próximo número
```

Corpo do `PUT`:

```json
{ "modelo": 55, "serie": 1, "ambiente": 2, "proximoNumero": 1001 }
```

- `modelo`: 55 (NF-e) ou 65 (NFC-e). `ambiente`: 1 (produção) ou 2 (homologação).
- `serie`: 0 a 999. `proximoNumero`: o número que a **próxima** nota receberá (≥ 1).
- **Proteção contra repetição (`422`)**: o `proximoNumero` deve ser **maior** que
  o maior número já emitido nessa série/ambiente — o sistema recusa um valor que
  reutilizaria uma NF-e existente (a SEFAZ rejeitaria a chave duplicada).
- Contadores de produção e homologação são **independentes**.
- Resposta (`200`) traz o contador atualizado, incluindo `ultimoNumero` e
  `proximoNumero`.

No painel: **Numeração → Inicializar numera��ão**, escolha modelo, série e
ambiente e informe o próximo número. A tabela mostra o último e o próximo número
de cada série.

> **Como o `GET /series` calcula o "último número"**: a emissão grava o `numero`
> que o seu app envia e **não** atualiza o contador. Por isso o `ultimoNumero`
> exibido é o **maior número realmente emitido** (notas autorizadas, canceladas
> ou denegadas — rejeitadas/erro não contam pois podem ser reenviadas),
> combinado com o contador inicializado manualmente quando este estiver à
> frente. O `proximoNumero` é sempre `ultimoNumero + 1`.

## Inutilização de numeração (NfeInutilizacao4)

Quando há uma **quebra de sequência** (ex.: falha no sistema pulou do 100 para o
105), a SEFAZ exige que você **inutilize** a faixa que ficou sem uso — é a
declaração oficial de que aqueles números nunca virarão nota. É **irreversível**
e a faixa **não pode** conter nenhuma NF-e já autorizada ou cancelada.

```
GET  /api/empresas/{id}/inutilizacoes    # histórico de inutilizações
POST /api/empresas/{id}/inutilizacoes    # inutiliza uma faixa (irreversível)
```

Corpo do `POST`:

```json
{
  "uf": "SP", "cnpj": "12345678000199",
  "modelo": 55, "serie": 1, "ambiente": 2, "ano": 2026,
  "numeroInicial": 101, "numeroFinal": 104,
  "justificativa": "Quebra de sequencia por falha no sistema emissor"
}
```

- `uf`, `cnpj` e `ambiente` são **obrigatórios no JSON**: a API não lê o cadastro da
  empresa (guarda só certificado e logo). `ufAutorizadorOverride` é opcional.
- `ano` é **opcional** (usa o ano corrente quando ausente). `justificativa`:
  15 a 255 caracteres.
- **Recusa (`422`)** faixas que contenham NF-e já autorizada/cancelada, ou dados
  inválidos (`numeroFinal` < `numeroInicial`, etc.), sem transmitir.
- **Sucesso (`200`)**: `homologado: true`, com `protocolo`, `codigoStatus`
  (102 = inutilização homologada) e `dataRegistro`.
- **Rejeição da SEFAZ (`422`)**: `homologado: false` com `codigoStatus`/`motivo`.
- É **idempotente** por faixa: repetir a mesma faixa devolve o registro existente.
- Todo resultado é **persistido com o XML bruto** (guarda fiscal); quando
  homologado, o `procInutNFe` é montado para arquivo.

No painel: **Numeração → Inutilizar faixa**, informe modelo, série, ambiente,
faixa e justificativa. O histórico aparece logo abaixo, com situação e protocolo.

## Consulta de protocolo e status do serviço

Estas rotas também não leem o cadastro da empresa; só o certificado é usado.
Tudo vem no JSON (não existe mais `?ambiente=`):

```
POST /api/empresas/{id}/nfe/consultar
{ "chaveAcesso": "<44 dígitos>", "ambiente": 2, "ufAutorizadorOverride": null }

POST /api/status/empresa/{id}/consultar
{ "uf": "SP", "ambiente": 2, "ufAutorizadorOverride": null, "contingencia": false }
```

- `ambiente` na consulta de protocolo é opcional **só se a nota já estiver
  cadastrada nesta API** (usa o ambiente do registro). A chave de acesso não
  carrega `tpAmb`, então para uma nota desconhecida informe `1` ou `2`.
  No status é sempre obrigatório (`1` produção / `2` homologação).
- `ufAutorizadorOverride` e `contingencia` são opcionais nas duas rotas.

### Consulta por chave: sincroniza a base e devolve XML e DANFE

`POST /api/empresas/{id}/nfe/consultar` não só consulta a SEFAZ: quando a nota
está **autorizada ou cancelada** e é do CNPJ da empresa, a API também:

1. **Cadastra** a NF-e como emitida, se ainda não existir (modelo, série,
   número, ambiente e chave vêm da própria chave; protocolo e data vêm da SEFAZ).
2. **Atualiza** a nota existente para `Autorizado` (ou `Cancelado`), com
   protocolo, `cStat` e motivo, e registra um evento de histórico.
3. **Monta o XML autorizado** (`nfeProc` = XML assinado + `protNFe`) e grava em
   `XmlProtocolado`, se ainda não estava gravado.
4. **Devolve** no JSON da resposta:

| Campo | Conteúdo |
|---|---|
| `documento` | registro da nota na base (status, protocolo, valores) |
| `xmlBase64` | XML autorizado (`nfeProc`) em Base64 UTF-8 |
| `danfePdfBase64` | DANFE em PDF, Base64 (só nota autorizada) |
| `cadastradaAgora` | `true` se esta chamada criou o registro |
| `aviso` | texto quando algo não pôde ser completado |

O app grava `xmlBase64` e `danfePdfBase64` no lugar certo (anexo da nota) e marca
a nota como emitida. Use também ao **imprimir o DANFE**: chame a consulta e
exiba o PDF devolvido.

Limites:
- **Nota de outro emitente** (chave com outro CNPJ): só consulta, não cadastra.
- **Nota que a API nunca viu**: a consulta da SEFAZ devolve só o protocolo, não o
  XML. O registro é criado com protocolo e status, mas **sem XML/DANFE**
  (`aviso` explica). A distribuição DF-e **não resolve**: ela não entrega ao
  emitente a própria nota. O XML precisa vir do sistema que emitiu a nota e ser
  anexado pelo endpoint abaixo.
- **Cancelada**: devolve o XML, mas não gera DANFE; o cancelamento nunca é
  revertido por uma nova consulta.
- Consulte uma vez por chave e guarde o resultado. Consultas repetidas em
  sequência geram `656 Consumo Indevido`.

### Anexar o XML a uma nota sem XML

```
POST /api/empresas/{empresaId}/nfe/xml
```

Corpo:

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `chaveAcesso` | string | sim | 44 dígitos |
| `xml` | string | um dos dois | `NFe` assinada ou `nfeProc` |
| `xmlBase64` | string | um dos dois | o mesmo XML em Base64 (UTF-8) |
| `ambiente` | int | não | `1` produção, `2` homologação; por padrão vem do `tpAmb` do XML |

A API valida: XML bem formado, `infNFe/@Id` igual à chave, `Signature` presente,
chave do mesmo CNPJ da empresa e, se for `nfeProc`, `protNFe` com `chNFe` da chave e
`cStat` 100 ou 150. Depois confirma na SEFAZ que a nota está autorizada (uma consulta,
dispensada se a base já tem o mesmo protocolo). Monta o `nfeProc`, grava em
`XmlProtocolado` e, se a nota foi cadastrada pela consulta, completa natureza, datas,
destinatário e totais a partir do XML. Se a nota já tem XML, nada é alterado.

Resposta `200`: `documento`, `cadastradaAgora`, `aviso`, `xmlBase64` (nfeProc) e
`danfePdfBase64`.

| Status | Quando |
|---|---|
| `400` | chave ou XML inválido, assinatura ausente, chave de outro emitente, protocolo incompatível |
| `404` | empresa não encontrada |
| `422` | SEFAZ não confirma a nota como autorizada, ou falha de certificado ou comunicação |

No painel, a nota autorizada sem XML mostra **Anexar XML para o DANFE** no lugar de
**Imprimir DANFE**.

### Pasta de XMLs (busca automática em disco)

Se os XMLs das notas já existem em disco, configure a pasta por empresa e a API os
localiza sozinha pela chave, sem upload manual. A pasta precisa existir **na máquina
onde a API roda** e ter permissão de leitura. Estrutura esperada:

```
{pasta}/AAAAMM/NFe/*.xml       notas (única subpasta lida)
{pasta}/AAAAMM/Evento/*.xml    eventos (ignorada)
```

Exemplo: `/mnt/hdbackup/API/levpao/arquivos/nfe/EDSMonitorPLUS/Arqs/03707154000106/NFe`
(pode ser a pasta que contém `202610`, `202611`… ou a do CNPJ que contém `NFe`).
A busca começa pelo mês da chave e, se não achar, tenta os demais meses.

```
PUT /api/empresas/{id}/pasta-xml               { "pasta": "/caminho/absoluto" }   (vazio remove)
GET /api/empresas/{id}/pasta-xml/diagnostico   meses encontrados e XMLs do mês mais recente
POST /api/empresas/{empresaId}/nfe/xml/buscar-pasta   { "chaveAcesso": "44 dígitos" }
```

O XML achado passa pelas mesmas validações do anexo manual (chave, assinatura, emitente,
protocolo e confirmação na SEFAZ). A consulta por chave também tenta a pasta sozinha
quando a nota está autorizada e sem XML; se não achar, a nota segue sem XML e o
painel continua oferecendo o anexo manual. No painel: **Empresas > Pasta de XMLs**
(com **Salvar e testar**) e, no anexo, o botão **Buscar na pasta**.
- **Cancelada**: devolve o XML, mas não gera DANFE; o cancelamento nunca é
  revertido por uma nova consulta.
- Consulte uma vez por chave e guarde o resultado. Consultas repetidas em
  sequência geram `656 Consumo Indevido`.

## Validação XSD (opcional)

Se quiser a checagem local contra o schema oficial **antes** de transmitir,
defina `NFE_SCHEMAS_DIR` apontando para a pasta com os XSD da NF-e 4.00
(ex.: `NFE_SCHEMAS_DIR=/home/sanches/v0/aprovador/backend/schemas/NFe`).
Sem essa variável, a emissão funciona normalmente, apenas sem a validação local.

## Segurança do painel (login de admin)

O painel web exige **login de administrador** (usuário e senha). O acesso é
controlado por sessão assinada num cookie **HttpOnly** (o navegador não
consegue ler por JavaScript), e toda navegação passa por um middleware que
redireciona para `/login` quando não há sessão válida.

Como o navegador fala com a API através de um **proxy no servidor**
(`/api/proxy/...`), o `NFE_API_TOKEN` �� injetado apenas do lado do servidor — o
token **nunca** chega ao navegador. O usuário só recebe o cookie de sessão.

### Variáveis de ambiente

| Variável | Onde | Para que serve |
| --- | --- | --- |
| `NFE_API_TOKEN` | API **e** painel | Token Bearer exigido pela API. No painel, é injetado pelo proxy. Use o **mesmo valor** nos dois. |
| `NFE_CERT_MASTER_KEY` | API | Chave mestra (base64, 32 bytes) que cifra/decifra os certificados A1. **Nunca troque** com certificados já salvos. |
| `PAINEL_ADMIN_USUARIO` | Painel | Usuário do login do painel. |
| `PAINEL_ADMIN_SENHA` | Painel | Senha do login do painel. |
| `PAINEL_SESSION_SECRET` | Painel | Segredo forte para assinar o cookie de sessão. Trocá-lo desloga todos. |
| `NFE_API_URL` | Painel | URL da API (padrão `http://127.0.0.1:5240` no preview). |

> **Use sempre os nomes SEM sufixo.** Cópias como `NFE_API_TOKEN_2`,
> `NFE_API_URL_2`, `PAINEL_ADMIN_SENHA_2` etc. são **ignoradas** pelo código e só
> causam confusão. Apague-as e mantenha uma variável de cada.

Gere segredos fortes, por exemplo:

```bash
openssl rand -base64 48   # para NFE_API_TOKEN
openssl rand -base64 48   # para PAINEL_SESSION_SECRET
```

> **Importante:** `NFE_API_TOKEN` precisa ser **idêntico** na API e no painel —
> é o mesmo token nas duas pontas. Guarde `PAINEL_ADMIN_SENHA` e os segredos
> fora do versionamento (variáveis de ambiente / cofre), nunca no código.

#### Onde definir (um arquivo por modo de execução)

- **Rodando no terminal** (`dotnet run`): tudo num **único `.env.local`** na
  raiz do projeto (ou em `backend/.env.local` se você roda de dentro de
  `backend/`). A API imprime no startup de qual arquivo leu:
  `[env] Variaveis carregadas de: .../.env.local`.
- **Rodando via systemd**: use o `EnvironmentFile` do serviço apontando para o
  **mesmo** `.env.local` (evita duplicar variáveis em `/etc`).
- **Painel (Next.js) no servidor**: no `EnvironmentFile` do servico do painel
  (`/etc/aprovador-painel.env`, ver `deploy/painel/`).

Nunca preencha o mesmo valor em dois lugares ao mesmo tempo — escolha um modo.
No startup, a API também imprime a impressão digital da chave mestra ativa
(`[cofre] ... Fingerprint: xxxxxxxx`) para você confirmar qual chave está em uso.
