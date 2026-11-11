#!/usr/bin/env python3
"""
Grava na pasta configurada o XML autorizado (nfeProc) das NF-e/NFC-e de UMA
empresa, lendo DIRETO DO BANCO da API (nao depende de arquivo em disco):

    {destino}/{data de emissao}/NFe/{chave}-nfe.xml

A pasta da data segue o formato das que ja existem no destino (202611,
NFe202611, 20261105 ou NFe20261105); sem nenhuma, usa AAAAMM.

Seguro: so CRIA arquivos. Nunca apaga, nunca sobrescreve e pula a nota cuja
chave ja aparece em qualquer arquivo do destino. Sem --executar, so simula.
So producao, so autorizadas e canceladas (que tem nfeProc).

A conexao vem do MYSQL_CONNECTION_STRING do .env.local da API (a senha nao
aparece na tela). Precisa do cliente "mysql" instalado.

Uso, no servidor da API:
    python3 deploy/ferramentas/gravar-nfes-do-banco.py \\
        --cnpj 03707154000106 --destino /home/sanches/levpao/NFe --desde 2026-11-05
    (confira a lista e rode de novo com --executar)
"""
import argparse
import base64
import os
import re
import subprocess
import sys
from datetime import datetime, timedelta

PERIODO_RE = re.compile(r"^(?P<pre>nfe)?(?P<mes>(?:19|20)\d{2}(?:0[1-9]|1[0-2]))(?P<dia>\d{2})?$", re.I)
CHAVE_RE = re.compile(r"\d{44}")
FUSO_BRASILIA = timedelta(hours=-3)  # sem horario de verao desde 2019


def ler_conexao(env_path):
    try:
        with open(env_path, encoding="utf-8") as f:
            linhas = f.read().splitlines()
    except OSError as e:
        sys.exit(f"Nao consegui ler {env_path}: {e}")
    valor = None
    for l in linhas:
        l = l.strip()
        if l.startswith("MYSQL_CONNECTION_STRING="):
            valor = l.split("=", 1)[1].strip().strip('"').strip("'")
    if not valor:
        sys.exit(f"MYSQL_CONNECTION_STRING nao encontrada em {env_path}")
    partes = {}
    for item in valor.split(";"):
        if "=" in item:
            k, v = item.split("=", 1)
            partes[k.strip().lower().replace(" ", "")] = v.strip()

    def pegar(*nomes, padrao=None):
        for n in nomes:
            if n in partes and partes[n]:
                return partes[n]
        return padrao

    return {
        "host": pegar("server", "host", "datasource", "address", padrao="127.0.0.1"),
        "port": pegar("port", padrao="3306"),
        "db": pegar("database", "initialcatalog"),
        "user": pegar("userid", "user", "uid", "username"),
        "senha": pegar("password", "pwd", padrao=""),
    }


def consultar(con, sql):
    cmd = ["mysql", "-h", con["host"], "-P", str(con["port"]), "-u", con["user"],
           "--batch", "--raw", "--skip-column-names", con["db"], "-e", sql]
    amb = dict(os.environ, MYSQL_PWD=con["senha"])
    r = subprocess.run(cmd, capture_output=True, text=True, env=amb)
    if r.returncode != 0:
        sys.exit("Erro no mysql: " + r.stderr.strip())
    return [l.split("\t") for l in r.stdout.splitlines() if l.strip()]


def estilo_do_destino(destino):
    for raiz in (destino, os.path.join(destino, "NFe")):
        if not os.path.isdir(raiz):
            continue
        periodos = []
        for nome in os.listdir(raiz):
            m = PERIODO_RE.match(nome)
            if m and os.path.isdir(os.path.join(raiz, nome)):
                periodos.append((m.group("mes") + (m.group("dia") or "99"), m))
        if periodos:
            periodos.sort(key=lambda p: p[0], reverse=True)
            m = periodos[0][1]
            return raiz, (m.group("pre") or ""), m.group("dia") is not None
    return destino, "", False


