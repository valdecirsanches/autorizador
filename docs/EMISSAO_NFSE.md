# Emissão de NFS-e via API (Padrão Nacional, ABRASF e Osasco RTC)

Guia para o seu app emitir **Nota Fiscal de Serviço eletrônica** por esta API.
O módulo NFS-e é **separado** do módulo NF-e: tabelas, serviços, endpoints e
telas próprias. Nada do fluxo de NF-e foi alterado — os dois convivem na mesma
API, com o **mesmo token** e o **mesmo certificado A1** da empresa.

## Como funciona (visão geral)

```
Seu app  --POST JSON-->  API  -->  reserva o número da DPS (série da empresa)
                                -->  gera o XML da DPS (layout nacional 1.01)
                                -->  assina com o A1 da empresa (XML-DSig)
                                -->  gzip + base64 e envia ao SEFIN Nacional (REST, mTLS)
                                     (ou SOAP ABRASF 2.04, se a empresa usar provedor municipal)
                                -->  recebe a NFS-e autorizada (chave de 50 dígitos)
                                -->  grava XML, protocolo e trilha de tentativas
         <--JSON + XML + DANFSe-->  API
```

Você **não** monta XML, não assina nem informa o endpoint no POST de emissão. Tudo o que define a emissão (padrão Nacional, ABRASF ou Osasco, ambiente, série, regime, município e credenciais do provedor) vem do objeto `configuracao` do próprio JSON; o servidor não guarda nem completa nenhuma configuração.

### Diferenças em relação à NF-e

| | NF-e | NFS-e |
|---|---|---|
| Numeração | Você envia o `numero` | **A API reserva** o número da DPS (série configurada) |
| Chave | 44 dígitos | **50 dígitos** |
| Transporte | SOAP/SEFAZ | REST JSON (gzip+base64) no SEFIN Nacional, SOAP ABRASF ou SOAP municipal RTC (Osasco) |
| Ambiente de teste | Homologação | "Produção restrita" (`ambiente=2`) |
| Documento auxiliar | DANFE | DANFSe (PDF) |
| Cancelamento | Evento 110111 | Evento **e101101** (com código de motivo) |

## O que o app envia e o que a API guarda

A API cuida **somente** de: certificado A1 e identidade do prestador (CNPJ, razão social e endereço da empresa), assinatura, comunicação com o provedor, logs/tentativas e armazenamento do XML/PDF das notas. **Todo o resto vem do JSON, em toda chamada.** Não existe configuração de NFS-e salva e nada recebe valor padrão: campo ausente gera 422 listando o que falta.

| Dado | Origem |
|---|---|
| Certificado A1, CNPJ e identidade do prestador | Servidor (cadastro da empresa) |
| Logs, tentativas, XML, PDF, eventos | Servidor |
| Padrão, ambiente, série, regime, município, inscrição municipal | JSON (`configuracao`) |
| Credenciais do provedor (chave de Osasco, URL/usuário/senha ABRASF) | JSON (`configuracao`) |
| Tomador, serviço, valores, número da DPS | JSON |

Consulta e cancelamento também recebem as credenciais no corpo (veja as seções próprias). As credenciais são mascaradas (`***`) nos logs e no JSON guardado nas tentativas e **não são gravadas**.

Os endpoints `GET/PUT /api/empresas/{empresaId}/nfse/numeracao` foram removidos: o `numeroDps` é sempre enviado no JSON e a API apenas valida duplicidade.

### JSON de emissão com sucesso (Osasco — modelo completo)

Todos os campos abaixo marcados como obrigatórios devem estar presentes. Substitua os valores de exemplo pelos reais; não registre a chave nos logs do seu app.

```http
POST /api/empresas/{empresaId}/nfse
Authorization: Bearer <NFE_API_TOKEN>
Idempotency-Key: pedido-12345
Content-Type: application/json
```

