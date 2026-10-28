"use client"

import useSWR from "swr"
import { useState } from "react"
import { RefreshCw } from "lucide-react"
import { toast } from "sonner"
import { fetcher, postJson } from "@/lib/api"
import type { StatusServico } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"
import { DiagnosticoConexao } from "./diagnostico-conexao"

function tomDoStatus(online: boolean, codigo: string | null): "success" | "warning" | "destructive" {
  if (online) return "success"
  // 108/109 = paralisacao temporaria/total: degradado, nao erro nosso.
  if (codigo === "108" || codigo === "109") return "warning"
  return "destructive"
}

export function SecaoStatus() {
  const { empresas, empresaId, empresa, ambiente } = useEmpresa()
  const { data, error, isLoading, mutate } = useSWR<StatusServico[]>(
    `status?ambiente=${ambiente}`,
    fetcher,
    { refreshInterval: 60_000 },
  )
  const [consultando, setConsultando] = useState(false)

  async function consultar() {
    if (!empresaId || !empresa) {
      toast.error("Selecione uma empresa no topo para consultar a SEFAZ")
      return
    }
    setConsultando(true)
    try {
      // A consulta real de status exige o certificado da empresa (mTLS).
      // UF e ambiente seguem no JSON; a API so guarda certificado e logo.
      await postJson(`status/empresa/${empresaId}/consultar`, {
        uf: empresa.uf,
        ambiente,
        ufAutorizadorOverride: empresa.ufAutorizadorOverride,
      })
      await mutate()
      toast.success("Consulta concluída")
    } catch (e) {
      toast.error("Falha ao consultar a SEFAZ", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setConsultando(false)
    }
  }

  if (error) {
    return (
      <div className="flex flex-col gap-6">
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Não foi possível carregar o status</EmptyTitle>
            <EmptyDescription>
              Verifique se a API do autorizador está no ar. {String(error.message ?? "")}
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
        <DiagnosticoConexao />
      </div>
    )
  }

  const leituras = data ?? []

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader>
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Consultar a SEFAZ agora
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3 sm:flex-row sm:items-center">
          <div className="flex min-w-0 flex-1 flex-col">
            <span className="text-sm">
              {empresa ? (
                <>
                  <span className="font-medium">{empresa.razaoSocial}</span>
                  <span className="text-muted-foreground"> — {empresa.uf}</span>
                </>
              ) : (
                <span className="text-muted-foreground">Nenhuma empresa selecionada</span>
              )}
            </span>
            <span className="text-xs text-muted-foreground">
              Ambiente {ambiente === 1 ? "de produção" : "de homologação"} · use o seletor no
              topo para trocar de empresa.
            </span>
          </div>
          <Button onClick={consultar} disabled={consultando || !empresaId}>
            <RefreshCw
              data-icon="inline-start"
              className={consultando ? "animate-spin" : undefined}
            />
            {consultando ? "Consultando..." : "Consultar status"}
          </Button>
          {empresas.length === 0 && (
            <span className="text-sm text-muted-foreground">
              Cadastre uma empresa com certificado ativo para consultar.
            </span>
          )}
        </CardContent>
      </Card>

      <div>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground">
          Últimas leituras por UF
        </h2>
        {isLoading ? (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {Array.from({ length: 4 }).map((_, i) => (
              <Card key={i}>
                <CardHeader>
                  <Skeleton className="h-5 w-16" />
                </CardHeader>
                <CardContent>
                  <Skeleton className="h-4 w-full" />
                </CardContent>
              </Card>
            ))}
          </div>
        ) : leituras.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyTitle>Nenhuma leitura ainda</EmptyTitle>
              <EmptyDescription>
                Dispare uma consulta acima para registrar o status de uma UF.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {leituras.map((s) => {
              const tom = tomDoStatus(s.online, s.codigoStatus)
              return (
                <Card key={`${s.uf}-${s.consultadoEm}`} className="gap-3">
                  <CardHeader className="flex-row items-center justify-between">
                    <CardTitle className="font-mono text-base tracking-wide">{s.uf}</CardTitle>
                    <StatusBadge tom={tom}>
                      {s.online ? "Online" : tom === "warning" ? "Paralisado" : "Offline"}
                    </StatusBadge>
                  </CardHeader>
                  <CardContent className="flex flex-col gap-2 text-sm">
                    {s.codigoStatus || s.motivo ? (
                      <p className="line-clamp-2 min-h-9 text-muted-foreground">
                        {[s.codigoStatus, s.motivo].filter(Boolean).join(" — ")}
                      </p>
                    ) : (
                      <p className="min-h-9 text-muted-foreground">
                        Sem resposta da SEFAZ (falha de transporte).
                      </p>
                    )}
                    {s.erroTransporte && (
                      <p
                        className="rounded-md border border-destructive/30 bg-destructive/10 p-2 font-mono text-xs leading-relaxed text-destructive"
                        title={s.erroTransporte}
                      >
                        {s.erroTransporte}
                      </p>
                    )}
                    <div className="flex items-center justify-between text-xs text-muted-foreground">
                      <span>{s.tempoRespostaMs > 0 ? `${s.tempoRespostaMs} ms` : "—"}</span>
                      <span className="font-mono">{formatDateTime(s.consultadoEm)}</span>
                    </div>
                  </CardContent>
                </Card>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}