def chaves_no_destino(destino):
    achadas = set()
    for _base, _dirs, arquivos in os.walk(destino):
        for a in arquivos:
            if a.lower().endswith(".xml"):
                m = CHAVE_RE.search(a)
                if m:
                    achadas.add(m.group(0))
    return achadas


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cnpj", required=True, help="CNPJ da empresa (so digitos)")
    ap.add_argument("--destino", required=True, help="Pasta de XMLs configurada no painel")
    ap.add_argument("--desde", help="So notas emitidas a partir desta data, AAAA-MM-DD")
    ap.add_argument("--env", default="~/apps/autorizador/backend/.env.local", help="Arquivo .env da API")
    ap.add_argument("--executar", action="store_true", help="Grava de verdade (sem isto, so simula)")
    a = ap.parse_args()

    cnpj = re.sub(r"\D", "", a.cnpj)
    if len(cnpj) != 14:
        sys.exit("CNPJ invalido.")
    destino = os.path.abspath(os.path.expanduser(a.destino))
    if not os.path.isdir(destino):
        sys.exit(f"Destino nao existe: {destino}")
    filtro_data = ""
    if a.desde:
        try:
            d0 = datetime.strptime(a.desde, "%Y-%m-%d") - FUSO_BRASILIA  # 00:00 de Brasilia em UTC
        except ValueError:
            sys.exit("--desde deve ser AAAA-MM-DD, ex.: 2026-11-05.")
        filtro_data = f" AND d.data_emissao >= '{d0:%Y-%m-%d %H:%M:%S}'"

    con = ler_conexao(os.path.expanduser(a.env))
    if not con["db"] or not con["user"]:
        sys.exit("MYSQL_CONNECTION_STRING sem Database ou User.")

    empresa = consultar(con, f"SELECT id, razao_social FROM empresa WHERE cnpj = '{cnpj}'")
    if not empresa:
        sys.exit(f"Empresa {cnpj} nao encontrada no banco {con['db']}.")
    print(f"Empresa: {empresa[0][1]} ({cnpj})")

    sql = (
        "SELECT d.chave_acesso, DATE_FORMAT(d.data_emissao, '%Y-%m-%d %H:%i:%s'), "
        "REPLACE(TO_BASE64(d.xml_protocolado), '\\n', '') "
        "FROM documento_fiscal d JOIN empresa e ON e.id = d.empresa_id "
        f"WHERE e.cnpj = '{cnpj}' AND d.ambiente = 1 AND d.status IN (100, 400) "
        "AND d.xml_protocolado IS NOT NULL AND d.chave_acesso IS NOT NULL"
        + filtro_data +
        " ORDER BY d.data_emissao"
    )
    linhas = consultar(con, sql)

    raiz, prefixo, por_dia = estilo_do_destino(destino)
    ja_existem = chaves_no_destino(destino)
    print(f"Destino: {raiz}  (pasta da data: {prefixo}{'AAAAMMDD' if por_dia else 'AAAAMM'})")
    print(f"Notas no banco{' desde ' + a.desde if a.desde else ''}: {len(linhas)}")

    gravar, existentes = [], 0
    for chave, data_utc, b64 in linhas:
        chave = chave.strip()
        if chave in ja_existem:
            existentes += 1
            continue
        local = datetime.strptime(data_utc, "%Y-%m-%d %H:%M:%S") + FUSO_BRASILIA
        periodo = prefixo + local.strftime("%Y%m%d" if por_dia else "%Y%m")
        alvo = os.path.join(raiz, periodo, "NFe", chave + "-nfe.xml")
        gravar.append((alvo, b64))

    print(f"Ja estavam na pasta: {existentes}")
    print(f"A gravar: {len(gravar)}")
    for alvo, _ in gravar[:10]:
        print("  " + alvo)
    if len(gravar) > 10:
        print(f"  ... e mais {len(gravar) - 10}")

    if not a.executar:
        print("\nSIMULACAO: nada foi gravado. Rode de novo com --executar para gravar.")
        return

    gravadas = erros = 0
    for alvo, b64 in gravar:
        try:
            xml = base64.b64decode(b64)
            if not xml.lstrip().startswith(b"<?xml"):
                xml = b'<?xml version="1.0" encoding="UTF-8"?>' + xml
            os.makedirs(os.path.dirname(alvo), exist_ok=True)
            if os.path.exists(alvo):
                continue
            tmp = alvo + ".tmp"
            with open(tmp, "wb") as f:
                f.write(xml)
            os.replace(tmp, alvo)
            gravadas += 1
        except (OSError, ValueError) as e:
            erros += 1
            print(f"ERRO {alvo}: {e}")
    print(f"\nGravadas: {gravadas}  Erros: {erros}")


if __name__ == "__main__":
    main()
