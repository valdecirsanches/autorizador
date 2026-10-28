# Autorizador de NF-e

Autorizador multiempresa de Nota Fiscal Eletrônica (modelo 55) com backend em
**.NET 9** e painel administrativo em **Next.js 16**. O backend gera, assina
(XMLDSig), valida (XSD) e transmite documentos à SEFAZ via SOAP com mTLS,
guardando os certificados A1 num cofre cifrado com AES-GCM. O painel monitora o
status dos serviços da SEFAZ por UF e gerencia empresas e certificados.

---

## Arquitetura

```
┌─────────────────────┐        HTTP same-origin        ┌────────────────────┐
│  Painel Next.js 16  │  /api/proxy/*  ───────────────▶│   API .NET 9       │
│  (porta 3000)       │                                │   (porta 5240)     │
└─────────────────────┘                                └─────────┬──────────┘
                                                                  │
                                          ┌───────────────────────┼───────────────────────┐
                                          ▼                       ▼                       ▼
                                   MySQL/MariaDB          Cofre AES-GCM            SOAP + mTLS
                                   (dados)                (certificados A1)        (SEFAZ)
```

Projetos do backend (`backend/NFe.Autorizador.sln`):

| Projeto | Responsabilidade |
| --- | --- |
| `NFe.Domain` | Entidades, enums, chave de acesso (DV módulo 11), modelos de requisição |
| `NFe.Sefaz` | Geração de XML, assinatura XMLDSig, validação XSD, SOAP, endpoints por UF, clientes de serviço |
| `NFe.Infrastructure` | EF Core (MySQL), cofre de certificados, orquestração de emissão, reserva de numeração |
| `NFe.Api` | API REST mínima consumida pelo painel |
| `NFe.Tests` | Suíte de testes (unidade + integração com MySQL real) |

> **Observação sobre o preview:** o navegador não alcança a porta interna da API
> (`127.0.0.1:5240`), mas o servidor Next.js alcança. Por isso o painel fala com
> a API através de um proxy same-origin em `app/api/proxy/[...path]/route.ts`,
> que também resolve o CORS. Em produção, aponte `NFE_API_URL` para o host real.

---

## Pré-requisitos

- **.NET SDK 9.0**
- **Node.js 20+** e **pnpm** (`corepack enable` ou `npm i -g pnpm`)
- **MySQL 8** ou **MariaDB 10.6+**
- **openssl** (para gerar a chave mestra do cofre)

---

## Configuração centralizada (`.env.local`)

Todas as configurações — do painel **e** da API — ficam num único arquivo
`.env.local` na **raiz do projeto**. O Next.js lê `.env.local` nativamente; a
API .NET usa um carregador próprio (`backend/src/NFe.Api/DotEnv.cs`) que sobe a
árvore de diretórios, encontra esse mesmo arquivo e popula as variáveis de
ambiente do processo. Variáveis já definidas no ambiente real **têm prioridade**
(não são sobrescritas), então produção/CI continuam no controle.

Comece copiando o modelo e ajustando os valores:

```bash
cp .env.example .env.local
```

O `.env.local` **não é versionado** (o `.gitignore` já ignora `.env*.local`),
então seus segredos ficam fora do Git. O `.env.example` é versionado e serve de
referência.

### Backend (API .NET)

| Variável | Obrigatória | Descrição |
| --- | --- | --- |
| `MYSQL_CONNECTION_STRING` | sim | String de conexão do MySQL/MariaDB. |
| `NFE_CERT_MASTER_KEY` | sim | Chave mestra do cofre AES-GCM: **32 bytes em base64**. |
| `NFE_CORS_ORIGINS` | não | Origens liberadas no CORS (separadas por vírgula). Default `http://localhost:3000`. |
| `Sefaz__PermitirCadeiaNaoConfiavel` | não | Raramente necessária: a cadeia ICP-Brasil já vem embutida no app. Só use `true` em dev como último recurso se a validação embutida falhar. **Sempre `false` em produção.** |
| `NFE_SCHEMAS_DIR` | recomendada | Diretório com os XSDs oficiais (pacote PL_009_V4). Sem ele, a validação XSD falha de propósito (veja abaixo). |
| `NFE_AUTO_MIGRATE` | não | Aplica as migrations automaticamente no startup. Default `true`. Defina `false` para controlar as migrations manualmente. |

