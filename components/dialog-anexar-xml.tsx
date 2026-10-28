"use client"

import { useState } from "react"
import { FolderSearch, RefreshCw, Upload } from "lucide-react"
import { toast } from "sonner"
import type { DocumentoFiscal } from "@/lib/types"
import { Button } from "@/components/ui/button"
import { Label } from "@/components/ui/label"
import { Textarea } from "@/components/ui/textarea"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"

/**
 * Anexa o XML (NFe assinada ou nfeProc) a uma nota autorizada que a API tem sem
 * XML (cadastrada pela consulta). A SEFAZ nao devolve o XML na consulta, entao ele
 * precisa vir do sistema que emitiu a nota. Depois de anexado, o DANFE e gerado.
 */
export function DialogAnexarXml({
  nota,
  aberto,
  onFechar,
  onAnexado,
}: {
  nota: DocumentoFiscal
  aberto: boolean
  onFechar: () => void
  onAnexado: () => void
}) {
  const [xml, setXml] = useState("")
  const [enviando, setEnviando] = useState(false)
  const [buscando, setBuscando] = useState(false)
  const ocupado = enviando || buscando

  async function lerArquivo(arquivo: File | undefined) {
    if (!arquivo) return
    setXml(await arquivo.text())
  }

  async function buscarNaPasta() {
    if (ocupado || !nota.chaveAcesso) return
    setBuscando(true)
    try {
      const resp = await fetch(`/api/proxy/empresas/${nota.empresaId}/nfe/xml/buscar-pasta`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ chaveAcesso: nota.chaveAcesso }),
      })
      const dado = (await resp.json().catch(() => ({}))) as { erro?: string; detalhe?: string; aviso?: string }
      if (!resp.ok) {
        toast.error("XML não encontrado na pasta", {
          description: [dado.erro, dado.detalhe].filter(Boolean).join(" — ") || `HTTP ${resp.status}`,
        })
        return
      }
      toast.success(dado.aviso ?? "XML encontrado na pasta e anexado. O DANFE já pode ser impresso.")
      onFechar()
      onAnexado()
    } catch (e) {
      toast.error("Falha ao buscar na pasta", { description: e instanceof Error ? e.message : undefined })
    } finally {
      setBuscando(false)
    }
  }

  async function confirmar() {
    if (!xml.trim() || ocupado || !nota.chaveAcesso) return
    setEnviando(true)
    try {
      const resp = await fetch(`/api/proxy/empresas/${nota.empresaId}/nfe/xml`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ chaveAcesso: nota.chaveAcesso, xml: xml.trim() }),
      })
      const dado = (await resp.json().catch(() => ({}))) as { erro?: string; detalhe?: string; aviso?: string }
      if (!resp.ok) {
        toast.error("Não foi possível anexar o XML", {
          description: [dado.erro, dado.detalhe].filter(Boolean).join(" — ") || `HTTP ${resp.status}`,
        })
        return
      }
      toast.success(dado.aviso ?? "XML anexado. O DANFE já pode ser impresso.")
      setXml("")
      onFechar()
      onAnexado()
    } catch (e) {
      toast.error("Falha ao anexar o XML", { description: e instanceof Error ? e.message : undefined })
    } finally {
      setEnviando(false)
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !ocupado ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Anexar XML · Nota nº {nota.numero || "—"}</DialogTitle>
          <DialogDescription>
            A consulta à SEFAZ devolve só o protocolo, não o XML. Envie o XML desta nota (NFe assinada ou
            nfeProc) para a API guardá-lo e gerar o DANFE. A chave, a assinatura, o emitente e o protocolo são
            conferidos.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-2">
            <Label htmlFor="arquivo-xml">Arquivo .xml</Label>
            <input
              id="arquivo-xml"
              type="file"
              accept=".xml,text/xml,application/xml"
              disabled={ocupado}
              onChange={(e) => void lerArquivo(e.target.files?.[0])}
              className="text-sm file:mr-3 file:rounded-md file:border file:border-border file:bg-muted file:px-3 file:py-1.5 file:text-sm"
            />
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor="texto-xml">Ou cole o conteúdo do XML</Label>
            <Textarea
              id="texto-xml"
              value={xml}
              onChange={(e) => setXml(e.target.value)}
              rows={8}
              className="font-mono text-xs"
              placeholder="<nfeProc ...> ou <NFe ...>"
              disabled={ocupado}
            />
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={ocupado}>
            Voltar
          </Button>
          <Button variant="outline" onClick={buscarNaPasta} disabled={ocupado || !nota.chaveAcesso}>
            {buscando ? (
              <RefreshCw data-icon="inline-start" className="animate-spin" />
            ) : (
              <FolderSearch data-icon="inline-start" />
            )}
            {buscando ? "Buscando…" : "Buscar na pasta"}
          </Button>
          <Button onClick={confirmar} disabled={!xml.trim() || ocupado}>
            {enviando ? (
              <>
                <RefreshCw data-icon="inline-start" className="animate-spin" />
                Enviando…
              </>
            ) : (
              <>
                <Upload data-icon="inline-start" />
                Anexar XML
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
