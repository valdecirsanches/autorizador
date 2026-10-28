"use client"

import { useState } from "react"
import useSWR from "swr"
import { Check, Copy, FileCode } from "lucide-react"
import { toast } from "sonner"
import type { VariantProps } from "class-variance-authority"
import { ApiError, fetcher } from "@/lib/api"
import { Button, buttonVariants } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"

interface JsonRecebidoResponse {
  jsonRecebido: string | null
}

function formatarJson(json: string) {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}

export function JsonRecebidoDialog({
  path,
  label = "Ver JSON recebido",
  variant = "outline",
}: {
  path: string
  label?: string
  variant?: VariantProps<typeof buttonVariants>["variant"]
}) {
  const [aberto, setAberto] = useState(false)
  const [copiado, setCopiado] = useState(false)
  const { data, error, isLoading } = useSWR<JsonRecebidoResponse>(aberto ? path : null, fetcher)
  const json = data?.jsonRecebido

  async function copiarJson() {
    if (!json) return
    try {
      await navigator.clipboard.writeText(formatarJson(json))
      setCopiado(true)
      window.setTimeout(() => setCopiado(false), 1800)
    } catch {
      toast.error("Não foi possível copiar o JSON")
    }
  }

  return (
    <>
      <Button variant={variant} size="sm" onClick={() => setAberto(true)}>
        <FileCode data-icon="inline-start" />
        {label}
      </Button>
      <Dialog open={aberto} onOpenChange={setAberto}>
        <DialogContent className="max-w-3xl">
          <DialogHeader>
            <DialogTitle>JSON recebido</DialogTitle>
            <DialogDescription>
              Corpo original enviado pelo aplicativo emissor nesta tentativa.
            </DialogDescription>
          </DialogHeader>
          {json ? (
            <>
              <div className="flex justify-end">
                <Button variant="outline" size="sm" onClick={copiarJson}>
                  {copiado ? <Check data-icon="inline-start" /> : <Copy data-icon="inline-start" />}
                  {copiado ? "Copiado" : "Copiar JSON"}
                </Button>
              </div>
              <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted p-4 font-mono text-xs leading-relaxed">
                {formatarJson(json)}
              </pre>
            </>
          ) : error ? (
            <div
              role="alert"
              className="rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm"
            >
              <p className="font-medium text-destructive">
                Não foi possível carregar o JSON desta tentativa
                {error instanceof ApiError ? ` (HTTP ${error.status})` : "."}
              </p>
              {error instanceof Error && error.message !== "Falha ao processar a requisicao." && (
                <p className="mt-1 break-words text-xs text-muted-foreground">{error.message}</p>
              )}
            </div>
          ) : isLoading ? (
            <p className="text-sm text-muted-foreground">Carregando JSON recebido...</p>
          ) : data ? (
            <p className="text-sm text-muted-foreground">
              Este registro é anterior ao armazenamento do JSON e não tem payload disponível.
            </p>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  )
}
