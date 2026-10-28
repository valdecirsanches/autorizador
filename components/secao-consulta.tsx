"use client"

import { useState } from "react"
import { Search, FileCheck2, FileX2, Ban, Clock } from "lucide-react"
import { toast } from "sonner"
import { postJson, ApiError } from "@/lib/api"
import type { ConsultaProtocolo } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Separator } from "@/components/ui/separator"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"

/** So digitos, no maximo 44 — a chave de acesso da NF-e. */
function limparChave(v: string) {
  return v.replace(/\D/g, "").slice(0, 44)
}

function veredito(r: ConsultaProtocolo): {
  tom: "success" | "warning" | "destructive"
  rotulo: string
  Icon: typeof FileCheck2
} {
  if (r.autorizada) return { tom: "success", rotulo: "Autorizada", Icon: FileCheck2 }
  if (r.cancelada) return { tom: "warning", rotulo: "Cancelada", Icon: Ban }
  if (r.denegada) return { tom: "destructive", rotulo: "Denegada", Icon: FileX2 }
  return { tom: "warning", rotulo: "Situação diversa", Icon: Clock }
}

export function SecaoConsulta() {
  const { empresas, empresaId, empresa, ambiente } = useEmpresa()
  const [chave, setChave] = useState("")
  const [consultando, setConsultando] = useState(false)
  const [resultado, setResultado] = useState<ConsultaProtocolo | null>(null)

  async function consultar() {
    if (!empresaId) {
      toast.error("Selecione a empresa emitente no topo")
      return
    }
    if (chave.length !== 44) {
      toast.error("A chave de acesso deve ter 44 dígitos")
      return
    }
    setConsultando(true)
    try {
      const r = await postJson<ConsultaProtocolo>(`empresas/${empresaId}/nfe/consultar`, {
        chaveAcesso: chave,
        ambiente,
        ufAutorizadorOverride: empresa?.ufAutorizadorOverride ?? null,
      })
      setResultado(r)
      toast.success(`SEFAZ respondeu: ${r.codigoStatus} — ${r.motivo}`)
    } catch (e) {
      setResultado(null)
      const msg = e instanceof ApiError ? e.message : "Erro inesperado"
      toast.error("Falha na consulta", { description: msg })
    } finally {
      setConsultando(false)
    }
  }

  const semEmpresas = empresas.length === 0

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader>
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Consultar situação de uma NF-e na SEFAZ
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">Empresa emitente</span>
            <span className="text-sm">
              {empresa ? (
                <>
                  <span className="font-medium">{empresa.razaoSocial}</span>
                  <span className="text-muted-foreground">
                    {" "}
                    — {empresa.uf} · usa o certificado dela
                  </span>
                </>
              ) : (
                <span className="text-muted-foreground">
                  Selecione a empresa no seletor do topo.
                </span>
              )}
            </span>
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="chave">Chave de acesso (44 dígitos)</Label>
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
              <Input
                id="chave"
                inputMode="numeric"
                autoComplete="off"
                placeholder="Cole aqui os 44 dígitos da chave"
                value={chave}
                onChange={(ev) => setChave(limparChave(ev.target.value))}
                onKeyDown={(ev) => {
                  if (ev.key === "Enter" && !ev.nativeEvent.isComposing) consultar()
                }}
                className="font-mono tracking-tight sm:w-96"
              />
              <Button onClick={consultar} disabled={consultando}>
                <Search
                  data-icon="inline-start"
                  className={consultando ? "animate-pulse" : undefined}
                />
                {consultando ? "Consultando..." : "Consultar"}
              </Button>
            </div>
            <span className="text-xs text-muted-foreground">
              {chave.length}/44 dígitos
            </span>
          </div>

          {semEmpresas && (
            <p className="text-sm text-muted-foreground">
              Cadastre uma empresa com certificado ativo para consultar.
            </p>
          )}
        </CardContent>
      </Card>

      {resultado ? (
        <ResultadoConsulta r={resultado} />
      ) : (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Nenhuma consulta ainda</EmptyTitle>
            <EmptyDescription>
              Informe a chave de acesso de uma NF-e para ver a situação atual na SEFAZ.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      )}
    </div>
  )
}

function ResultadoConsulta({ r }: { r: ConsultaProtocolo }) {
  const { tom, rotulo, Icon } = veredito(r)

  return (
    <Card className="gap-4">
      <CardHeader className="flex-row items-center justify-between">
        <div className="flex items-center gap-2.5">
          <Icon className="size-5 text-muted-foreground" />
          <CardTitle className="text-base">Resultado</CardTitle>
        </div>
        <StatusBadge tom={tom}>{rotulo}</StatusBadge>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p className="text-sm text-muted-foreground">
          <span className="font-mono text-foreground">{r.codigoStatus}</span>
          {" — "}
          {r.motivo}
        </p>

        <div className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
          <Campo rotulo="Chave de acesso" mono>{r.chaveAcesso}</Campo>
          <Campo rotulo="Protocolo">{r.numeroProtocolo ?? "—"}</Campo>
          <Campo rotulo="UF / Autorizador">{`${r.uf} · ${r.autorizador}`}</Campo>
          <Campo rotulo="Ambiente">{r.ambienteDescricao}</Campo>
          <Campo rotulo="Autorização">{r.dataAutorizacao ? formatDateTime(r.dataAutorizacao) : "—"}</Campo>
          <Campo rotulo="Resposta">{r.tempoRespostaMs > 0 ? `${r.tempoRespostaMs} ms` : "—"}</Campo>
        </div>

        {r.xmlOrigem === "pasta" && (
          <p className="text-sm text-muted-foreground">
            XML localizado automaticamente na pasta de backup e anexado à nota.
          </p>
        )}
        {r.xmlPendente && r.aviso && (
          <p role="alert" className="rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
            {r.aviso}
          </p>
        )}

        {r.eventos.length > 0 && (
          <>
            <Separator />
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">Eventos vinculados</h3>
              <ul className="flex flex-col gap-2">
                {r.eventos.map((ev, i) => (
                  <li
                    key={`${ev.tipoEvento}-${i}`}
                    className="flex items-center justify-between rounded-md border border-border bg-muted/40 px-3 py-2 text-sm"
                  >
                    <span>
                      <span className="font-mono text-xs text-muted-foreground">{ev.tipoEvento}</span>
                      {" · "}
                      {ev.descricao || "Evento"}
                    </span>
                    <span className="font-mono text-xs text-muted-foreground">
                      {ev.dataEvento ? formatDateTime(ev.dataEvento) : ""}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  )
}

function Campo({
  rotulo,
  children,
  mono,
}: {
  rotulo: string
  children: React.ReactNode
  mono?: boolean
}) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-xs text-muted-foreground">{rotulo}</span>
      <span className={mono ? "break-all font-mono text-xs" : undefined}>{children}</span>
    </div>
  )
}
