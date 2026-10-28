import { createHash } from "node:crypto"
import { type NextRequest, NextResponse } from "next/server"
import { COOKIE_SESSAO, verificarSessao } from "@/lib/auth-session"
import {
  caminhoConfig,
  isPreviewApiFixa,
  definirApiToken,
  definirApiUrl,
  resolverApiBase,
  resolverApiToken,
  urlValida,
} from "@/lib/config-runtime"

/**
 * Configuracao do destino da API .NET pela propria interface do painel.
 *
 * GET: estado atual (URL efetiva + origem, presenca/fingerprint do token).
 * POST: define URL e/ou token sobrepostos; em preview, a URL de teste fica fixa.
 * DELETE: limpa os overrides e volta a usar as variaveis de ambiente.
 *
 * Seguranca: exige sessao valida do painel; NUNCA devolve o token — apenas
 * comprimento e um fingerprint SHA-256 (8 hex), irreversivel.
 */

function fingerprint(valor: string): string {
  return createHash("sha256").update(valor, "utf8").digest("hex").slice(0, 8)
}

async function estadoAtual() {
  const base = await resolverApiBase()
  const tok = await resolverApiToken()
  return {
    apiUrl: base.url,
    apiUrlOrigem: base.origem,
    apiUrlFixaPreview: isPreviewApiFixa(),
    tokenPresente: tok.token.length > 0,
    tokenLength: tok.token.length,
    tokenFingerprint: tok.token ? fingerprint(tok.token) : null,
    tokenOrigem: tok.origem,
    arquivo: caminhoConfig(),
  }
}

async function exigirSessao(req: NextRequest) {
  const sessao = await verificarSessao(req.cookies.get(COOKIE_SESSAO)?.value)
  if (!sessao) {
    return NextResponse.json(
      { erro: "Sessao expirada ou ausente." },
      { status: 401, headers: { "x-sessao-expirada": "1" } },
    )
  }
  return null
}

export async function GET(req: NextRequest) {
  const bloqueio = await exigirSessao(req)
  if (bloqueio) return bloqueio
  return NextResponse.json(await estadoAtual())
}

export async function POST(req: NextRequest) {
  const bloqueio = await exigirSessao(req)
  if (bloqueio) return bloqueio

  let corpo: { apiUrl?: unknown; apiToken?: unknown }
  try {
    corpo = (await req.json()) as typeof corpo
  } catch {
    return NextResponse.json({ erro: "Corpo JSON invalido." }, { status: 400 })
  }

  // Em preview, todas as chamadas usam a API de teste sem gravar sobrescritas.
  if (!isPreviewApiFixa() && corpo.apiUrl !== undefined) {
    if (corpo.apiUrl === null || corpo.apiUrl === "") {
      await definirApiUrl(null)
    } else if (typeof corpo.apiUrl === "string" && urlValida(corpo.apiUrl)) {
      await definirApiUrl(corpo.apiUrl)
    } else {
      return NextResponse.json(
        { erro: "URL invalida. Use http:// ou https:// (ex.: https://facil-api.x-star.com.br)." },
        { status: 400 },
      )
    }
  }

  // Token: string nao vazia define; string vazia ou null limpa o override.
  if (corpo.apiToken !== undefined) {
    if (corpo.apiToken === null || corpo.apiToken === "") {
      await definirApiToken(null)
    } else if (typeof corpo.apiToken === "string") {
      await definirApiToken(corpo.apiToken)
    } else {
      return NextResponse.json({ erro: "Token invalido." }, { status: 400 })
    }
  }

  try {
    return NextResponse.json(await estadoAtual())
  } catch (e) {
    return NextResponse.json(
      { erro: e instanceof Error ? e.message : "Falha ao gravar configuracao." },
      { status: 500 },
    )
  }
}

export async function DELETE(req: NextRequest) {
  const bloqueio = await exigirSessao(req)
  if (bloqueio) return bloqueio
  await definirApiUrl(null)
  await definirApiToken(null)
  return NextResponse.json(await estadoAtual())
}
