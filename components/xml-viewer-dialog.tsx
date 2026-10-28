"use client"

import { useState } from "react"
import useSWR from "swr"
import { Check, Copy, Download, RefreshCw } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { salvarTexto } from "@/lib/baixar"

/** XML a exibir no visualizador (null = fechado). */
export type XmlView = { titulo: string; url: string; nomeArquivo: string }

/** Fetcher que devolve o corpo como TEXTO (os endpoints de XML respondem application/xml). */
const textFetcher = (url: string) =>
  fetch(url, { cache: "no-store" }).then((r) => {
    if (!r.ok) throw new Error(`Erro ${r.status}`)
    return r.text()
  })

/** Indenta um XML "achatado" para leitura humana. Puramente cosmetico. */
export function formatarXml(xml: string): string {
  const semQuebras = xml.replace(/>\s+</g, "><").trim()
  const comQuebras = semQuebras.replace(/(>)(<)(\/*)/g, "$1\n$2$3")
  let nivel = 0
  return comQuebras
    .split("\n")
    .map((linha) => {
      if (linha.match(/^<\/\w/) && nivel > 0) nivel -= 1
      const recuada = "  ".repeat(nivel) + linha
      if (linha.match(/^<\w[^>]*[^/]>.*$/) && !linha.match(/<\/\w/)) nivel += 1
      return recuada
    })
    .join("\n")
}

/**
 * Visualizador inline de XML (NF-e, NFS-e, resposta da SEFAZ). Mostra o
 * conteudo na tela, com copiar e baixar dentro do dialogo — o download via
 * <a download> nao funciona dentro de preview/iframe.
 */
export function DialogXml({ view, onFechar }: { view: XmlView | null; onFechar: () => void }) {
  const aberto = view !== null
  const { data, error, isLoading } = useSWR<string>(aberto ? view.url : null, textFetcher)
  const [copiado, setCopiado] = useState(false)
  const formatado = data ? formatarXml(data) : ""

  async function copiar() {
    if (!data) return
    try {
      await navigator.clipboard.writeText(data)
      setCopiado(true)
      setTimeout(() => setCopiado(false), 2000)
    } catch {
      toast.error("Não foi possível copiar para a área de transferência.")
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o ? onFechar() : undefined)}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{view?.titulo}</DialogTitle>
          <DialogDescription>
            Conteúdo do XML exibido na tela para análise. Use os botões para copiar ou baixar.
          </DialogDescription>
        </DialogHeader>

        {isLoading ? (
          <div className="flex items-center gap-2 py-10 text-sm text-muted-foreground">
            <RefreshCw className="size-4 animate-spin" />
            Carregando XML…
          </div>
        ) : error ? (
          <div className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2.5 text-sm text-destructive">
            Não foi possível carregar o XML: {error instanceof Error ? error.message : "erro"}
          </div>
        ) : (
          <pre className="max-h-[55vh] overflow-auto rounded-md border border-border bg-muted/40 p-3 font-mono text-xs leading-relaxed text-foreground">
            {formatado}
          </pre>
        )}

        <DialogFooter>
          <Button variant="outline" onClick={copiar} disabled={!data}>
            {copiado ? <Check data-icon="inline-start" /> : <Copy data-icon="inline-start" />}
            {copiado ? "Copiado" : "Copiar"}
          </Button>
          <Button
            variant="outline"
            onClick={() => data && view && salvarTexto(data, view.nomeArquivo, "application/xml")}
            disabled={!data}
          >
            <Download data-icon="inline-start" />
            Baixar arquivo
          </Button>
          <Button onClick={onFechar}>Fechar</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
