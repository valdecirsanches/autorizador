"use client"

import { useState } from "react"
import { CopyPlus, FolderSearch, Save } from "lucide-react"
import { toast } from "sonner"
import { fetcher, postJson, putJson } from "@/lib/api"
import type { DiagnosticoPastaXml, Empresa } from "@/lib/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Spinner } from "@/components/ui/spinner"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"

/**
 * Pasta (no servidor da API) com os XMLs ja emitidos, organizada em
 * periodo (NFeAAAAMM/NFe, AAAAMM/NFe...) com NFe e Evento. Com ela configurada, a API completa sozinha o XML
 * das notas cadastradas pela consulta e o DANFE passa a ser gerado.
 */
export function ConfigPastaXml({
  empresa,
  aberto,
  onFechar,
  onSalvo,
}: {
  empresa: Empresa
  aberto: boolean
  onFechar: () => void
  onSalvo: () => void
}) {
  const [pasta, setPasta] = useState(empresa.pastaXmlBackup ?? "")
  const [salvando, setSalvando] = useState(false)
  const [testando, setTestando] = useState(false)
  const [copiando, setCopiando] = useState(false)
  const [resultadoCopia, setResultadoCopia] = useState<ResultadoCopia | null>(null)
  const [desde, setDesde] = useState("")
  const [diag, setDiag] = useState<DiagnosticoPastaXml | null>(null)

  async function salvar() {
    setSalvando(true)
    try {
      await putJson(`empresas/${empresa.id}/pasta-xml`, { pasta: pasta.trim() })
      toast.success(pasta.trim() ? "Pasta de XML salva" : "Pasta de XML removida")
      onSalvo()
      onFechar()
    } catch (e) {
      toast.error("Não foi possível salvar a pasta", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setSalvando(false)
    }
  }

  async function testar() {
    setTestando(true)
    setDiag(null)
    try {
      await putJson(`empresas/${empresa.id}/pasta-xml`, { pasta: pasta.trim() })
      setDiag(await fetcher<DiagnosticoPastaXml>(`empresas/${empresa.id}/pasta-xml/diagnostico`))
      onSalvo()
    } catch (e) {
      toast.error("Não foi possível ler a pasta", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setTestando(false)
    }
  }

  /** Salva a pasta e grava nela o XML de todas as notas de producao ja autorizadas. */
  async function copiarNotas() {
    setCopiando(true)
    setResultadoCopia(null)
    try {
      await putJson(`empresas/${empresa.id}/pasta-xml`, { pasta: pasta.trim() })
      const filtro = desde ? `?desde=${encodeURIComponent(desde)}` : ""
      const r = await postJson<ResultadoCopia>(`empresas/${empresa.id}/pasta-xml/exportar${filtro}`, {})
      setResultadoCopia(r)
      onSalvo()
      if (r.erros > 0) toast.warning("Cópia concluída com erros", { description: r.mensagem })
      else toast.success("Notas copiadas para a pasta", { description: r.mensagem })
    } catch (e) {
      toast.error("Não foi possível copiar as notas", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setCopiando(false)
    }
  }

  const ocupado = salvando || testando || copiando

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !ocupado ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Pasta de XMLs · {empresa.razaoSocial}</DialogTitle>
          <DialogDescription>
            Caminho completo, no servidor da API, da pasta do CNPJ que contém as subpastas por período
            (NFe202610, NFe20261028 ou 202610…), cada uma com NFe e Evento. Só a subpasta NFe é lida para
            achar o XML da nota. As notas autorizadas em produção também são gravadas aqui
            ({"{período}"}/NFe/{"{chave}"}-nfe.xml), sem sobrescrever arquivos. Deixe vazio para remover.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-2">
          <Label htmlFor={`pasta-xml-${empresa.id}`}>Pasta</Label>
          <Input
            id={`pasta-xml-${empresa.id}`}
            value={pasta}
            onChange={(e) => {
              setPasta(e.target.value)
              setDiag(null)
            }}
            className="font-mono text-xs"
            placeholder="/mnt/hdbackup/API/levpao/arquivos/nfe/EDSMonitorPLUS/Arqs/03707154000106"
            disabled={ocupado}
          />
          {diag ? (
            <p
              role="status"
              className={`text-sm ${diag.existe && diag.meses > 0 ? "text-emerald-500" : "text-destructive"}`}
            >
              {diag.mensagem}
            </p>
          ) : null}
          <div className="mt-2 flex items-center gap-2">
            <Label htmlFor={`desde-${empresa.id}`} className="whitespace-nowrap text-sm">
              Copiar notas autorizadas desde
            </Label>
            <Input
              id={`desde-${empresa.id}`}
              type="date"
              value={desde}
              onChange={(e) => setDesde(e.target.value)}
              className="w-44"
              disabled={ocupado}
            />
            <span className="text-xs text-muted-foreground">(vazio = todas)</span>
          </div>
          {resultadoCopia ? (
            <p role="status" className={`text-sm ${resultadoCopia.erros > 0 ? "text-amber-500" : "text-emerald-500"}`}>
              {resultadoCopia.total} nota(s) de produção: {resultadoCopia.mensagem}
            </p>
          ) : null}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={ocupado}>
            Voltar
          </Button>
          <Button
            variant="outline"
            onClick={copiarNotas}
            disabled={ocupado || !pasta.trim()}
            title="Grava na pasta o XML de todas as notas de produção já autorizadas (não sobrescreve)"
          >
            {copiando ? <Spinner data-icon="inline-start" /> : <CopyPlus data-icon="inline-start" />}
            Copiar notas para a pasta
          </Button>
          <Button variant="outline" onClick={testar} disabled={ocupado || !pasta.trim()}>
            {testando ? <Spinner data-icon="inline-start" /> : <FolderSearch data-icon="inline-start" />}
            Salvar e testar
          </Button>
          <Button onClick={salvar} disabled={ocupado}>
            {salvando ? <Spinner data-icon="inline-start" /> : <Save data-icon="inline-start" />}
            Salvar
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

type ResultadoCopia = {
  pasta: string
  total: number
  gravados: number
  jaExistiam: number
  erros: number
  exemplosErro: string[]
  mensagem: string
}
