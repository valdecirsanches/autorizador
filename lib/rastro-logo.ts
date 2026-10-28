"use client"

/**
 * Rastro do envio de logo: cada etapa (clique, seletor, arquivo, POST,
 * conferencia, lista, imagem) e registrada no log do servico do painel, sem
 * nada na tela. Para investigar um problema no servidor:
 *   journalctl -u nfe-app --since "15 min ago" | grep rastro-logo
 */

export type NivelRastro = "info" | "ok" | "aviso" | "erro"

function horaAgora() {
  const d = new Date()
  const p = (n: number, t = 2) => String(n).padStart(t, "0")
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}

export function rastrear(empresa: string, etapa: string, detalhe?: string, nivel: NivelRastro = "info") {
  const evento = { hora: horaAgora(), empresa, etapa, detalhe, nivel }
  try {
    void fetch("/api/rastro", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ...evento, pagina: location.href, navegador: navigator.userAgent }),
      keepalive: true,
    }).catch(() => undefined)
  } catch {
    // o rastro nunca pode atrapalhar o envio do logo
  }
}
