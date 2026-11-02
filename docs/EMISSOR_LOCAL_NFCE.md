# Emissor local da NFC-e (contingência off-line)

Programa que roda **no computador do caixa** e emite NFC-e em **contingência off-line**
(`tpEmis` 9) quando a internet ou a SEFAZ caem. Ele gera e assina a nota com o
certificado da loja, aplica o QR Code v3 assinado, guarda tudo numa fila em disco e,
quando a conexão volta, envia cada nota à **API central**, que transmite à SEFAZ.

```
Caixa (navegador) ──> Emissor local 127.0.0.1:5240 ──(quando houver internet)──> API central ──> SEFAZ
                        assina + QR Code v3                                        /nfce/contingencia
                        fila em disco
```

- Usa o **mesmo** gerador de XML, assinatura e QR Code da API central.
- Não usa banco de dados. A fila é uma pasta com um JSON por nota.
- Só faz contingência off-line. A emissão normal continua na API central.

## 1. Instalar (Windows)

No servidor (onde está o código), gere o executável para Windows:

```bash
cd ~/apps/autorizador/backend
dotnet publish src/NFe.EmissorLocal -c Release -r win-x64 --self-contained -o publish/emissor-local
```

Copie a pasta `publish/emissor-local` para o caixa, por exemplo em `C:\NFeEmissorLocal`,
junto com o certificado A1 (`certificado.pfx`).

Edite `C:\NFeEmissorLocal\appsettings.json`:

| Chave (`EmissorLocal:`) | Valor |
|---|---|
| `Url` | `http://127.0.0.1:5240` (só o próprio computador acessa). |
| `Token` | Segredo longo (16+ caracteres) que o caixa envia em `Authorization: Bearer ...`. Gere com `openssl rand -hex 32`. |
| `OrigensPermitidas` | Endereço do Facil no navegador, ex.: `["https://facil.seudominio.com.br"]`. |
| `CertificadoArquivo` | `C:\\NFeEmissorLocal\\certificado.pfx` |
| `CertificadoSenha` | Senha do .pfx. Prefira a variável de ambiente `EmissorLocal__CertificadoSenha`. |
| `CertificadoThumbprint` | Alternativa ao .pfx: certificado instalado no Windows, inclusive A3. |
| `PastaFila` | Vazio = `C:\ProgramData\NFeEmissorLocal\fila`. |
| `ApiCentralUrl` | `https://nfe-api.seudominio.com.br` |
| `ApiCentralToken` | Chave de app `nfeapp_...` com acesso à empresa (tela **Apps integrados**). |
| `IntervaloTransmissaoSegundos` | `60` |

Teste no terminal (`cmd`), na pasta:

```
NFeEmissorLocal.exe
```

Em outro terminal: `curl http://127.0.0.1:5240/health` deve mostrar `"status":"ok"` e o
CNPJ do certificado. Depois instale como serviço (PowerShell como administrador):

```powershell
sc.exe create NFeEmissorLocal binPath= "C:\NFeEmissorLocal\NFeEmissorLocal.exe" start= auto
sc.exe failure NFeEmissorLocal reset= 86400 actions= restart/5000
sc.exe start NFeEmissorLocal
```

Se faltar alguma configuração, o programa não sobe e lista o que falta.

> Em Linux, o mesmo programa roda com `dotnet NFeEmissorLocal.dll` (publique com
> `-r linux-x64`) e pode virar um serviço systemd.

## 2. Endpoints

Todos os `/api/...` exigem `Authorization: Bearer <Token>`. Respostas em JSON (camelCase).

### Emitir em contingência

```
POST http://127.0.0.1:5240/api/empresas/{empresaId}/nfe?contingencia=offline
(mesmo corpo da emissão na API central, com "modelo": 65)
```

`empresaId` é o **mesmo id da empresa na API central**. Ele é usado depois para
transmitir. Resposta **200**:

