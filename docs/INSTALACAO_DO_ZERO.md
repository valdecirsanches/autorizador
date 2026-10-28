# Instalação do zero (servidor próprio, sem Vercel)

Roteiro para montar **API .NET + painel Next.js** num servidor Linux (Ubuntu),
os dois na mesma máquina, a partir deste repositório.

```
Navegador ──HTTPS──> nginx ──> painel Next.js (127.0.0.1:3000) ──> API .NET (127.0.0.1:5240) ──> MySQL
Apps integrados ──HTTPS──> nginx ──> API .NET (127.0.0.1:5240)
```

## 0. Antes de apagar o que existe

1. **Banco MySQL: NÃO apague.** Ele guarda as empresas, os **certificados A1
   cifrados**, as notas e o histórico fiscal (guarda obrigatória por lei). Faça
   um backup mesmo assim:
   ```bash
   mysqldump -u root -p --single-transaction --routines nfe > backup-nfe-$(date +%F).sql
   ```
2. **Guarde a `NFE_CERT_MASTER_KEY` atual.** Os certificados no banco só abrem
   com ela. Se você trocar a chave, terá de reenviar todos os A1.
3. **Guarde a pasta de XMLs** (`NFE_XML_DIR`, ex.: `/var/lib/aprovador-nfe/xmls`).
4. Copie os arquivos de variáveis atuais (`/etc/aprovador-nfe.env`, `.env.local`)
   para um lugar seguro, fora do servidor.

## 1. Pré-requisitos

```bash
sudo apt update
sudo apt install -y git nginx mysql-client
# .NET 9 SDK
sudo apt install -y dotnet-sdk-9.0      # ou siga https://learn.microsoft.com/dotnet/core/install/linux-ubuntu
# Node.js 20+ (exemplo com NodeSource)
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt install -y nodejs
```

## 2. Clonar

```bash
cd ~
git clone https://github.com/valdecirsanches/autorizador
cd autorizador
```

## 3. API .NET

```bash
cd backend
dotnet build                 # tem que terminar sem erros
dotnet test                  # testes
sudo bash deploy/instalar-servico.sh
```

Na primeira vez o instalador cria `/etc/aprovador-nfe.env`. Preencha
(`sudo nano /etc/aprovador-nfe.env`):

| Variável | Valor |
|---|---|
| `MYSQL_CONNECTION_STRING` | conexão com o banco existente |
| `NFE_CERT_MASTER_KEY` | **a mesma de antes** (passo 0.2) |
| `NFE_API_TOKEN` | token mestre **novo**: `openssl rand -base64 48` |
| `NFE_CORS_ORIGINS` | endereço do painel, ex.: `https://painel.seudominio.com.br` |
| `NFE_XML_DIR` | pasta dos XMLs (passo 0.3) |

Depois rode o instalador de novo e confira:

```bash
sudo bash deploy/instalar-servico.sh
curl http://127.0.0.1:5240/health      # deve mostrar "versao": "1.1.0"
journalctl -u aprovador-nfe -f         # logs
```

## 4. Painel Next.js

Na **raiz** do projeto:

```bash
cd ~/autorizador
sudo bash deploy/painel/instalar-painel.sh
```

Na primeira vez ele cria `/etc/aprovador-painel.env` e para. Preencha
(`sudo nano /etc/aprovador-painel.env`):

| Variável | Valor |
|---|---|
| `NFE_API_URL` | `http://127.0.0.1:5240` |
| `NFE_API_TOKEN` | **o mesmo** token mestre do passo 3 |
| `PAINEL_ADMIN_USUARIO` / `PAINEL_ADMIN_SENHA` | login do painel (senha nova e forte) |
| `PAINEL_SESSION_SECRET` | `openssl rand -base64 48` |

Rode o instalador de novo. O painel sobe em `http://127.0.0.1:3000`.

## 5. nginx + HTTPS

`/etc/nginx/sites-available/autorizador`:

```nginx
server {
    server_name painel.seudominio.com.br;
    client_max_body_size 5m;            # upload de logo e certificado
    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

server {
    server_name api.seudominio.com.br;  # usado pelos apps integrados
    client_max_body_size 5m;
    location / {
        proxy_pass http://127.0.0.1:5240;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 120s;        # SEFAZ pode demorar
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/autorizador /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d painel.seudominio.com.br -d api.seudominio.com.br
```

## 6. Conferir

1. Abra o painel, faça login e veja o **rodapé da barra lateral**:
   `Painel v0.2.x` e `API v1.1.0`.
2. **Empresas**: as empresas e certificados de antes aparecem (mesmo banco).
3. **Apps integrados**: crie uma chave para cada sistema que emite notas e troque
   nos sistemas o token antigo pela chave nova (ver `docs/CHAVES_POR_APP.md`).

## Atualizar depois (cada deploy)

```bash
cd ~/autorizador && git pull
(cd backend && sudo bash deploy/instalar-servico.sh)
sudo bash deploy/painel/instalar-painel.sh
```
