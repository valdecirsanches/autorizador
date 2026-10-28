# Como enviar uma NF-e — comece aqui

Se voce vem do **ACBrMonitor**, a diferenca principal e esta: aqui voce **nao monta
XML nem assina nada**. Voce manda um **JSON** com os dados da nota, e a API cuida de
gerar o XML 4.00, assinar com o certificado da empresa, validar e transmitir para a
SEFAZ. A resposta ja vem com a nota autorizada (ou o motivo da rejeicao).

O fluxo tem apenas **2 passos**:

```
1. Descobrir o ID da empresa (uma vez, a partir do CNPJ)
2. Enviar a NF-e usando esse ID
```

---

## Antes de comecar (pre-requisitos)

1. A **API esta no ar** (local em `http://127.0.0.1:5240`, ou publica em
   `https://nfe-api.x-star.com.br`).
2. A **empresa foi cadastrada** no painel (aba Empresas).
3. A empresa **tem um certificado A1 ativo** (aba Certificados). Sem certificado a
   emissao falha, porque nao da para assinar a nota.
4. Voce tem a **chave do seu app** (`nfeapp_...`), criada pelo administrador em
   *Apps integrados* no painel. Envie-a em toda requisicao:
   `Authorization: Bearer nfeapp_...`. Ela so enxerga as empresas liberadas para o
   seu app. Detalhes em [CHAVES_POR_APP.md](CHAVES_POR_APP.md).

> Dica: teste a saude da API com `curl http://127.0.0.1:5240/health`.
> Deve responder `{"status":"ok",...}`.

---

## Passo 0 (opcional) — Cadastro self-service da empresa

Se a empresa ainda **nao existe**, da para cadastra-la e **enviar o certificado A1
(.pfx + senha) numa unica chamada** — sem precisar do painel. E uma requisicao
`multipart/form-data`:

```
POST /api/empresas/onboarding
```

```bash
curl -X POST http://127.0.0.1:5240/api/empresas/onboarding \
  -F "cnpj=67570036000181" \
  -F "razaoSocial=SUA EMPRESA LTDA" \
  -F "uf=SP" \
  -F "regimeTributario=3" \
  -F "ambientePadrao=2" \
  -F "senha=senha-do-pfx" \
  -F "arquivo=@/caminho/para/certificado.pfx"
```

Esta operacao e **transacional**: se o certificado for invalido (senha errada, CNPJ
divergente ou vencido), a empresa **nao** e criada. Assim uma empresa nunca fica
cadastrada sem poder emitir. Sucesso devolve `201` com o `id` da empresa ja com o
certificado ativo — pule direto para o Passo 2.

> Prefere separar? Os endpoints antigos continuam valendo: `POST /api/empresas` cria a
> empresa e `POST /api/certificados/empresa/{id}` envia o .pfx depois.

---

## Passo 1 — Descobrir o ID da empresa pelo CNPJ

O seu app provavelmente conhece o **CNPJ**, mas a API identifica a empresa por um
**ID** (um GUID). Faca isto **uma vez** e guarde o ID no seu sistema.

> Envie o CNPJ com **apenas os 14 digitos** (sem pontos, barra ou hifen).

```bash
curl http://127.0.0.1:5240/api/empresas/por-cnpj/67570036000181
```

Resposta:

```json
{
  "id": "a19c9920-6951-4008-bbbd-acd805838094",
  "cnpj": "67570036000181",
  "razaoSocial": "SUA EMPRESA LTDA",
  "ambientePadrao": 2,
  "temCertificadoAtivo": true
}
```

O que importa aqui:

- **`id`** -> e isto que voce usa no passo 2.
- **`temCertificadoAtivo`** -> precisa ser `true`. Se vier `false`, cadastre o A1 no painel.

Erros possiveis:

| HTTP | Significado | O que fazer |
|------|-------------|-------------|
| 200  | Empresa encontrada | Copie o `id`. |
| 400  | CNPJ nao tem 14 digitos | Envie apenas numeros. |
| 404  | Nao existe empresa com esse CNPJ | Cadastre a empresa no painel primeiro. |

---

## Passo 2 — Enviar a NF-e

Agora e so mandar o JSON da nota para o `id` da empresa. **O JSON carrega tudo**:
ambiente (`configuracao`) e cadastro do emitente (`emitente`). A API guarda e usa
apenas o **certificado** da empresa.

```
POST /api/empresas/{empresaId}/nfe
```

