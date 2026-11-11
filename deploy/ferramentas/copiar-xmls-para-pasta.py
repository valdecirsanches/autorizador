#!/usr/bin/env python3
"""
Copia os XMLs AUTORIZADOS (nfeProc, producao) de uma empresa para a "Pasta de
XMLs" configurada no painel, no mesmo padrao que a API usa:

    {destino}/{periodo}/NFe/{chave}-nfe.xml

O {periodo} segue o formato das pastas que ja existem no destino (202610,
NFe202610, 20261028 ou NFe20261028); sem nenhuma, usa AAAAMM. A data usada e a de
EMISSAO (dhEmi).

Seguro: so COPIA. Nunca apaga, nunca sobrescreve e pula a nota cuja chave ja
existe em qualquer lugar do destino. Sem --executar, apenas simula e mostra o
que faria.

Uso (no servidor da API):
    python3 copiar-xmls-para-pasta.py --cnpj 03707154000106 \\
        --destino /home/sanches/levpao/NFe --desde 2026-11-05     # simula
    python3 copiar-xmls-para-pasta.py --cnpj 03707154000106 \\
        --destino /home/sanches/levpao/NFe --desde 2026-11-05 --executar   # copia

Origens padrao (as que existirem): pastas de XML da API (NFE_XML_DIR atual e
antigas). Acrescente outras com --origem /caminho (pode repetir).
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

NS = "{http://www.portalfiscal.inf.br/nfe}"
CHAVE_RE = re.compile(r"(\d{44})")
PERIODO_RE = re.compile(r"^(?P<pre>nfe)?(?P<mes>(?:19|20)\d{2}(?:0[1-9]|1[0-2]))(?P<dia>\d{2})?$", re.I)

ORIGENS_PADRAO = [
    "~/apps/autorizador/backend/nfe-xml",
    "~/apps/autorizador/backend/xmls",
    "~/apps/autorizador/backend/src/NFe.Api/xmls",
    "~/v0/aprovador/backend/nfe-xml",
    "~/v0/aprovador/backend/xmls",
]


def ler_nota(caminho):
    """Devolve (chave, cnpj, tpAmb, cStat, dhRecbto, dhEmi, texto) de um nfeProc, ou None."""
    try:
        if os.path.getsize(caminho) > 5 * 1024 * 1024:
            return None
        with open(caminho, "rb") as f:
            dados = f.read()
        raiz = ET.fromstring(dados)
    except (OSError, ET.ParseError):
        return None
    if raiz.tag != NS + "nfeProc":
        return None
    inf = raiz.find(f"{NS}NFe/{NS}infNFe")
    prot = raiz.find(f"{NS}protNFe/{NS}infProt")
    if inf is None or prot is None:
        return None
    chave = (inf.get("Id") or "")[3:]
    if len(chave) != 44:
        return None

    def t(el, caminho_tag):
        e = el.find(caminho_tag)
        return (e.text or "").strip() if e is not None else ""

    return {
        "chave": chave,
        "cnpj": t(inf, f"{NS}emit/{NS}CNPJ"),
        "tpAmb": t(inf, f"{NS}ide/{NS}tpAmb"),
        "cStat": t(prot, f"{NS}cStat"),
        "dhRecbto": t(prot, f"{NS}dhRecbto"),
        "dhEmi": t(inf, f"{NS}ide/{NS}dhEmi"),
        "dados": dados,
    }


def estilo_do_destino(destino):
    """(raiz, prefixo, por_dia) a partir das pastas de periodo que ja existem."""
    candidatas = [destino, os.path.join(destino, "NFe")]
    for raiz in candidatas:
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
    for base, _dirs, arquivos in os.walk(destino):
        for a in arquivos:
            if a.lower().endswith(".xml"):
                m = CHAVE_RE.search(a)
                if m:
                    achadas.add(m.group(1))
    return achadas


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cnpj", required=True, help="CNPJ do emitente (so digitos)")
    ap.add_argument("--destino", required=True, help="Pasta de XMLs configurada no painel")
    ap.add_argument("--origem", action="append", default=[], help="Pasta extra onde procurar (repetivel)")
    ap.add_argument("--mes", help="So notas emitidas neste mes, AAAAMM (ex.: 202611)")
    ap.add_argument("--desde", help="So notas emitidas a partir desta data, AAAA-MM-DD (ex.: 2026-11-05)")
    ap.add_argument("--executar", action="store_true", help="Copia de verdade (sem isto, so simula)")
    a = ap.parse_args()

    cnpj = re.sub(r"\D", "", a.cnpj)
    destino = os.path.abspath(os.path.expanduser(a.destino))
    if len(cnpj) != 14:
        sys.exit("CNPJ invalido.")
    if a.mes and not re.fullmatch(r"20\d{2}(0[1-9]|1[0-2])", a.mes):
        sys.exit("--mes deve ser AAAAMM, ex.: 202611.")
    if a.desde and not re.fullmatch(r"20\d{2}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])", a.desde):
        sys.exit("--desde deve ser AAAA-MM-DD, ex.: 2026-11-05.")
    if not os.path.isdir(destino):
        sys.exit(f"Destino nao existe: {destino}")

    origens = [os.path.expanduser(o) for o in ORIGENS_PADRAO + a.origem]
    origens = [os.path.abspath(o) for o in origens if os.path.isdir(o)]
    origens = [o for o in dict.fromkeys(origens) if not o.startswith(destino + os.sep) and o != destino]
    if not origens:
        sys.exit("Nenhuma pasta de origem encontrada. Informe com --origem /caminho.")

    raiz, prefixo, por_dia = estilo_do_destino(destino)
    ja_existem = chaves_no_destino(destino)
    print(f"Destino: {raiz}  (periodo: {prefixo}{'AAAAMMDD' if por_dia else 'AAAAMM'})")
    print(f"Ja existem no destino: {len(ja_existem)} chave(s)")
    print("Procurando em:\n  " + "\n  ".join(origens))

    vistas = set()
    copiar = []
    ignoradas = {"antes de --desde": 0, "outro mes": 0, "outro CNPJ": 0, "homologacao": 0, "nao autorizada": 0, "ja no destino": 0, "repetida": 0}
    for origem in origens:
        for base, _dirs, arquivos in os.walk(origem):
            for nome in arquivos:
                if not nome.lower().endswith(".xml"):
                    continue
                n = ler_nota(os.path.join(base, nome))
                if n is None:
                    continue
                if n["cnpj"] != cnpj:
                    ignoradas["outro CNPJ"] += 1
                elif n["tpAmb"] != "1":
                    ignoradas["homologacao"] += 1
                elif n["cStat"] not in ("100", "150"):
                    ignoradas["nao autorizada"] += 1
                elif a.desde and (n["dhEmi"] or n["dhRecbto"])[:10] < a.desde:
                    ignoradas["antes de --desde"] += 1
                elif a.mes and (n["dhEmi"] or n["dhRecbto"])[:7].replace("-", "") != a.mes:
                    ignoradas["outro mes"] += 1
                elif n["chave"] in ja_existem:
                    ignoradas["ja no destino"] += 1
                elif n["chave"] in vistas:
                    ignoradas["repetida"] += 1
                else:
                    vistas.add(n["chave"])
                    data = (n["dhEmi"] or n["dhRecbto"])[:10]  # AAAA-MM-DD no fuso do proprio XML
                    periodo = prefixo + data.replace("-", "")[: 8 if por_dia else 6]
                    alvo = os.path.join(raiz, periodo, "NFe", n["chave"] + "-nfe.xml")
                    copiar.append((alvo, n["dados"], os.path.join(base, nome)))

    print(f"\nA copiar: {len(copiar)} nota(s)")
    for k, v in ignoradas.items():
        if v:
            print(f"  ignoradas ({k}): {v}")
    for alvo, _d, origem in copiar[:10]:
        print(f"  {origem}\n    -> {alvo}")
    if len(copiar) > 10:
        print(f"  ... e mais {len(copiar) - 10}")

    if not a.executar:
        print("\nSIMULACAO: nada foi gravado. Rode de novo com --executar para copiar.")
        return

    gravadas = erros = 0
    for alvo, dados, _origem in copiar:
        try:
            os.makedirs(os.path.dirname(alvo), exist_ok=True)
            if os.path.exists(alvo):
                continue
            tmp = alvo + ".tmp"
            with open(tmp, "wb") as f:
                f.write(dados)
            os.replace(tmp, alvo)
            gravadas += 1
        except OSError as e:
            erros += 1
            print(f"ERRO {alvo}: {e}")
    print(f"\nGravadas: {gravadas}  Erros: {erros}")


if __name__ == "__main__":
    main()
