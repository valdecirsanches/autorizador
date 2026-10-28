import { createHash } from 'node:crypto'
import { type NextRequest, NextResponse } from 'next/server'
import { COOKIE_SESSAO, verificarSessao } from '@/lib/auth-session'
import { resolverApiBase, resolverApiToken } from '@/lib/config-runtime'

/**
 * Diagnostico de conexao painel -> API .NET, feito INTEIRAMENTE no servidor do
 * painel. Existe para resolver o 401 "Nao autorizado." sem depender de comandos
 * no terminal: compara o token que o PAINEL carregou com o que a API carregou
 * (via /diag/auth) e ainda faz um teste real em /api/empresas.
 *
 * URL e token vem de lib/config-runtime (tela "Configuracao da API" > env).
 *
 * Seguranca: exige sessao valida do painel e NUNCA devolve o token — apenas o
 * comprimento e um fingerprint SHA-256 (8 hex), que nao e reversivel.
 */

function fingerprintToken(valor: string): string {
  return createHash('sha256').update(valor, 'utf8').digest('hex').slice(0, 8)
}

export async function GET(req: NextRequest) {
  const sessao = await verificarSessao(req.cookies.get(COOKIE_SESSAO)?.value)
  if (!sessao) {
    return NextResponse.json(
      { erro: 'Sessao expirada ou ausente.' },
      { status: 401, headers: { 'x-sessao-expirada': '1' } },
    )
  }

  const { url: API_BASE, origem: apiUrlOrigem } = await resolverApiBase()
  const { token } = await resolverApiToken()
  const painel = {
    apiUrl: API_BASE,
    apiUrlOrigem,
    tokenPresente: token.length > 0,
    tokenLength: token.length,
    tokenFingerprint: token ? fingerprintToken(token) : null,
  }

  // 1) A API esta no ar? (/health nunca exige token)
  let apiNoAr = false
  try {
    const r = await fetch(`${API_BASE}/health`, { cache: 'no-store' })
    apiNoAr = r.ok
  } catch {
    apiNoAr = false
  }

  // 2) Qual token a API carregou? (/diag/auth: so len + fingerprint)
  let apiToken: { tokenLength: number; tokenFingerprint: string } | null = null
  let diagDisponivel = false
  try {
    const r = await fetch(`${API_BASE}/diag/auth`, { cache: 'no-store' })
    if (r.ok) {
      diagDisponivel = true
      apiToken = (await r.json()) as {
        tokenLength: number
        tokenFingerprint: string
      }
    }
  } catch {
    // API antiga (sem /diag/auth) — seguimos so com o teste real.
  }

  // 3) Teste real: o token do painel e aceito pela API?
  let teste: { status: number; ok: boolean } | null = null
  if (token) {
    try {
      const r = await fetch(`${API_BASE}/api/empresas`, {
        headers: { authorization: `Bearer ${token}` },
        cache: 'no-store',
      })
      teste = { status: r.status, ok: r.ok }
    } catch {
      teste = null
    }
  }

  // 4) A QUAL banco a API esta conectada e quantas notas existem la? Resolve o
  //    caso "o MySQL que eu abri tem N notas, mas o painel mostra outra coisa":
  //    quase sempre a API aponta para outro MySQL/base. Passa a empresa
  //    selecionada para tambem contar as notas dela naquele banco.
  const empresaId = req.nextUrl.searchParams.get('empresaId')
  let banco: {
    host: string
    port: number
    database: string
    totalDocumentos: number
    documentosEmpresa: number | null
  } | null = null
  if (token) {
    try {
      const url = new URL(`${API_BASE}/diag/db`)
      if (empresaId) url.searchParams.set('empresaId', empresaId)
      const r = await fetch(url, {
        headers: { authorization: `Bearer ${token}` },
        cache: 'no-store',
      })
      if (r.ok) banco = await r.json()
    } catch {
      banco = null
    }
  }

  // Veredito legivel para a UI.
  let veredito: string
  if (!painel.tokenPresente) {
    veredito = 'NFE_API_TOKEN nao esta definido no ambiente do painel.'
  } else if (!apiNoAr) {
    veredito = `A API nao respondeu em ${API_BASE}/health. Confirme que o NFe.Api esta rodando e que NFE_API_URL aponta para ele.`
  } else if (teste?.ok) {
    veredito = 'Conexao OK. O token do painel foi aceito pela API.'
  } else if (diagDisponivel && apiToken) {
    veredito =
      apiToken.tokenFingerprint === painel.tokenFingerprint
        ? 'Os tokens do painel e da API SAO IGUAIS, mas a API recusou. Verifique se a API foi REINICIADA apos a ultima mudanca de .env.'
        : `Os tokens DIVERGEM: painel=${painel.tokenFingerprint} (len ${painel.tokenLength}) x API=${apiToken.tokenFingerprint} (len ${apiToken.tokenLength}). Ajuste o NFE_API_TOKEN do painel para o mesmo valor da API e reinicie o painel.`
  } else {
    veredito =
      'A API recusou o token (401). Atualize a API para expor /diag/auth ou compare os tokens dos dois .env manualmente.'
  }

  return NextResponse.json({ painel, apiNoAr, diagDisponivel, apiToken, teste, banco, veredito })
}