```json
{
  "numeroDps": 2111,
  "dataEmissao": "2026-10-03T10:15:00-03:00",
  "dataCompetencia": "2026-10-03",
  "tomador": {
    "documento": "12345678000199",
    "nome": "Cliente Exemplo LTDA",
    "email": "financeiro@cliente.com.br",
    "telefone": "11999990000",
    "logradouro": "Rua Exemplo",
    "numero": "100",
    "complemento": "Sala 2",
    "bairro": "Centro",
    "codigoMunicipio": "3534401",
    "cidade": "Osasco",
    "uf": "SP",
    "cep": "06010000"
  },
  "servico": {
    "codigoMunicipioPrestacao": "3534401",
    "codigoTributacaoNacional": "010701",
    "codigoTributacaoMunicipal": "<ATIVIDADE_MUNICIPAL>",
    "itemListaServico": "01.07",
    "cnae": "6201501",
    "descricao": "Desenvolvimento e manutenção de sistemas — outubro/2026",
    "enderecoPrestacaoServico": "Rua Exemplo",
    "numeroPrestacaoServico": "100",
    "bairroPrestacaoServico": "Centro",
    "cidadePrestacaoServico": "Osasco",
    "estadoPrestacaoServico": "SP",
    "cepPrestacaoServico": "06010000",
    "codigoCidadeIBGEPrestacaoServico": "3534401"
  },
  "valores": {
    "valorServico": 1500.00,
    "tributacaoIssqn": 1,
    "retencaoIssqn": 1,
    "aliquotaIss": 2.00,
    "valorIss": 30.00
  },
  "informacoesComplementares": "Pedido 12345",
  "configuracao": {
    "padrao": 3,
    "ambiente": 2,
    "serie": "1",
    "opSimpNac": 3,
    "regApTribSN": 1,
    "regEspTrib": 0,
    "codigoMunicipioEmissao": "3534401",
    "inscricaoMunicipal": "123456",
    "versaoAplicativo": "MeuApp 2.0",
    "osascoChaveAutenticacao": "<CHAVE_DO_PORTAL_DE_OSASCO>"
  }
}
```

