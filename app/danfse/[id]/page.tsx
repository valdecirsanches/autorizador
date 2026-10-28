"use client"

import { use, useEffect, useState } from "react"
import { ArrowLeft, Download, AlertTriangle, ExternalLink } from "lucide-react"
import { Button } from "@/components/ui/button"

/**
 * Viewer do DANFSe. Diferente do DANFE (renderizado no cliente a partir do
 * XML), o DANFSe ja sai pronto em PDF do backend (QuestPDF). Aqui apenas
 * buscamos o PDF pelo proxy same-origin e exibimos num <iframe> via blob URL,
 * com botoes de download e abertura em nova aba.
 */
export default function DanfsePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params)
  const [url, setUrl] = useState<string | null>(null)
  const [erro, setErro] = useState<string | null>(null)
  const [carregando, setCarregando] = useState(true)

  useEffect(() => {
    let objectUrl: string | null = null
    const ctrl = new AbortController()

    async function carregar() {
      setCarregando(true)
      setErro(null)
      try {
        const resp = await fetch(`/api/proxy/nfse/${id}/danfse.pdf`, { cache: "no-store", signal: ctrl.signal })
        if (!resp.ok) {
          let msg = `Erro ${resp.status}`
          try {
            const j = await resp.json()
            if (j?.erro) msg = String(j.erro)
          } catch {
            // resposta sem JSON: mantem a mensagem padrao
          }
          throw new Error(msg)
        }
        const blob = await resp.blob()
        objectUrl = URL.createObjectURL(blob)
        setUrl(objectUrl)
      } catch (e) {
        if ((e as Error).name === "AbortError") return
        setErro(e instanceof Error ? e.message : "Falha ao carregar o DANFSe.")
      } finally {
        setCarregando(false)
      }
    }

    void carregar()
    return () => {
      ctrl.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [id])

  return (
    <main className="flex min-h-screen flex-col bg-muted/40">
      <div className="sticky top-0 z-10 flex items-center justify-between border-b border-border bg-background px-4 py-3">
        <Button render={<a href="/" />} nativeButton={false} variant="ghost" size="sm">
          <ArrowLeft data-icon="inline-start" />
          Voltar ao painel
        </Button>
        <div className="flex items-center gap-2">
          <span className="hidden text-sm text-muted-foreground sm:inline">
            DANFSe — documento auxiliar da NFS-e
          </span>
          <Button
            render={<a href={url ?? "#"} target="_blank" rel="noopener noreferrer" />}
            nativeButton={false}
            variant="outline"
            size="sm"
            disabled={!url}
          >
            <ExternalLink data-icon="inline-start" />
            Abrir em nova aba
          </Button>
          <Button
            render={<a href={url ?? "#"} download={`DANFSe-${id}.pdf`} />}
            nativeButton={false}
            size="sm"
            disabled={!url}
          >
            <Download data-icon="inline-start" />
            Baixar PDF
          </Button>
        </div>
      </div>

      <div className="flex flex-1 flex-col p-4">
        {carregando && <p className="text-center text-sm text-muted-foreground">Gerando DANFSe…</p>}

        {erro && (
          <div className="mx-auto flex max-w-lg flex-col items-center gap-2 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-6 text-center">
            <AlertTriangle className="size-6 text-destructive" />
            <p className="text-sm font-medium text-destructive">Não foi possível gerar o DANFSe</p>
            <p className="text-sm text-muted-foreground">{erro}</p>
            <p className="text-xs text-muted-foreground">
              O DANFSe só existe para notas de serviço autorizadas (ou canceladas, com a tarja correspondente).
            </p>
          </div>
        )}

        {url && (
          <iframe
            src={url}
            title="DANFSe"
            className="min-h-[80vh] w-full flex-1 rounded-md border border-border bg-background"
          />
        )}
      </div>
    </main>
  )
}
