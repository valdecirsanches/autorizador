import { type NextRequest, NextResponse } from "next/server"

/**
 * Recebe os eventos do rastreador do logo (lib/rastro-logo.ts) e escreve no log
 * do servico do painel. Para ver no servidor:
 *   journalctl -u nfe-app --since "10 min ago" | grep rastro-logo
 * Protegido pela sessao do painel (middleware).
 */
export const dynamic = "force-dynamic"

const limpar = (v: unknown, max: number) =>
  String(v ?? "")
    .replace(/[\r\n\t]+/g, " ")
    .slice(0, max)

export async function POST(req: NextRequest) {
  try {
    const e = (await req.json()) as Record<string, unknown>
    console.log(
      `[rastro-logo] ${limpar(e.hora, 16)} ${limpar(e.nivel, 6).toUpperCase()} ` +
        `${limpar(e.empresa, 80)} | ${limpar(e.etapa, 120)}` +
        (e.detalhe ? ` | ${limpar(e.detalhe, 600)}` : "") +
        (e.etapa === "Clique no botão" ? ` | navegador=${limpar(e.navegador, 200)}` : ""),
    )
  } catch {
    // corpo invalido: ignora
  }
  return new NextResponse(null, { status: 204 })
}
