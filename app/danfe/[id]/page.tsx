"use client"

import { use, useMemo } from "react"
import useSWR from "swr"
import { ArrowLeft, Printer, AlertTriangle } from "lucide-react"
import { Button } from "@/components/ui/button"
import { DanfeDocumento } from "@/components/danfe/danfe-documento"
import { parseDanfe } from "@/lib/danfe/parse-danfe"

// Busca o XML autorizado (nfeProc) pelo proxy same-origin. Retorna TEXTO (o
// endpoint devolve XML, nao JSON), por isso um fetcher dedicado.
async function xmlFetcher(path: string): Promise<string> {
  const resp = await fetch(path, { cache: "no-store" })
  if (!resp.ok) {
    let msg = `Erro ${resp.status}`
    try {
      const j = await resp.json()
      if (j?.erro) msg = String(j.erro)
    } catch {
      // ignora: mantem a mensagem padrao
    }
    throw new Error(msg)
  }
  return resp.text()
}

export default function DanfePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params)
  const { data: xml, error, isLoading } = useSWR(`/api/proxy/nfe/${id}/xml`, xmlFetcher)

  const { dados, parseErro } = useMemo(() => {
    if (!xml) return { dados: null, parseErro: null as string | null }
    try {
      return { dados: parseDanfe(xml), parseErro: null }
    } catch (e) {
      return { dados: null, parseErro: e instanceof Error ? e.message : "Falha ao ler o XML." }
    }
  }, [xml])

  const mensagemErro = error?.message ?? parseErro

  return (
    <main className="min-h-screen bg-muted/40">
      {/* Barra de acoes — nao sai na impressao */}
      <div className="no-print sticky top-0 z-10 flex items-center justify-between border-b border-border bg-background px-4 py-3">
        <Button render={<a href="/" />} nativeButton={false} variant="ghost" size="sm">
          <ArrowLeft data-icon="inline-start" />
          Voltar ao painel
        </Button>
        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">DANFE — gerado a partir do XML autorizado</span>
          <Button size="sm" onClick={() => window.print()} disabled={!dados}>
            <Printer data-icon="inline-start" />
            Imprimir / Salvar PDF
          </Button>
        </div>
      </div>

      <div className="py-6">
        {isLoading && (
          <p className="text-center text-sm text-muted-foreground">Carregando XML autorizado…</p>
        )}

        {mensagemErro && (
          <div className="mx-auto flex max-w-lg flex-col items-center gap-2 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-6 text-center">
            <AlertTriangle className="size-6 text-destructive" />
            <p className="text-sm font-medium text-destructive">Não foi possível gerar o DANFE</p>
            <p className="text-sm text-muted-foreground">{mensagemErro}</p>
            <p className="text-xs text-muted-foreground">
              O DANFE só existe para notas autorizadas, que possuem o XML de distribuição (nfeProc).
            </p>
          </div>
        )}

        {dados && (
          <div className="danfe-page">
            <DanfeDocumento dados={dados} logoUrl={`/api/proxy/nfe/${id}/logo`} />
          </div>
        )}
      </div>
    </main>
  )
}
