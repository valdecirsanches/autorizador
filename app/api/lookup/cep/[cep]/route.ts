import { NextResponse } from "next/server"

// O ViaCEP fica atrás de WAF que bloqueia requisições sem User-Agent de
// navegador (retorna 403). Enviamos cabeçalhos realistas.
const LOOKUP_HEADERS = {
  "User-Agent":
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36",
  Accept: "application/json",
}

/**
 * Busca endereço por CEP no ViaCEP (público, sem chave). Roda no servidor para
 * evitar CORS e normalizar os campos para o formato do formulário de empresa.
 */
export async function GET(
  _req: Request,
  { params }: { params: Promise<{ cep: string }> },
) {
  const { cep: cepRaw } = await params
  const cep = (cepRaw ?? "").replace(/\D/g, "")

  if (cep.length !== 8) {
    return NextResponse.json({ erro: "CEP deve ter 8 dígitos." }, { status: 400 })
  }

  try {
    const resp = await fetch(`https://viacep.com.br/ws/${cep}/json/`, {
      cache: "no-store",
      headers: LOOKUP_HEADERS,
    })
    if (!resp.ok) {
      return NextResponse.json(
        { erro: "Não foi possível consultar o CEP." },
        { status: 502 },
      )
    }

    const d = await resp.json()
    if (d?.erro) {
      return NextResponse.json({ erro: "CEP não encontrado." }, { status: 404 })
    }

    return NextResponse.json({
      logradouro: d.logradouro ?? "",
      bairro: d.bairro ?? "",
      nomeMunicipio: d.localidade ?? "",
      uf: d.uf ?? "",
      codigoMunicipio: d.ibge ?? "",
    })
  } catch {
    return NextResponse.json(
      { erro: "Falha ao consultar o serviço de CEP." },
      { status: 502 },
    )
  }
}
