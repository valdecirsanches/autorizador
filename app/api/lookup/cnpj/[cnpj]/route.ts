import { NextResponse } from "next/server"

// APIs públicas (BrasilAPI/ViaCEP) ficam atrás de WAF que bloqueia requisições
// sem User-Agent de navegador (retorna 403). Enviamos cabeçalhos realistas.
const LOOKUP_HEADERS = {
  "User-Agent":
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36",
  Accept: "application/json",
}

/**
 * Busca dados cadastrais por CNPJ na BrasilAPI (público, sem chave). Roda no
 * servidor para evitar CORS e normalizar para o formulário de empresa.
 * O regime tributário é inferido de opcao_pelo_simples (1 = Simples, 3 = Normal).
 */
export async function GET(
  _req: Request,
  { params }: { params: Promise<{ cnpj: string }> },
) {
  const { cnpj: cnpjRaw } = await params
  const cnpj = (cnpjRaw ?? "").replace(/\D/g, "")

  if (cnpj.length !== 14) {
    return NextResponse.json({ erro: "CNPJ deve ter 14 dígitos." }, { status: 400 })
  }

  try {
    const resp = await fetch(`https://brasilapi.com.br/api/cnpj/v1/${cnpj}`, {
      cache: "no-store",
      headers: LOOKUP_HEADERS,
    })
    if (resp.status === 404) {
      console.log(`[painel] lookup/cnpj ${cnpj} -> 404 nao encontrado`)
      return NextResponse.json({ erro: "CNPJ não encontrado." }, { status: 404 })
    }
    if (!resp.ok) {
      console.error(`[painel] lookup/cnpj ${cnpj} -> BrasilAPI ${resp.status}`)
      return NextResponse.json(
        { erro: "Não foi possível consultar o CNPJ." },
        { status: 502 },
      )
    }

    const d = await resp.json()

    // Confere que a Receita devolveu exatamente o CNPJ pedido. Se divergir,
    // não devolvemos dados: preencher o formulário com CNPJ e razão social de
    // empresas diferentes é um risco fiscal grave.
    const cnpjRetornado = String(d.cnpj ?? d.estabelecimento?.cnpj ?? "").replace(/\D/g, "")
    if (cnpjRetornado && cnpjRetornado !== cnpj) {
      console.error(
        `[painel] lookup/cnpj divergência: pedido=${cnpj} retornado=${cnpjRetornado}`,
      )
      return NextResponse.json(
        { erro: "O serviço retornou um CNPJ diferente do pesquisado." },
        { status: 502 },
      )
    }

    // A BrasilAPI expõe codigo_municipio_ibge (7 dígitos). Como fallback,
    // buscamos o IBGE pelo CEP no ViaCEP se vier vazio.
    let codigoMunicipio = d.codigo_municipio_ibge
      ? String(d.codigo_municipio_ibge)
      : ""
    const cep = (d.cep ?? "").replace(/\D/g, "")
    if (!codigoMunicipio && cep.length === 8) {
      try {
        const via = await fetch(`https://viacep.com.br/ws/${cep}/json/`, {
          cache: "no-store",
          headers: LOOKUP_HEADERS,
        })
        if (via.ok) {
          const v = await via.json()
          if (!v?.erro && v?.ibge) codigoMunicipio = String(v.ibge)
        }
      } catch {
        // fallback silencioso: o usuário pode preencher o IBGE manualmente.
      }
    }

    const telefone = (d.ddd_telefone_1 ?? "").replace(/\D/g, "")

    // CRT sugerido: MEI (4) tem prioridade — a BrasilAPI expõe opcao_pelo_mei.
    // Optante do Simples => 1; caso contrário Regime Normal => 3. O usuário
    // pode ajustar no formulário; MEI foi a causa de CRT divergente no painel.
    const regimeTributario = d.opcao_pelo_mei ? 4 : d.opcao_pelo_simples ? 1 : 3

    return NextResponse.json({
      cnpj,
      razaoSocial: d.razao_social ?? "",
      nomeFantasia: d.nome_fantasia ?? "",
      regimeTributario,
      logradouro: d.logradouro ?? "",
      numero: d.numero ?? "",
      bairro: d.bairro ?? "",
      nomeMunicipio: d.municipio ?? "",
      uf: d.uf ?? "",
      cep,
      codigoMunicipio,
      telefone,
    })
  } catch {
    return NextResponse.json(
      { erro: "Falha ao consultar o serviço de CNPJ." },
      { status: 502 },
    )
  }
}