Gere a chave mestra uma única vez e guarde-a com segurança (perdê-la torna os
certificados cifrados irrecuperáveis):

```bash
openssl rand -base64 32
```

Exemplo de connection string:

```
Server=127.0.0.1;Port=3306;Database=nfe_autorizador;User Id=nfeapp;Password=SUA_SENHA;
```

### Frontend (painel Next.js)

| Variável | Obrigatória | Descrição |
| --- | --- | --- |
| `NFE_API_URL` | não | URL base da API .NET. Default `http://127.0.0.1:5240`. |

---

## Banco de dados

Crie o banco e o usuário (ajuste a senha):

```sql
CREATE DATABASE nfe_autorizador CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'nfeapp'@'%' IDENTIFIED BY 'SUA_SENHA';
GRANT ALL PRIVILEGES ON nfe_autorizador.* TO 'nfeapp'@'%';
FLUSH PRIVILEGES;
```

**As tabelas são criadas automaticamente.** Com `NFE_AUTO_MIGRATE=true` (o
default), a API aplica as migrations pendentes no startup — não é preciso rodar
nada à mão. Basta o banco e o usuário existirem e a `MYSQL_CONNECTION_STRING`
estar no `.env.local`.

> Se o banco estiver vazio (nenhuma tabela) mesmo depois de subir a API,
> verifique os logs de startup: normalmente é a `MYSQL_CONNECTION_STRING`
> apontando para host/porta errados ou o MySQL fora do ar. A porta padrão do
> MySQL local é **3306**.

Se preferir aplicar as migrations manualmente (com `NFE_AUTO_MIGRATE=false`), a
ferramenta `dotnet-ef` lê a connection string de `MYSQL_CONNECTION_STRING` (via
`DesignTimeDbContextFactory`):

```bash
# instalar a ferramenta uma vez
dotnet tool install --global dotnet-ef

cd backend
export MYSQL_CONNECTION_STRING="Server=127.0.0.1;Port=3306;Database=nfe_autorizador;User Id=nfeapp;Password=SUA_SENHA;"

dotnet ef database update \
  --project src/NFe.Infrastructure \
  --startup-project src/NFe.Api
```

Para criar uma nova migration após mudar o modelo:

```bash
dotnet ef migrations add NomeDaMigration \
  --project src/NFe.Infrastructure \
  --startup-project src/NFe.Api
```

---

## Subir a API .NET

Com o `.env.local` preenchido na raiz, **não é preciso exportar variáveis** —
a API lê tudo dele e aplica as migrations no startup:

```bash
cd backend
cd backend
dotnet run --project src/NFe.Api --urls "http://127.0.0.1:5240"
```

Nos logs de startup você verá algo como:

```
Aplicando 1 migracao(oes) pendente(s): 20260728151210_InicialMultiempresa
Migracoes aplicadas. Banco pronto.
Now listening on: http://127.0.0.1:5240
```

Confirme que subiu:

```bash
curl http://127.0.0.1:5240/health
# {"status":"ok","hora":"..."}
```

Em desenvolvimento, o OpenAPI fica exposto em `/openapi/v1.json`.

### Endpoints principais

| Método | Rota | Descrição |
| --- | --- | --- |
| `GET` | `/health` | Health check. |
| `GET` | `/api/status?ambiente=2` | Último status por UF (1=produção, 2=homologação). |
| `POST` | `/api/status/empresa/{id}/consultar` | Consulta a SEFAZ agora (exige certificado ativo). Corpo: `uf`, `ambiente` (1\|2), `ufAutorizadorOverride` e `contingencia` opcionais. |
| `GET` | `/api/empresas` | Lista empresas. |
| `POST` | `/api/empresas` | Cria empresa. |
| `PUT` | `/api/empresas/{id}` | Atualiza empresa. |
| `GET` | `/api/certificados` | Lista certificados (só metadados). |
| `POST` | `/api/certificados` | Faz upload de A1 (.pfx) — `multipart/form-data` com `empresaId`, `senha`, `arquivo`. |
| `GET` | `/api/certificados/empresa/{id}` | Certificados de uma empresa. |
| `GET` | `/api/empresas/{id}/inutilizacoes` | Histórico de inutilizações de faixa. |
| `POST` | `/api/empresas/{id}/inutilizacoes` | Inutiliza uma faixa de numeração (`NfeInutilizacao4`) — **irreversível**. Corpo: `uf`, `cnpj`, `ufAutorizadorOverride` (opcional), `modelo`, `serie`, `ambiente`, `ano` (opcional), `numeroInicial`, `numeroFinal`, `justificativa` (15–255 caracteres). |

