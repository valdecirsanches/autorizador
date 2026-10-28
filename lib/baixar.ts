/**
 * Downloads pelo navegador via Blob + object URL. Funciona dentro de iframes e
 * previews, onde <a href download> costuma ser ignorado.
 */

function dispararDownload(blob: Blob, nomeArquivo: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement("a")
  a.href = url
  a.download = nomeArquivo
  document.body.appendChild(a)
  a.click()
  a.remove()
  // Da tempo do navegador iniciar o download antes de liberar a URL.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** Salva um texto ja carregado (ex.: XML exibido na tela). */
export function salvarTexto(conteudo: string, nomeArquivo: string, tipo = "text/plain") {
  dispararDownload(new Blob([conteudo], { type: tipo }), nomeArquivo)
}

/** Nome do arquivo informado pelo servidor em Content-Disposition, se houver. */
function nomeDoCabecalho(cabecalho: string | null): string | null {
  if (!cabecalho) return null
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(cabecalho)
  if (utf8) {
    try {
      return decodeURIComponent(utf8[1].trim())
    } catch {
      // cai para o filename simples
    }
  }
  const simples = /filename="?([^";]+)"?/i.exec(cabecalho)
  return simples ? simples[1].trim() : null
}

/**
 * Baixa um arquivo do proxy da API. Lanca Error com a mensagem do servidor
 * quando a resposta nao e 2xx, para a tela mostrar o motivo.
 */
export async function baixarArquivo(url: string, nomePadrao: string): Promise<void> {
  const resp = await fetch(url, { cache: "no-store" })
  if (!resp.ok) {
    let motivo = `Erro ${resp.status}`
    try {
      const dado = (await resp.json()) as { erro?: string; detalhe?: string }
      motivo = [dado.erro, dado.detalhe].filter(Boolean).join(" — ") || motivo
    } catch {
      // corpo nao-JSON: mantem o status
    }
    throw new Error(motivo)
  }
  const blob = await resp.blob()
  dispararDownload(blob, nomeDoCabecalho(resp.headers.get("content-disposition")) ?? nomePadrao)
}
