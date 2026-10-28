import { createHash } from 'node:crypto'
import { type NextRequest, NextResponse } from 'next/server'
import { COOKIE_SESSAO, verificarSessao } from '@/lib/auth-session'
import { resolverApiBase, resolverApiToken } from '@/lib/config-runtime'

/** Mesma impressao digital do backend: SHA-256 -> 8 hex. Nao expoe o segredo. */
function fingerprintToken(valor: string): string {
  return createHash('sha256').update(valor, 'utf8').digest('hex').slice(0, 8)
}

/**
 * Proxy fino e same-origin para a API .NET (NFe.Api).
 *
 * Por que existe: no preview, o navegador do usuario nao alcanca o host
 * interno da API (127.0.0.1:5240). O servidor Next.js, porem, alcanca. Este
 * handler roda no servidor e encaminha a chamada, mantendo o front same-origin
 * e evitando CORS.
 *
 * O destino (URL) e o token vem de lib/config-runtime: o valor definido na tela
 * "Configuracao da API" tem prioridade; sem ele, caimos em NFE_API_URL /
 * NFE_API_TOKEN do ambiente. Por isso resolvemos por requisicao (nao em modulo).
 *
 * Seguranca: este handler roda no SERVIDOR. Ele (1) exige uma sessao valida do
 * painel e (2) injeta o token Bearer da API. Assim o token nunca chega ao
 * navegador — o browser so tem o cookie de sessao assinado.
 */

async function encaminhar(req: NextRequest, path: string[]) {
  // Defesa em profundidade: mesmo com o middleware, revalidamos a sessao aqui.
  const sessao = await verificarSessao(req.cookies.get(COOKIE_SESSAO)?.value)
  if (!sessao) {
    // x-sessao-expirada marca que o 401 e da sessao do PAINEL (nao do backend),
    // para o cliente so redirecionar ao login neste caso.
    return NextResponse.json(
      { erro: 'Sessao expirada ou ausente.' },
      { status: 401, headers: { 'x-sessao-expirada': '1' } },
    )
  }

  const { url: API_BASE } = await resolverApiBase()
  const { token } = await resolverApiToken()
  if (!token) {
    console.error('[v0] proxy: NFE_API_TOKEN nao definido no ambiente do painel.')
    return NextResponse.json(
      { erro: 'Painel mal configurado: NFE_API_TOKEN ausente no servidor.' },
      { status: 500 },
    )
  }

  const search = req.nextUrl.search
  const url = `${API_BASE}/api/${path.join('/')}${search}`

  // Repassa corpo e content-type para POST/PUT (inclui multipart de upload).
  const method = req.method
  const temCorpo = method !== 'GET' && method !== 'HEAD'

  const headers = new Headers()
  const contentType = req.headers.get('content-type')
  if (contentType) headers.set('content-type', contentType)
  // Token da API e identidade da sessão injetados server-side.
  headers.set('authorization', `Bearer ${token}`)
  headers.set('x-panel-user', sessao.sub)
  const clientIp = req.headers.get('x-forwarded-for') ?? req.headers.get('x-real-ip')
  if (clientIp) headers.set('x-panel-client-ip', clientIp.slice(0, 128))

  let body: BodyInit | undefined
  if (temCorpo) {
    // Usa o corpo bruto para preservar multipart/form-data intacto.
    body = await req.arrayBuffer()
  }

  const inicio = Date.now()
  const rota = `${method} /api/${path.join('/')}${search}`

  try {
    const resp = await fetch(url, {
      method,
      headers,
      body,
      // Nunca cachear: o painel precisa do estado atual do backend.
      cache: 'no-store',
    })

    const respBody = await resp.arrayBuffer()
    const ms = Date.now() - inicio

    // Transparencia: erros do backend (4xx/5xx) sao logados com o corpo da
    // resposta, para que a causa apareca nos logs em vez de sumir.
    if (!resp.ok) {
      const contentType = resp.headers.get('content-type') ?? ''
      let detalhe = ''
      try {
        const texto = new TextDecoder().decode(respBody)
        detalhe = contentType.includes('application/json')
          ? texto.slice(0, 500)
          : texto.slice(0, 200)
      } catch {
        detalhe = '<corpo binario>'
      }
      console.error(`[v0] proxy ${rota} -> ${resp.status} (${ms}ms) ${detalhe}`)
      // 401 do backend = token divergente. Logamos o fingerprint do token que
      // o painel ENVIOU para voce comparar com o "[auth] ... Fingerprint=" do
      // log de startup da API. Iguais => o problema nao e o token; diferentes
      // => painel e API estao com tokens distintos (confira o .env de cada um).
      if (resp.status === 401) {
        console.error(
          `[v0] proxy: 401 do backend. Fingerprint do token do PAINEL=${fingerprintToken(token)} ` +
            `(comprimento=${token.length}). Compare com o fingerprint no startup da API.`,
        )
      }
    } else {
      console.log(`[v0] proxy ${rota} -> ${resp.status} (${ms}ms)`)
    }

    return new NextResponse(
      method === 'HEAD' || resp.status === 204 || resp.status === 304 ? null : respBody,
      {
        status: resp.status,
        headers: {
          'content-type':
            resp.headers.get('content-type') ?? 'application/json',
          // Repassa o nome do arquivo (XML/PDF) definido pela API, se houver.
          ...(resp.headers.get('content-disposition')
            ? { 'content-disposition': resp.headers.get('content-disposition')! }
            : {}),
        },
      },
    )
  } catch (e) {
    const ms = Date.now() - inicio
    const causa = e instanceof Error ? e.message : String(e)
    console.error(`[v0] proxy ${rota} -> FALHA DE REDE (${ms}ms): ${causa}`)
    return NextResponse.json(
      {
        erro:
          'Nao foi possivel contatar a API .NET. Confirme que o NFe.Api esta ' +
          'em execucao e que NFE_API_URL aponta para ele.',
        detalhe: causa,
      },
      { status: 502 },
    )
  }
}

export async function GET(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function POST(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function PUT(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function DELETE(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function PATCH(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function OPTIONS(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}

export async function HEAD(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const { path } = await ctx.params
  return encaminhar(req, path)
}