A inutilização declara à SEFAZ que uma faixa `[inicial, final]` de uma série
**não** será usada (quebra de sequência por falha de sistema). A API recusa
(`422`) faixas que contenham NF-e já autorizada ou cancelada, valida a
justificativa e persiste sempre o resultado (homologado `cStat 102` ou
rejeitado) com o XML bruto para guarda fiscal, montando o `procInutNFe` quando
homologada.

---

## Subir o painel Next.js

Num outro terminal, a partir da raiz do projeto:

```bash
pnpm install
export NFE_API_URL="http://127.0.0.1:5240"   # opcional; este é o default
pnpm dev
```

Abra `http://localhost:3000`. O painel tem as seções **Status SEFAZ**, **Notas
emitidas**, **Tentativas**, **Consultar NF-e**, **Empresas**, **Certificados** e
**Numeração** (contadores de série e inutilização de faixa), com alternância
entre ambientes de **Homologação** e **Produção**.

Build de produção:

```bash
pnpm build
pnpm start
```

---

## Schemas XSD

O pacote oficial de schemas da NF-e (**PL_009_V4** ou posterior) **não é
versionado** neste repositório — o portal da SEFAZ o distribui atrás de um fluxo
com estado. Baixe-o do Portal Nacional da NF-e e extraia os `.xsd` num
diretório, então aponte `NFE_SCHEMAS_DIR` para ele.

Decisão deliberada de segurança: se o diretório ou o schema pedido não existir,
a validação **falha explicitamente** (`XsdIndisponivelException`) em vez de
passar em silêncio. Um autorizador que "valida" sem schema daria uma falsa
sensação de segurança e só revelaria o erro como rejeição na SEFAZ.

---

## Testes

A suíte inclui testes de unidade e testes de integração que usam um MySQL real
(numeração concorrente, reserva de faixas). Defina a connection string de teste
antes de rodar:

```bash
cd backend
export MYSQL_CONNECTION_STRING="Server=127.0.0.1;Port=3306;Database=nfe_autorizador;User Id=nfeapp;Password=SUA_SENHA;"
dotnet test
```

Testes que dependem de rede externa (SEFAZ) são marcados como *skipped* quando o
ambiente não tem acesso, e não quebram a suíte.

---

## Notas de produção e segurança

- **Nunca** versione a `NFE_CERT_MASTER_KEY`, senhas ou arquivos `.pfx`. Use um
  gerenciador de segredos.
- A cadeia de ACs da ICP-Brasil (raízes + intermediárias) vem **embutida no
  app** (`NFe.Sefaz/Certificados/icp-brasil-bundle.pem`) e é usada para validar
  o certificado do servidor da SEFAZ. Não é preciso instalar as raízes no SO —
  funciona em Linux/containers out-of-the-box, de forma segura (o nome do host
  continua sendo validado). Mantenha `Sefaz__PermitirCadeiaNaoConfiavel=false`
  em produção; a flag é apenas um último recurso para desenvolvimento.
- A senha do certificado nunca é persistida: o A1 é aberto, a chave privada é
  recifrada com AES-GCM sob a chave mestra e só então gravada.
- A numeração de NF-e é reservada com lock pessimista (`SELECT ... FOR UPDATE`),
  garantindo faixas sem lacunas nem repetições mesmo sob concorrência.
- Restrinja `NFE_CORS_ORIGINS` ao domínio real do painel em produção.