- `configuracao.ambiente = 2` -> **Homologacao** (teste; use sempre aqui primeiro).
- `configuracao.ambiente = 1` -> **Producao** (nota com valor fiscal real).
- `?ambiente=` na URL **nao existe mais** e o `ambientePadrao` da empresa **nao** e
  usado na emissao: sem `configuracao.ambiente` a API responde `400`.
- O `emitente.cnpj` precisa ser o mesmo do certificado A1 associado ao `empresaId`.

A aplicação cliente controla a numeração e as retentativas. `Idempotency-Key` é
opcional e fica apenas na auditoria; a API processa cada `POST` como uma nova
solicitação, mesmo quando já existe uma tentativa rejeitada para o mesmo número.
A proteção contra repetição de rede e contra duplicação de notas autorizadas deve
ser implementada pelo app que chama a API.

```bash
curl -X POST "http://127.0.0.1:5240/api/empresas/a19c9920-6951-4008-bbbd-acd805838094/nfe" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: pedido-2026-000123" \
  -d '{
    "configuracao": { "ambiente": 2, "contingencia": false },
    "emitente": {
      "cnpj": "67570036000181",
      "razaoSocial": "SUA EMPRESA LTDA",
      "inscricaoEstadual": "123456789",
      "regimeTributario": 3,
      "logradouro": "Rua Exemplo",
      "numero": "100",
      "bairro": "Centro",
      "codigoMunicipio": "3550308",
      "nomeMunicipio": "Sao Paulo",
      "uf": "SP",
      "cep": "01001000"
    },
    "serie": 1,
    "numero": 273,
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
  }'
```

### Como ler a resposta

| HTTP | Significado | Campos importantes |
|------|-------------|--------------------|
| **200** | **Autorizada** | `chaveAcesso`, `protocolo`, `status: 3 (Autorizado)` |
| **422** | **Rejeitada pela SEFAZ** | `codigoStatusRetorno`, `motivoRetorno` (diz o porque) |
| 404 | Empresa nao encontrada | confira o `empresaId` |
| 400 | JSON invalido | confira `configuracao.ambiente`, `emitente` e os demais campos obrigatorios |

Exemplo de sucesso (200):

```json
{
  "id": "7b1e...c9",
  "chaveAcesso": "35260767570036000181550010000000011000000017",
  "status": 3,
  "statusDescricao": "Autorizado",
  "protocolo": "135260000123456",
  "dataAutorizacao": "2026-07-30T18:22:10Z"
}
```

Uma rejeicao (422) **nao e um bug** — e a SEFAZ recusando a nota por regra fiscal. O
campo `motivoRetorno` explica (ex.: "Rejeicao: CNPJ do emitente invalido"). Corrija os
dados e faça um novo `POST`; a API não reaproveita nem bloqueia a tentativa rejeitada.
O app cliente decide qual número enviar e como evitar duplicação em retentativas de rede.

### Campos opcionais: transportadora, veículo, cBenef e IBS/CBS

Todos são **opcionais**. Sem eles no JSON, a nota sai exatamente como no exemplo
acima. Os nomes abaixo são os que a API lê; qualquer outro nome é ignorado sem aviso.

| Onde no JSON | Campo | O que gera no XML |
|---|---|---|
| raiz | `transportadora` (`cnpj` ou `cpf`, `nome`, `inscricaoEstadual`, `endereco`, `municipio`, `uf`) | `<transporta>` (aparece também no DANFE) |
| raiz | `veiculo` (`placa`, `uf`, `rntc`) | `<veicTransp>` |
| cada item | `cBenef` (8 ou 10 caracteres, ou `SEM CBENEF`) | `<cBenef>` entre `NCM` e `CFOP` |
| cada item | `ibsCbs` (`cst`, `classificacaoTributaria`, `baseCalculo`, `aliquotaIbsUf`, `aliquotaIbsMunicipal`, `aliquotaCbs`, `valorIbsUf`, `valorIbsMunicipal`, `valorCbs`) | `<IBSCBS>` no item e `<IBSCBSTot>` no total |

Regra do `cBenef`: é opcional no layout nacional, mas algumas UFs exigem quando o
item tem benefício fiscal, redução de base, isenção ou diferimento. A API não impõe
a exigência (quem decide é o app); se a UF exigir e faltar, a SEFAZ rejeita.

Regras do IBS/CBS: `cst` tem 3 dígitos e `classificacaoTributaria` (cClassTrib) tem 6.
As alíquotas são percentuais (`0.9` = 0,9%). Se os valores (`valorIbsUf`,
`valorIbsMunicipal`, `valorCbs`) não forem enviados, a API calcula
`base × alíquota / 100`. Se `baseCalculo` for `0` ou omitida, usa `vProd − vDesc`.
O `IBSCBSTot` soma só os itens que enviaram `ibsCbs`.

