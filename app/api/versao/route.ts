import { NextResponse } from "next/server"
import { resolverApiBase } from "@/lib/config-runtime"

/**
 * Versao do painel (definida no build pelo next.config.mjs) e da API .NET
 * (lida do GET /health). Serve para o operador confirmar, na tela, que os dois
 * deploys pegaram.
 */
export const dynamic = "force-dynamic"

type HealthApi = {
  versao?: string
  commit?: string | null
  compiladaEm?: string | null
  iniciadaEm?: string | null
  recursos?: string[]
}

export async function GET() {
  const painel = {
    versao: process.env.NEXT_PUBLIC_APP_VERSION ?? "?",
    commit: process.env.NEXT_PUBLIC_APP_COMMIT || null,
    compiladoEm: process.env.NEXT_PUBLIC_APP_BUILD_DATE ?? null,
  }

  const { url } = await resolverApiBase()
  try {
    const resp = await fetch(`${url}/health`, {
      cache: "no-store",
      signal: AbortSignal.timeout(5000),
    })
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
    const h = (await resp.json()) as HealthApi
    return NextResponse.json({
      painel,
      api: {
        online: true,
        url,
        // API anterior a esta mudanca responde /health sem "versao".
        versao: h.versao ?? null,
        commit: h.commit ?? null,
        compiladaEm: h.compiladaEm ?? null,
        iniciadaEm: h.iniciadaEm ?? null,
        recursos: h.recursos ?? [],
      },
    })
  } catch (e) {
    return NextResponse.json({
      painel,
      api: { online: false, url, erro: e instanceof Error ? e.message : String(e) },
    })
  }
}
