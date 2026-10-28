# Chaves de acesso por app

Cada sistema que emite notas por esta API (ERP, PDV, app de delivery...) recebe a
**sua própria chave**. A chave:

- só enxerga as **empresas liberadas** para aquele app;
- **não** acessa rotas administrativas (monitor, exclusão de empresa, pasta de XML,
  listagem geral de certificados, gestão de chaves);
- pode ser **suspensa, trocada ou excluída** sem afetar os outros apps.

O **token mestre** (`NFE_API_TOKEN`) continua funcionando exatamente como antes,
com acesso total. É o que o painel usa. Integrações que já usam o token mestre
**não quebram**, mas o ideal é migrá-las para chaves próprias e deixar o mestre só
no painel.

---

## Para o administrador

### Criar a chave de um app

No painel: **Sistema → Apps integrados → Novo app**. Informe o nome, marque as
empresas e clique em **Criar e gerar chave**. A chave aparece **uma única vez**:
copie e entregue ao desenvolvedor do app. O servidor guarda só o hash (SHA-256).

Pela API, com o token mestre:

```bash
curl -X POST https://SUA-API/api/clientes \
  -H "Authorization: Bearer $NFE_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"nome":"ERP Loja Centro","empresaIds":["a19c9920-6951-4008-bbbd-acd805838094"],"podeCadastrarEmpresas":false}'
```

Resposta `201`:

```json
{
  "cliente": { "id": "…", "nome": "ERP Loja Centro", "identificadorChave": "nfeapp_Q2h4c0…", "empresaIds": ["a19c…"], "ativo": true, … },
  "chave": "nfeapp_Q2h4c0R3b1pV…",
  "aviso": "Guarde esta chave agora: ela NAO sera exibida de novo…"
}
```

### Gerenciar

| O que fazer | Rota (token mestre) |
|---|---|
| Listar apps | `GET /api/clientes` |
| Ver um app | `GET /api/clientes/{id}` |
| Trocar empresas / nome / permissão | `PUT /api/clientes/{id}` com `{"empresaIds":[…]}` (a lista **substitui** a anterior) |
| Suspender / reativar | `PUT /api/clientes/{id}` com `{"ativo": false}` / `{"ativo": true}` |
| Gerar nova chave (a antiga para na hora) | `POST /api/clientes/{id}/nova-chave` |
| Excluir o app | `DELETE /api/clientes/{id}` |

Campos ausentes no `PUT` ficam como estão.

### Permissão "pode cadastrar empresas"

Desligada por padrão. Quando ligada, o app pode usar `POST /api/empresas` e
`POST /api/empresas/onboarding`:

- empresa **nova** criada pelo app fica liberada para ele automaticamente;
- CNPJ que **já existe** e não está liberado para o app → `403` (um app não
  sobrescreve o cadastro de empresa de outro). Libere manualmente se for o caso.

### No monitor da API

Requisições feitas com chave de app aparecem com o usuário **`App: <nome>`**,
então dá para saber qual sistema fez cada chamada.

---

## Para o desenvolvedor do app

Envie a chave em **toda** requisição, no mesmo cabeçalho de sempre:

```
Authorization: Bearer nfeapp_SUA_CHAVE
```

Confira o que a chave enxerga:

```bash
curl https://SUA-API/api/clientes/eu -H "Authorization: Bearer nfeapp_SUA_CHAVE"
```

```json
{
  "tipo": "app",
  "nome": "ERP Loja Centro",
  "podeCadastrarEmpresas": false,
  "empresas": [ { "id": "a19c…", "cnpj": "67570036000181", "razaoSocial": "SUA EMPRESA LTDA", "uf": "SP", "ativa": true } ]
}
```

O resto do fluxo é o mesmo de `COMECE_AQUI.md` (descobrir o ID pelo CNPJ, emitir,
consultar, cancelar, DANFE...).

### Respostas novas

| HTTP | Quando | O que fazer |
|---|---|---|
| `401` `"Nao autorizado."` | Chave errada ou ausente | Confira o cabeçalho `Authorization`. |
| `401` com `detalhe: "Chave de app desativada…"` | O administrador suspendeu o app | Fale com o administrador. |
| `403` `"Empresa nao encontrada ou nao liberada para esta chave."` | Empresa (ou CNPJ) de outro app, ou inexistente | Peça a liberação da empresa. |
| `403` com outro `detalhe` | Rota administrativa ou não liberada para apps | Use a rota indicada no `detalhe`. |

Por segurança, "empresa não existe" e "empresa de outro app" dão a **mesma**
resposta, para um app não descobrir quais CNPJs são clientes da API.

---

## O que um app pode e não pode chamar

**Pode** (nas empresas liberadas): tudo de emissão, consulta, cancelamento, carta de
correção, inutilização, DANFE/DANFSe, XML, numeração, tentativas, logo, certificado
da empresa (`/api/certificados/empresa/{id}`), ambiente e dados cadastrais da
empresa, status da SEFAZ, NFS-e.

**Pode** (sem empresa): `GET /api/status`, `GET /api/nfse/municipios`,
`GET /api/nfse/motivos-cancelamento`, `GET /openapi/v1.json`, `GET /api/clientes/eu`,
`GET /api/empresas` (já vem filtrada).

**Não pode** (só token mestre): `/api/clientes/*` (exceto `/eu`), `/api/monitor/*`,
`/diag/*`, `GET|POST /api/certificados` (lista/upload global), `DELETE /api/empresas/{id}`,
`/api/empresas/{id}/pasta-xml*`.

**Rota nova** adicionada à API no futuro nasce **bloqueada** para apps até ser
classificada em `src/NFe.Api/ClientesApi/PoliticaAcessoApi.cs`.

---

## Detalhes técnicos

- Tabelas `api_cliente` e `api_cliente_empresa`, criadas automaticamente no startup
  (`CREATE TABLE IF NOT EXISTS`, aditivo). Não há migration do EF e o
  `NFeDbContext` não mudou.
- Chave: `nfeapp_` + 32 bytes aleatórios em base64url. No banco: só o SHA-256.
- A consulta da chave usa um cache de 60 s por processo. Mudanças feitas pelas
  rotas de gestão limpam o cache na hora. Se você rodar **mais de uma instância**
  da API, uma suspensão leva até 60 s para valer nas outras instâncias.
- Código: `src/NFe.Api/ClientesApi/` — `AutenticacaoApi.cs` (middleware),
  `PoliticaAcessoApi.cs` (regras), `RepositorioClientesApi.cs` (banco),
  `ClientesApiEndpoints.cs` (rotas). Testes: `tests/NFe.Tests/ClientesApiTests.cs`.