`quantidade` e `valorUnitario` são arredondados para **3 casas decimais** no XML,
nos totais e no DANFE.

Exemplo completo (apenas os trechos novos):

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
  "veiculo": { "placa": "ABC1D23", "uf": "SP" },
  "itens": [
    {
      "codigo": "PROD-001",
      "descricao": "PRODUTO DE TESTE",
      "ncm": "22030000",
      "cfop": "5102",
      "quantidade": 1.5,
      "valorUnitario": 10.0,
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

> **Validação local do IBS/CBS:** o grupo só passa no XSD local se `NFE_SCHEMAS_DIR`
> apontar para o pacote de schemas com IBS/CBS (NT 2025.002 em diante). Detalhes e
> tabela completa dos campos em [`EMISSAO_NFE.md`](./EMISSAO_NFE.md).

---

## Passo 3 (opcional) — Consultar depois

```bash
# Detalhe + trilha de eventos (o que aconteceu com a nota)
curl http://127.0.0.1:5240/api/nfe/{documentoId}

# Baixar o XML autorizado (nfeProc), so existe se status = Autorizado
curl -o nota.xml http://127.0.0.1:5240/api/nfe/{documentoId}/xml

# Listar todas as notas de uma empresa
curl http://127.0.0.1:5240/api/empresas/{empresaId}/nfe
```

---

## "Enviei a nota e o retorno nao chegou" — como destravar

Se o seu app enviou a NF-e e perdeu o retorno (timeout, queda, base limpa), ele
sabe apenas o **numero** — nao tem o `documentoId` nem a chave de acesso. Consulte
por numero para descobrir o que realmente aconteceu:

```bash
# serie e opcional (?serie=1)
curl "http://127.0.0.1:5240/api/empresas/{empresaId}/nfe/por-numero/273"
```

Este endpoint responde **sempre HTTP 200** (nunca 404), justamente para o seu app
diferenciar "a nota nao existe" de "a rota nao existe".

Nota nunca registrada — pode emitir:

```json
{
  "existe": false,
  "numero": 273,
  "podeEmitir": true,
  "mensagem": "Nenhuma NF-e com este numero esta registrada para esta empresa. ..."
}
```

Nota registrada — confira o status; uma rejeição não bloqueia um novo POST:

```json
{
  "existe": true,
  "numero": 273,
  "podeEmitir": false,
  "status": "Autorizado",
  "documento": { "id": "...", "chaveAcesso": "...", "status": 100 }
}
```

Como o seu app deve reagir:

| Resposta | O que fazer |
| --- | --- |
| `existe: false` | A nota **nao** foi enviada por esta API. Limpe a trava local e emita. |
| `status: Autorizado` ou `Cancelado` | Ja esta na SEFAZ. Guarde o `id`/`chaveAcesso` e baixe o XML. **Nao** reemita. |
| `status: Rejeitado` | Corrija os dados e faça um novo `POST`; a tentativa rejeitada não bloqueia o número. |
| `status: ErroTransmissao` | O resultado pode ser incerto: a aplicação decide entre consultar/repetir o mesmo XML ou iniciar novo pedido. |

> Atencao: se a base foi limpa, `existe: false` significa apenas que **esta API** nao
> tem registro da nota — ela pode ter sido autorizada antes da limpeza. Nesse caso
> confirme no portal da SEFAZ (ou via DistDFe) antes de reemitir, para nao gerar
> nota duplicada.

---

## Ambiente (homologacao x producao)

Na **emissao de NF-e** o ambiente vem sempre do JSON, em `configuracao.ambiente`
(`1` = producao, `2` = homologacao). A decisao e do seu app a cada nota; a API nao
consulta o cadastro da empresa nem aceita `?ambiente=` no `POST .../nfe`.

O campo `ambientePadrao` da empresa continua existindo, mas **so** serve para as
rotinas da propria API que nao recebem JSON de nota (monitor de status do servico e
consulta de protocolo). Para altera-lo:

```
PUT /api/empresas/{empresaId}/ambiente
Content-Type: application/json

{ "ambiente": 1 }   // 1 = producao, 2 = homologacao
```

No painel isso aparece no seletor **Ambiente** do topo e grava o `ambientePadrao` da
empresa selecionada — **nao** muda o ambiente das notas emitidas pelo seu app.

---

## Logo do emitente no DANFE

O DANFE imprime o logotipo do emitente quando a empresa tem um cadastrado. **Sem logo,
imprime apenas o texto** (razao social e dados) — nada quebra.

```bash
# Enviar/atualizar (PNG ou JPEG, ate 1 MB)
curl -X POST http://127.0.0.1:5240/api/empresas/{empresaId}/logo \
  -F "arquivo=@/caminho/para/logo.png"

# Baixar o logo atual (pre-visualizacao)
curl -o logo.png http://127.0.0.1:5240/api/empresas/{empresaId}/logo

# Remover (volta a imprimir so o texto)
curl -X DELETE http://127.0.0.1:5240/api/empresas/{empresaId}/logo
```

No painel, use a coluna **Logo** na aba Empresas para enviar, trocar ou remover.

---

## Onde ficam os XMLs (uma pasta por empresa)

Alem de guardar cada XML no banco, a API **espelha todo XML gerado em disco**,
numa pasta separada por empresa (identificada pelo CNPJ). Assim voce tem um backup
em arquivo pronto para entregar ao contador, sem depender do banco.

Estrutura das pastas:

```
xmls/
  30511823000142/            <- uma pasta por empresa (CNPJ, so digitos)
    producao/
      <chave>-nfe.xml        XML assinado (o que foi enviado a SEFAZ)
      <chave>-procNFe.xml    XML autorizado (nfeProc, valor legal)
      <chave>-ret.xml        Retorno bruto da SEFAZ (autorizacao/rejeicao/erro)
    homologacao/
      <chave>-nfe.xml
```

- O XML **assinado** e salvo assim que a nota e gerada — antes mesmo da transmissao,
  entao mesmo uma falha de rede deixa registrado o que foi enviado.
- O XML **autorizado** (nfeProc) e salvo quando a SEFAZ autoriza. Um reenvio que
  finalmente autoriza sobrescreve o arquivo com a versao definitiva.
- O **retorno da SEFAZ** (`-ret.xml`) e salvo em **TODOS os desfechos** — inclusive
  quando a nota e **rejeitada ou da erro de transmissao**. E o XML que permite avaliar
  o que a SEFAZ respondeu mesmo sem autorizacao. Se o erro ocorrer antes de existir
  chave de acesso, o arquivo usa o `id` do documento no lugar da chave.
- A pasta base padrao e `xmls/` na raiz de execucao da API. Para gravar em outro
  lugar (ex.: um volume/HD de backup), defina a variavel de ambiente **`NFE_XML_DIR`**:

  ```bash
  NFE_XML_DIR=/mnt/backup/nfe-xml
  ```

- Falha ao gravar em disco **nunca** derruba a emissao (a nota ja pode estar
  autorizada na SEFAZ); o problema so e registrado no log com o prefixo `[XML]`.

---

## Jeito mais facil de testar

### Opcao A — script pronto (curl)

```bash
cd docs/testes
chmod +x testar-nfe.sh
CNPJ=67570036000181 ./testar-nfe.sh
```

O script faz health check, resolve o CNPJ, emite, consulta e tenta baixar o XML.
Cada execução gera uma nova tentativa; em produção, defina um número livre no app cliente. Para apontar para produção:

```bash
BASE_URL=https://nfe-api.x-star.com.br CNPJ=67570036000181 AMBIENTE=1 ./testar-nfe.sh
```

### Opcao B — Insomnia

1. Abra o Insomnia > **Create > Import from File**.
2. Escolha `docs/testes/insomnia-nfe.json`.
3. No ambiente **Base**, ajuste `base_url` e `cnpj`.
4. Rode **1. Descoberta > Resolver empresa por CNPJ**, copie o `id` para a variavel
   `empresa_id`.
5. Rode **2. Emissao > Emitir NF-e**.

---

## Tabela de-para: ACBrMonitor -> esta API

| No ACBrMonitor voce... | Aqui voce... |
|------------------------|--------------|
| Monta o arquivo INI/XML da nota | Monta um **JSON** com os mesmos dados |
| Chama `NFe.CriarEnviarNFe` | Faz `POST /api/empresas/{id}/nfe` |
| A DLL assina e transmite | A **API** assina e transmite |
| Le o retorno no arquivo/resposta | Le o **JSON** da resposta (200/422) |
| Aponta o certificado no .ini | O certificado ja esta cadastrado na empresa |
| Configura emitente (CNPJ, IE, endereco, regime) no .ini | Envia `emitente` no JSON de cada nota |
| Escolhe `tpAmb` no config | Envia `configuracao.ambiente` (`1` ou `2`) no JSON |

---

## Referencia completa

Todos os campos do JSON (destinatario, itens, transportadora, veiculo, IBS/CBS,
pagamentos, defaults e regras) estao em
[`EMISSAO_NFE.md`](./EMISSAO_NFE.md).