```json
{
  "chaveAcesso": "3526...", "numero": 9, "serie": 1, "ambiente": 2,
  "status": "contingencia", "contingenciaOffline": true, "situacao": "pendente",
  "dataEmissao": "2026-11-02T17:10:00-03:00", "prazoTransmissao": "2026-11-03T17:10:00-03:00",
  "prazoVencido": false, "valorTotal": 11.00,
  "qrCode": "https://...qrcode?p=...|3|2|02|11.00|||<assinatura>",
  "urlChave": "https://.../NFCeConsultaPublica",
  "xmlBase64": "<NFe assinada com infNFeSupl>", "jaExistia": false
}
```

- **O mesmo número devolve a mesma nota** (`jaExistia: true`). Repetir o pedido é
  seguro: não cria uma segunda nota para o número.
- Se a nota anterior desse número foi **rejeitada**, uma nova emissão gera outra nota.
  A antiga vai para `fila/historico`.
- `400`: dados inválidos (lista em `problemas`). `409`: faltou `?contingencia=offline`.
  `503`: certificado ausente ou vencido. `401`: token errado.

### Acompanhar e transmitir

| Chamada | Para quê |
|---|---|
| `GET /api/contingencia/pendentes` | Notas ainda não autorizadas: pendentes, rejeitadas e denegadas. `?todas=true` inclui as autorizadas. |
| `GET /api/contingencia/{chave}` | Uma nota, com `xmlBase64` e, depois de autorizada, `xmlAutorizadoBase64`, `protocolo` e `documentoId` (id na API central). |
| `POST /api/contingencia/transmitir` | Transmite agora, sem esperar o intervalo. |
| `GET /health` | Certificado, pendentes, prazos vencidos e última transmissão. Sem token. |

`situacao`:

- `pendente`: aguardando transmissão.
- `autorizada`: autorizada pela SEFAZ.
- `rejeitada`: corrija os dados e emita de novo.
- `denegada`: o número não pode ser reaproveitado.

A transmissão automática roda a cada `IntervaloTransmissaoSegundos`. Falta de internet,
API central fora do ar ou SEFAZ indisponível mantêm a nota `pendente` para a próxima
rodada.

## 3. O que o caixa (Facil) faz

1. Tenta a emissão normal na API central. Se não houver resposta em ~10 s, ou se não
   houver internet, chama o emissor local com `?contingencia=offline`, **com o mesmo número**.
2. Imprime o DANFE NFC-e com o `qrCode` e a `urlChave` da resposta. Na contingência, o
   cupom traz a mensagem "EMITIDA EM CONTINGÊNCIA" e a via do consumidor.
3. Guarda a chave na venda. Quando a internet voltar, consulta
   `GET /api/contingencia/{chave}` até `situacao` = `autorizada` e sincroniza o XML
   autorizado e o `documentoId` com o servidor.
4. Se a nota de contingência for `rejeitada`, corrige e emite de novo.

> **Se a emissão on-line respondeu depois**, a mesma venda pode ter ficado com duas
> notas autorizadas: a on-line e a de contingência. Cancele a de contingência com
> `POST /api/nfe/{id}/cancelar-substituicao` na API central, informando a chave da
> on-line como `chaveSubstituta`.

## 4. Segurança

- O emissor escuta só em `127.0.0.1`: outros computadores da rede não o alcançam.
- Toda chamada exige o `Token`. Sem ele, uma página qualquer aberta no navegador do
  caixa poderia emitir notas.
- O CORS libera só as origens de `OrigensPermitidas`. O emissor responde ao
  "preflight de rede privada" do Chrome e do Edge
  (`Access-Control-Allow-Private-Network`), exigido quando um site `https://` chama o
  `127.0.0.1`.
- O certificado e a senha ficam só no caixa. A senha não deve ir para o
  `appsettings.json` se o computador for compartilhado: use a variável de ambiente.
