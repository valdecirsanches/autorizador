"use client"

import { useEffect, useState } from "react"
import useSWR from "swr"
import { CheckCircle2, XCircle, RefreshCw, ChevronLeft, ChevronRight } from "lucide-react"
import { fetcher } from "@/lib/api"
import type { TentativaNfse, PaginaNfse } from "@/lib/types-nfse"
import { formatChaveNfse } from "@/lib/types-nfse"
import { useEmpresa } from "./empresa-provider"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Label } from "@/components/ui/label"
import { Switch } from "@/components/ui/switch"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"
import { JsonRecebidoDialog } from "./json-recebido-dialog"

/** Rotulo legivel para cada fase em que a tentativa NFS-e terminou. */
const FASE_LABEL: Record<string, string> = {
  Validacao: "Validação",
  Configuracao: "Configuração NFS-e",
  Certificado: "Certificado",
  Numeracao: "Numeração DPS",
  Geracao: "Geração da DPS",
  Assinatura: "Assinatura",
  Transmissao: "Transmissão",
  Rejeicao: "Rejeição",
  Autorizada: "Autorizada",
  Cancelamento: "Cancelamento",
  Consulta: "Consulta",
}

function moeda(v: number | null) {
  return v == null ? "—" : v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })
}

const TAMANHO = 50

export function SecaoNfseTentativas() {
  const { empresa, empresaId } = useEmpresa()
  const [apenasFalhas, setApenasFalhas] = useState(false)
  const [pagina, setPagina] = useState(1)

  // Trocar de empresa volta para a primeira pagina.
  useEffect(() => {
    setPagina(1)
  }, [empresaId])

  const { data, isLoading, mutate } = useSWR<PaginaNfse<TentativaNfse>>(
    empresaId
      ? `nfse/tentativas?empresaId=${empresaId}&pagina=${pagina}&tamanho=${TAMANHO}${
          apenasFalhas ? "&sucesso=false" : ""
        }`
      : null,
    fetcher,
  )

  const tentativas = data?.itens ?? []
  const totalPaginas = data ? Math.max(1, Math.ceil(data.total / data.tamanho)) : 1

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Tentativas de emissão de NFS-e
          </CardTitle>
          {empresaId && (
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw data-icon="inline-start" className={isLoading ? "animate-spin" : undefined} />
              Atualizar
            </Button>
          )}
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {!empresaId ? (
            <p className="text-sm text-muted-foreground">
              Selecione uma empresa no topo da página para ver as tentativas.
            </p>
          ) : (
            <>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <p className="text-sm text-muted-foreground">
                  Registro de toda emissão de NFS-e de{" "}
                  <span className="font-medium text-foreground">{empresa?.razaoSocial}</span>, inclusive as
                  que falharam antes de virar nota.
                  {data && (
                    <>
                      {" "}
                      <span className="tabular-nums">{data.total}</span> registro(s).
                    </>
                  )}
                </p>
                <div className="flex items-center gap-2">
                  <Switch
                    id="nfse-apenas-falhas"
                    checked={apenasFalhas}
                    onCheckedChange={(v) => {
                      setApenasFalhas(v)
                      setPagina(1)
                    }}
                  />
                  <Label htmlFor="nfse-apenas-falhas" className="text-sm">
                    Somente falhas
                  </Label>
                </div>
              </div>

              {isLoading && !data ? (
                <p className="text-sm text-muted-foreground">Carregando...</p>
              ) : tentativas.length === 0 ? (
                <Empty className="py-10">
                  <EmptyHeader>
                    <EmptyTitle>Nenhuma tentativa registrada</EmptyTitle>
                    <EmptyDescription>
                      {apenasFalhas
                        ? "Não há falhas de emissão de NFS-e para esta empresa."
                        : "Assim que o app emissor enviar uma DPS, o resultado aparecerá aqui."}
                    </EmptyDescription>
                  </EmptyHeader>
                </Empty>
              ) : (
                <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
                  {tentativas.map((t) => (
                    <li key={t.id} className="flex flex-col gap-2 px-4 py-3 text-sm">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="flex items-center gap-2">
                          {t.sucesso ? (
                            <CheckCircle2 className="size-4 shrink-0 text-success" aria-hidden />
                          ) : (
                            <XCircle className="size-4 shrink-0 text-destructive" aria-hidden />
                          )}
                          <span className="font-medium">
                            {t.sucesso ? "Autorizada" : `Falhou em: ${FASE_LABEL[t.fase] ?? t.fase}`}
                          </span>
                          {t.codigoStatus && (
                            <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs text-muted-foreground">
                              {t.codigoStatus}
                            </span>
                          )}
                        </div>
                        <span className="text-xs text-muted-foreground">{formatDateTime(t.criadoEm)}</span>
                      </div>

                      <p className="text-pretty text-muted-foreground">{t.mensagem}</p>

                      <div className="flex flex-wrap gap-x-5 gap-y-1 text-xs text-muted-foreground">
                        {t.serie && t.numeroDps != null && (
                          <span>
                            DPS <span className="tabular-nums text-foreground">{t.serie}/{t.numeroDps}</span>
                          </span>
                        )}
                        <span>{t.ambiente === 1 ? "Produção" : "Homologação"}</span>
                        {t.tomadorNome && <span>Tomador: {t.tomadorNome}</span>}
                        {t.valorServico != null && <span>Valor: {moeda(t.valorServico)}</span>}
                        {t.chaveAcesso && (
                          <span className="font-mono">{formatChaveNfse(t.chaveAcesso)}</span>
                        )}
                      </div>

                      {t.detalhe && (
                        <details className="text-xs">
                          <summary className="cursor-pointer text-muted-foreground hover:text-foreground">
                            Detalhe técnico
                          </summary>
                          <pre className="mt-2 max-h-48 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 font-mono text-muted-foreground">
                            {t.detalhe}
                          </pre>
                        </details>
                      )}
                      <div className="flex flex-wrap gap-2">
                        <JsonRecebidoDialog path={`tentativas/nfse/${t.id}/json-recebido`} />
                      </div>
                    </li>
                  ))}
                </ul>
              )}

              {data && totalPaginas > 1 && (
                <div className="flex items-center justify-between">
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={pagina <= 1}
                    onClick={() => setPagina((p) => Math.max(1, p - 1))}
                  >
                    <ChevronLeft data-icon="inline-start" />
                    Anterior
                  </Button>
                  <span className="text-xs text-muted-foreground tabular-nums">
                    Página {pagina} de {totalPaginas}
                  </span>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={pagina >= totalPaginas}
                    onClick={() => setPagina((p) => Math.min(totalPaginas, p + 1))}
                  >
                    Próxima
                    <ChevronRight data-icon="inline-end" />
                  </Button>
                </div>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
