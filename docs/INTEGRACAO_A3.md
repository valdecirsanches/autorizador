# Certificado A3 — o que o seu app precisa fazer

No A3 a chave privada fica no token do cliente e nunca sai dele. Por isso existe um
**agente** (`NFeAgenteA3`, Windows) na maquina onde o token esta plugado. Ele abre uma
conexao de saida (WebSocket) com a API e assina o que a API pedir. O app nao fala com o
agente: **o app fala so com a API**, e a API usa o agente quando precisa assinar.

```
App  --HTTP-->  API  <--WebSocket (saida)--  Agente A3  --USB-->  Token
```

Toda chamada HTTP abaixo exige `Authorization: Bearer <NFE_API_TOKEN>`.
O unico canal que nao usa esse token e `/agente/ws`, que usa o token de pareamento.

---

## Visao geral: 4 etapas

| # | Quem | O que |
|---|------|-------|
| 1 | App | Envia a parte publica do certificado (`.cer`) e recebe o `tokenAgente` |
| 2 | App | Mostra o `tokenAgente` ao usuario (aparece **uma unica vez**) |
| 3 | Usuario | Na maquina do token, roda `NFeAgenteA3 configurar` e informa URL da API, o `tokenAgente` e o PIN |
| 4 | App | Consulta se o agente esta online e, depois, emite normalmente |

A emissao (`POST /api/empresas/{empresaId}/nfe`) **nao muda**: o JSON e o mesmo do A1.
A API detecta que o certificado ativo da empresa e A3 e assina pelo agente.

---

## 1. Cadastrar o A3 (parte publica, sem senha)

`POST /api/certificados/empresa/{empresaId}/a3` — `multipart/form-data`

| Campo | Obrigatorio | Descricao |
|-------|-------------|-----------|
| `arquivo` | sim | Certificado publico `.cer` ou `.crt` (ate 64 KB). Nunca envie `.pfx` nem PIN |
| `descricao` | nao | Rotulo do certificado |
| `nomeAgente` | nao | Nome da maquina/agente, para identificar no painel |

```bash
curl -X POST "$API/api/certificados/empresa/$EMPRESA_ID/a3" \
  -H "Authorization: Bearer $NFE_API_TOKEN" \
  -F "arquivo=@certificado-publico.cer" \
  -F "descricao=A3 Matriz" \
  -F "nomeAgente=PC do financeiro"
```

Resposta `201`:

```json
{
  "certificado": { "id": "…", "tipo": "A3", "thumbprint": "…", "validoAte": "…" },
  "agenteId": "…",
  "tokenAgente": "…",
  "aviso": "Guarde o token agora: ele nao sera exibido novamente. …"
}
```

Regras:

- `tokenAgente` e devolvido **so nesta resposta**. No banco fica apenas o hash. Se o
  usuario perder, refaca o cadastro.
- Cadastrar de novo **revoga os agentes anteriores** da empresa e o novo A3 vira o ativo.
- Erro de certificado invalido responde `400` com `{ "erro": "…" }`.

---

## 2. Mostrar o token ao usuario

O app deve exibir o `tokenAgente` em destaque, com botao de copiar e o aviso de que ele
nao aparece de novo. Nao grave esse token no seu banco.

---

## 3. Parear o agente (acao do usuario, na maquina do token)

Com o token plugado, o driver instalado e o Windows ligado:

```
NFeAgenteA3 configurar   # pede: URL da API, tokenAgente, escolha do certificado e PIN
NFeAgenteA3 instalar     # instala como servico para iniciar sozinho
```

- O PIN fica cifrado localmente (DPAPI) e **nunca e enviado a API**.
- O `configurar` testa o PIN com uma assinatura unica e so salva se funcionar.
- O certificado escolhido precisa ser o mesmo cadastrado no passo 1. Se o thumbprint for
  outro, a API recusa a conexao.
- Para testar sem instalar o servico, rode apenas `NFeAgenteA3`.

---

## 4. Verificar se o agente esta online

`GET /api/certificados/{certificadoId}/agente`

```json
{
  "online": true,
  "conectadoEm": "2026-10-26T12:00:00Z",
  "agentes": [
    {
      "id": "…", "nome": "PC do financeiro", "revogado": false,
      "versao": "1.0.0", "ultimoContatoEm": "…", "criadoEm": "…", "online": true
    }
  ]
}
```

Use `online` para mostrar um indicador no app (verde/vermelho) e para **bloquear o botao
de emitir** com uma mensagem clara quando estiver `false`.

Para revogar um agente (maquina perdida, troca de token):
`DELETE /api/certificados/{certificadoId}/agente/{agenteId}` → `204`.

---

## 5. Emitir

Igual ao A1, sem campo novo. Veja `docs/COMECE_AQUI.md` e `docs/EMISSAO_NFE.md`.

```bash
curl -X POST "$API/api/empresas/$EMPRESA_ID/nfe" \
  -H "Authorization: Bearer $NFE_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d @nota.json
```

### Quando o agente esta offline

A API falha **antes** de gravar qualquer documento e responde `503`:

```json
{
  "erro": "Agente do certificado A3 desconectado.",
  "detalhe": "Agente do certificado A3 desconectado. Verifique se a maquina com o token esta ligada, com o token plugado e o agente em execucao.",
  "tipo": "AgenteCertificadoException"
}
```

O app deve tratar o `503` com `tipo = "AgenteCertificadoException"` como **temporario**:
mostrar "Maquina do token offline", permitir tentar de novo e **nao** marcar a nota como
rejeitada. Nenhum numero foi consumido.

Outros erros de certificado (vencido, sem parte publica, nenhum ativo) continuam
voltando como erro de negocio com o texto em `erro`/`detalhe`.

---

## Checklist do app

- [ ] Tela de cadastro A3: upload do `.cer` + `descricao` + `nomeAgente`.
- [ ] Exibir o `tokenAgente` uma unica vez, com instrucao do passo 3.
- [ ] Indicador de agente online (consultar o endpoint do passo 4, ex.: a cada 30 s).
- [ ] Bloquear ou avisar na emissao quando `online = false`.
- [ ] Tratar `503` + `AgenteCertificadoException` como falha temporaria, com "tentar de novo".
- [ ] Acao para revogar agente.
- [ ] Alertar quando faltar pouco para `validoAte` do certificado.

## Limites importantes

- A maquina do token precisa estar **ligada, com o token plugado e o agente rodando**
  sempre que houver emissao (autorizacao, cancelamento, carta de correcao, NFS-e).
- Um unico agente por certificado: parear outro revoga o anterior.
- Troca de certificado (renovacao) exige novo cadastro e novo pareamento, pois o
  thumbprint muda.