Para ABRASF, troque `padrao` por `2` e envie `abrasfUrl` (obrigatória), `abrasfVersao`, `abrasfUsuario` e `abrasfSenha` no lugar da chave de Osasco. Para o padrão Nacional (`padrao = 1`), nenhuma credencial é necessária. Os blocos de endereço de prestação e de tomador (cidade/UF) são exigidos por Osasco; a tabela de regras está em [Corpo](#corpo-nfserequest).

Sucesso = HTTP 200 com `status: 3` (`Autorizada`), `chaveAcesso`, `numeroNfse` e `xmlBase64` na resposta.

### Campos RTC usados em Osasco

Além dos campos comuns de `servico`, o DTO aceita estes dados RTC. Informe os códigos fornecidos pelo município/regra fiscal aplicável; não use os exemplos como valores fiscais universais.

```json
"servico": {
  "codigoMunicipioPrestacao": "3534401",
  "codigoTributacaoNacional": "010701",
  "codigoTributacaoMunicipal": "<ATIVIDADE_MUNICIPAL>",
  "codigoNbs": "<CODIGO_NBS>",
  "classTrib": "<CLASSIFICACAO_TRIBUTARIA_RTC>",
  "codigoCst": "<CST_IBS_CBS>",
  "idLocalIncidencia": 0,
  "indOperacao": "<INDICADOR_DE_OPERACAO>",
  "descricao": "<DESCRICAO_DO_SERVICO>"
}
```

Os campos RTC adicionais são opcionais no modelo HTTP, mas o provedor municipal ou a regra tributária pode exigir valores específicos. `codigoMunicipioPrestacao` é um código IBGE de 7 dígitos. Em Osasco, `codigoTributacaoMunicipal` é usado como `Atividade` quando informado; na ausência dele, a API tenta `itemListaServico` e depois `codigoTributacaoNacional`.

### Ordem recomendada de chamadas

1. `POST .../nfse` — envia os dados fiscais da nota e a `configuracao` completa.
2. Considere sucesso somente quando a resposta indicar `status: 3` (`Autorizada`).
3. Consulta (`POST .../nfse/consulta/{chave}`) e cancelamento (`POST .../nfse/{id}/cancelar`) repetem as credenciais no corpo.

Abaixo está o contrato completo do POST de emissão.

## Autenticação

Igual à NF-e: `Authorization: Bearer <NFE_API_TOKEN>` em todas as chamadas.

## Emitir uma NFS-e

```
POST /api/empresas/{empresaId}/nfse
Authorization: Bearer <token>
Idempotency-Key: <id único do seu lado>   (recomendado)
Content-Type: application/json
```

- O ambiente vem **somente de `configuracao.ambiente`** (`1` = produção, `2` = homologação/produção restrita). O parâmetro `?ambiente=` na URL é ignorado e o ambiente da empresa não é consultado.
- `Idempotency-Key`: se repetir a chamada após timeout/falha de transmissão,
  use a mesma chave para reenviar a mesma DPS. Se a DPS foi rejeitada e você vai
  corrigir os dados e emitir outro número, use um número e uma chave novos.
  Chamadas repetidas de uma DPS já autorizada devolvem a nota existente.

### Corpo (`NfseRequest`)

```json
{
  "numeroDps": 42,
  "dataEmissao": "2026-09-02T10:15:00-03:00",
  "dataCompetencia": "2026-09-02",
  "tomador": {
    "documento": "12345678000199",
    "nome": "Cliente Exemplo LTDA",
    "inscricaoMunicipal": null,
    "email": "financeiro@cliente.com.br",
    "telefone": "11999990000",
    "logradouro": "Rua Exemplo",
    "numero": "100",
    "complemento": "Sala 2",
    "bairro": "Centro",
    "codigoMunicipio": "3534401",
    "cep": "06010000"
  },
  "servico": {
    "codigoMunicipioPrestacao": "3534401",
    "codigoTributacaoNacional": "010701",
    "codigoTributacaoMunicipal": null,
    "codigoNbs": null,
    "classTrib": null,
    "codigoCst": null,
    "idLocalIncidencia": null,
    "indOperacao": null,
    "descricao": "Desenvolvimento e manutenção de sistemas — setembro/2026",
    "itemListaServico": "01.07",
    "cnae": "6201501"
  },
  "valores": {
    "valorServico": 1500.00,
    "descontoIncondicionado": 0,
    "descontoCondicionado": 0,
    "deducoes": 0,
    "tributacaoIssqn": 1,
    "retencaoIssqn": 1,
    "aliquotaIss": 2.00,
    "valorIss": 30.00,
    "valorPis": 0,
    "valorCofins": 0,
    "valorInss": 0,
    "valorIr": 0,
    "valorCsll": 0,
    "percentualTotalTributos": null
  },
  "informacoesComplementares": "Pedido 12345",
  "configuracao": {
    "padrao": 1,
    "ambiente": 2,
    "serie": "1",
    "opSimpNac": 3,
    "regApTribSN": 1,
    "regEspTrib": 0,
    "codigoMunicipioEmissao": "3534401",
    "inscricaoMunicipal": "123456",
    "versaoAplicativo": "MeuApp 2.0"
  }
}
```

Campos e regras:

| Campo | Obrigatório | Observações |
|---|---|---|
| `numeroDps` | recomendado para app emissor | Número escolhido pelo app (1 a 15 dígitos). A API o respeita e valida duplicidade por empresa/série/ambiente; omitido, usa o contador interno. |
| `servico.enderecoPrestacaoServico` | obrigatório em Osasco | Rua/logradouro do local real da prestação. |
| `servico.numeroPrestacaoServico` | obrigatório em Osasco | Número do endereço da prestação; não é o número da DPS. |
| `servico.bairroPrestacaoServico`, `cidadePrestacaoServico`, `estadoPrestacaoServico`, `cepPrestacaoServico` | obrigatórios em Osasco | Demais dados do endereço; CEP pode vir formatado ou só com dígitos. |
| `servico.codigoCidadeIBGEPrestacaoServico` | recomendado em Osasco | Código IBGE do local; se omitido, usa `codigoMunicipioPrestacao` e, por fim, o município cadastrado do prestador. |
| `dataEmissao` | não | Padrão: agora. Com offset de fuso. |
| `dataCompetencia` | não | Padrão: data da emissão. |
| `tomador` | não* | *Obrigatório para a maioria dos serviços. `documento` = CPF (11) ou CNPJ (14) só dígitos; para estrangeiro use `nif` e omita `documento`. |
| `tomador.codigoMunicipio` | condicional | Código IBGE (7 dígitos) quando houver endereço. |
| `tomador.cidade`, `tomador.uf` | condicional em Osasco | Com endereço do tomador, Osasco exige `logradouro`, `numero`, `bairro`, `cep`, `codigoMunicipio`, cidade e UF. A UF é derivada do IBGE; a cidade é preenchida automaticamente quando o IBGE é o da prestação/prestador. Para tomador de outro município, informe `cidade`. |
| `servico.codigoTributacaoNacional` | **sim** | 6 dígitos (`cTribNac`, ex.: `010701` = item 01.07 subitem 01). |
| `servico.descricao` | **sim** | Até 2000 caracteres. |
| `servico.codigoMunicipioPrestacao` | não | IBGE do local da prestação. Padrão: município do emissor. |
| `valores.valorServico` | **sim** | Maior que zero. |
| `valores.tributacaoIssqn` | não | `1` tributável, `2` imunidade, `3` exportação, `4` não incidência. Padrão `1`. |
| `valores.retencaoIssqn` | não | `1` não retido, `2` retido pelo tomador, `3` retido pelo intermediário. Padrão `1`. |
| `valores.aliquotaIss` / `valorIss` | condicional | Informe quando o município não calcula automaticamente (fora do SN ou ISS retido). |

Os enums são aceitos como número (`1`) ou nome (`"OperacaoTributavel"`).

### `configuracao` — única fonte da configuração de emissão

Nada é lido do cadastro de NFS-e nem recebe valor padrão: o que não estiver no JSON gera 422.

| Campo | Obrigatório | Observações |
|---|---|---|
| `configuracao.padrao` | sim | `1` Nacional, `2` ABRASF, `3` Osasco. |
| `configuracao.ambiente` | sim | `1` produção, `2` homologação. Gravado na nota e usado depois na consulta e no cancelamento. |
| `configuracao.serie` | sim | Até 5 posições. A numeração é reservada para essa série. |
| `configuracao.opSimpNac` | sim | `1` não optante, `2` MEI, `3` ME/EPP. |
| `configuracao.regApTribSN` | se `opSimpNac = 3` | `1..3`. |
| `configuracao.regEspTrib` | sim | Regime especial de tributação (`0` = nenhum). |
| `configuracao.codigoMunicipioEmissao` | sim | Código IBGE (7 dígitos) do município emissor. |
| `configuracao.inscricaoMunicipal` | sim | Inscrição municipal do prestador. |
| `configuracao.versaoAplicativo` | não | `verAplic`, texto livre (até 20). |
| `configuracao.osascoChaveAutenticacao` | Osasco | Obrigatória. Nunca lida do servidor. |
| `configuracao.abrasfUrl`, `abrasfVersao`, `abrasfUsuario`, `abrasfSenha` | ABRASF | `abrasfUrl` obrigatória; os demais conforme exigência do município. Nunca lidos do servidor. |

O certificado A1 e o CNPJ/identidade do prestador continuam sob responsabilidade do servidor; o CNPJ precisa corresponder ao certificado usado na assinatura.

### Resposta (200)

```json
{
  "id": "5d7f...-...",
  "empresaId": "...",
  "padrao": 1,
  "ambiente": 2,
  "serie": "1",
  "numeroDps": 42,
  "idDps": "DPS35344012...",
  "chaveAcesso": "35344012345678000199000010000000000000004212345678",
  "numeroNfse": "202600000000042",
  "codigoVerificacao": null,
  "status": 3,
  "statusDescricao": "Autorizada",
  "dataEmissao": "2026-09-02T13:15:00",
  "dataAutorizacao": "2026-09-02T13:15:02",
  "tomadorDocumento": "12345678000199",
  "tomadorNome": "Cliente Exemplo LTDA",
  "valorServico": 1500.00,
  "valorIss": 30.00,
  "issRetido": false,
  "valorLiquido": 1500.00,
  "codigoRetorno": null,
  "motivoRetorno": null,
  "chaveIdempotencia": "pedido-12345",
  "xmlBase64": "PD94bWwg...",
  "danfsePdfBase64": "JVBERi0x...",
  "duracaoMs": 1834,
  "reaproveitada": false
}
```

- `xmlBase64`: XML **da NFS-e autorizada** (distribuído pelo SEFIN), UTF-8 em Base64.
- `danfsePdfBase64`: PDF do DANFSe pronto para salvar/imprimir/enviar por e-mail.
- `status`: `1` Pendente, `2` Enviada, `3` **Autorizada**, `4` Rejeitada,
  `5` Cancelada, `6` ErroTransmissao. Trate somente `3` como sucesso fiscal.

### Erros

| HTTP | Quando | Corpo |
|---|---|---|
| 422 | Validação do JSON ou rejeição fiscal (com detalhes) | `{ "erro": "...", "erros": ["...", "..."] }` |
| 422 | Certificado ausente/vencido/não decifrável | `{ "erro": "..." }` |
| 401 | Token inválido | — |
| 404 | Empresa não encontrada | — |

Rejeição pelo SEFIN também fica gravada na nota (`status = 4`, `codigoRetorno`,
`motivoRetorno`) e em **Tentativas NFS-e** no painel — inclusive as falhas que
ocorrem antes do envio (validação, assinatura, rede).

## Consultar uma NFS-e já emitida

Local (banco desta API):

```
GET /api/nfse/{id}                 -> JSON da nota + eventos
GET /api/nfse/{id}/xml             -> XML da NFS-e (application/xml)
GET /api/nfse/{id}/danfse.pdf      -> PDF do DANFSe
GET /api/empresas/{empresaId}/nfse?status=3&ambiente=2&busca=&pagina=1&tamanho=25
```

Na Receita (situação atual pela chave de 50 dígitos):

```
POST /api/empresas/{empresaId}/nfse/consulta/{chave}
{
  "padrao": 3,
  "ambiente": 2,
  "configuracao": { "osascoChaveAutenticacao": "<CHAVE_DO_PORTAL_DE_OSASCO>" }
}
-> { "encontrada": true, "cancelada": false, "chaveAcesso": "...", "numeroNfse": "...",
     "codigo": null, "motivo": null, "xmlBase64": "..." }
```

É `POST` (e não `GET`) porque as credenciais do provedor vão no corpo. Para uma nota emitida por este servidor, `padrao` e `ambiente` são os da própria nota; para uma chave que não existe localmente, **envie-os**. As credenciais (`configuracao`) são obrigatórias para Osasco e ABRASF; para o padrão Nacional o corpo pode ser vazio.

## Cancelar

```
POST /api/empresas/{empresaId}/nfse/{id}/cancelar
{ "codigoMotivo": "1", "justificativa": "Emitida com valor incorreto (mín. 15 caracteres)" }
```

Padrão, série e ambiente do cancelamento são os da nota (os que vieram no JSON da emissão). **As credenciais do provedor são obrigatórias no corpo**, em `configuracao`: `osascoChaveAutenticacao` (Osasco) ou `abrasfUrl`/`abrasfUsuario`/`abrasfSenha` (ABRASF). Nada é lido do servidor; sem elas a API devolve 422 listando o que falta.

```json
{
  "codigoMotivo": "1",
  "justificativa": "Emitida com valor incorreto (mín. 15 caracteres)",
  "configuracao": { "osascoChaveAutenticacao": "<CHAVE_DO_PORTAL_DE_OSASCO>" }
}
```

Motivos do padrão nacional (`GET /api/nfse/motivos-cancelamento`):
`1` erro na emissão, `2` serviço não prestado, `9` outros. A resposta traz a
nota com `status = 5` e o evento e101101 em `eventos[]`. O cancelamento exige
a nota autorizada e respeita o prazo definido pelo município.

## Numeração da DPS

O `numeroDps` (RPS/DPS) é **obrigatório no JSON**: o app emissor é a autoridade da
sequência. A API não oferece endpoint de numeração; apenas valida duplicidade
dentro de empresa/série/ambiente (`serie_dps` serializa chamadas concorrentes e
guarda o maior número recebido como referência interna). Em caso de falha de
transmissão, repita com a mesma `Idempotency-Key` para reenviar a mesma DPS.

## Configuração por empresa

**Não existe configuração de NFS-e guardada no servidor.** O `GET /api/empresas/{empresaId}/nfse/config` e o `/nfse/numeracao` foram removidos (chamá-los retorna 404). O `PUT /api/empresas/{empresaId}/nfse/config` existe só por compatibilidade com apps antigos: não grava nada, devolve 200 com `gravado: false` e a lista `pendenciasParaEmitir` (campos que faltam para emitir). Apps novos não devem chamá-lo. Padrão, ambiente, série, regime, município e credenciais do provedor vêm sempre do JSON de cada chamada (emissão, consulta e cancelamento). O servidor guarda apenas o certificado A1, a identidade do prestador (empresa), os logs/tentativas e o histórico das notas.

## Catálogo de municípios (ACBrNFSeX)

Os webservices de cada prefeitura vêm do arquivo `ACBrNFSeXServicos.ini` do ACBr
(`backend/src/NFe.Nfse/Municipios/`), embutido na API como recurso. O
`CatalogoMunicipiosNfse` lê esse `.ini` pelo código IBGE (`codigoMunicipioEmissao`)
e resolve o padrão/provedor e as URLs de produção e homologação. Para atualizar
ou acrescentar municípios, substitua o `.ini` pela versão mais recente do ACBr; não
é preciso informar URL no JSON quando o município consta no catálogo.

### Consultar municípios com emissão configurada

```http
GET /api/nfse/municipios?uf=SP&busca=os&padrao=3&ambiente=2&page=1&pageSize=50
```

Filtros opcionais: `uf`, `busca` (nome ou código IBGE), `padrao` (`1` Nacional,
`2` ABRASF, `3` Osasco), `ambiente` (`1` produção, `2` homologação), `page` e
`pageSize` (máximo 500). Sem filtros, retorna os municípios do catálogo
com provedor/padrão reconhecido e URL cadastrada (o Padrão Nacional não depende de
URL municipal). A resposta inclui o código IBGE, nome, UF, provedor, padrão e URLs
de produção/homologação.

Sem `page` e sem `pageSize`, a rota devolve a lista **inteira** (cerca de 3 mil
municípios), ordenada por UF e nome. Ao informar qualquer um dos dois, a resposta é
paginada (`pageSize` padrão 100) e traz `totalPaginas`. A lista é ordenada por UF,
então Osasco (SP) cai na página 29 com 100 itens por página; para achá-lo use
`?busca=osasco` ou `?uf=SP`.

A presença na lista significa que há configuração do provedor no catálogo ACBr;
não garante que o prestador tenha inscrição, credenciais ou autorização municipal
para emitir. Os requisitos de cada prefeitura continuam valendo.

```json
{
  "catalogo": "ACBrNFSeXServicos.ini",
  "total": 1,
  "page": 1,
  "pageSize": 50,
  "totalPaginas": 1,
  "municipios": [{
    "codigoIbge": "3534401",
    "nome": "Osasco",
    "uf": "SP",
    "provedor": "eGoverneISS",
    "padrao": 3,
    "padraoDescricao": "Osasco",
    "urlProducao": "https://nfe.osasco.sp.gov.br/EissnfeWebServices/NotaFiscalEletronica.svc",
    "urlHomologacao": "https://nfe.osasco.sp.gov.br/EissnfeWebServices/NotaFiscalEletronica.svc"
  }]
}
```

A busca não verifica disponibilidade online dos webservices; ela lista os municípios
configurados no arquivo versionado com a API.

## Ambientes do Padrão Nacional

| `ambiente` | Nome | Uso |
|---|---|---|
| `2` | Produção restrita | Testes com certificado real; notas sem valor fiscal |
| `1` | Produção | Notas válidas |

A empresa precisa estar habilitada no município para o emissor nacional
(convênio com o Sistema Nacional NFS-e). Municípios não aderentes usam
`padrao = 2` (ABRASF) com a URL do provedor municipal.

## Variáveis de ambiente (opcionais)

```
NFSE_TIMEOUT_SEGUNDOS=60    # timeout das chamadas HTTP ao SEFIN/ADN
NFSE_LOG_CORPOS=false       # true loga corpos das requisições (depuração)
```

Os XMLs (DPS assinada e NFS-e) ficam em `{NFE_XML_DIR}/{cnpj}/nfse/`, separados
dos arquivos da NF-e.

## Checklist de implantação (EC2)

```bash
cd ~/apps/aprovador && git pull
sudo bash backend/deploy/deploy-nfe-api.sh
sudo journalctl -u nfe-api.service -n 80 --no-pager | grep -i "schema\|json_recebido\|migra"
```

O script publica em staging, conecta ao banco configurado no `.env.local`, cria
`json_recebido` como `LONGTEXT NULL` nas tabelas de tentativas existentes e só
então troca os arquivos e reinicia o serviço. É idempotente: pode ser executado
em cada servidor; se a coluna já existir, não a recria nem altera os dados. O
script obtém o diretório de publicação do `WorkingDirectory` da unit systemd.

Depois, no painel: **Configuração NFS-e** → preencher → **Ativa** → emitir uma
DPS em produção restrita (`ambiente=2`) e conferir em **NFS-e emitidas**.
