"use client"

import { useState } from "react"
import useSWR from "swr"
import { CheckCircle2, XCircle, RefreshCw, FileCode } from "lucide-react"
import { toast } from "sonner"
import { fetcher } from "@/lib/api"
import { baixarArquivo } from "@/lib/baixar"
import type { TentativaEmissao } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Label } from "@/components/ui/label"
import { Switch } from "@/components/ui/switch"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"
import { JsonRecebidoDialog } from "./json-recebido-dialog"

/** Rotulo legivel para cada fase em que a tentativa terminou. */
const FASE_LABEL: Record<string, string> = {
  Validacao: "Validação",
  Certificado: "Certificado",
  Schema: "Schema (XSD)",
  ResolucaoServico: "Serviço SEFAZ",
  Transmissao: "Transmissão",
  Rejeicao: "Rejeição SEFAZ",
  Autorizada: "Autorizada",
}

function moeda(v: number | null) {
  return v == null ? "—" : v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })
}

export function SecaoTentativas() {
  const { empresa, empresaId } = useEmpresa()
  const [apenasFalhas, setApenasFalhas] = useState(false)

  const {
    data: tentativas,
    isLoading,
    mutate,
  } = useSWR<TentativaEmissao[]>(
    empresaId
      ? `empresas/${empresaId}/tentativas${apenasFalhas ? "?apenasFalhas=true" : ""}`
      : null,
    fetcher,
  )

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Tentativas de emissão
          </CardTitle>
          {empresaId && (
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw data-icon="inline-start" className={isLoading ? "animate-spin" : undefined} />
              Atualizar
            </Button>
          )}
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">Empresa emitente</span>
            <span className="text-sm">
              {empresa ? (
                <>
                  <span className="font-medium">{empresa.razaoSocial}</span>
                  <span className="text-muted-foreground"> — {empresa.uf}</span>
                </>
              ) : (
                <span className="text-muted-foreground">
                  Selecione a empresa no seletor do topo para ver as tentativas.
                </span>
              )}
            </span>
          </div>

          <p className="text-sm text-muted-foreground text-pretty">
            Toda emissão que seu app envia gera um registro aqui — inclusive as que falharam
            antes de virar nota (dados inválidos, certificado, schema). É onde você descobre o
            motivo exato de uma emissão que não retornou nota.
          </p>

          {empresaId && (
            <div className="flex items-center gap-2.5">
              <Switch id="apenas-falhas" checked={apenasFalhas} onCheckedChange={setApenasFalhas} />
              <Label htmlFor="apenas-falhas" className="text-sm font-normal text-muted-foreground">
                Mostrar apenas falhas
              </Label>
            </div>
          )}
        </CardContent>
      </Card>

      {!empresaId ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Selecione uma empresa</EmptyTitle>
            <EmptyDescription>
              Escolha o emitente acima para ver a trilha completa de tentativas de emissão.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (tentativas?.length ?? 0) === 0 && !isLoading ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Nenhuma tentativa registrada</EmptyTitle>
            <EmptyDescription>
              {apenasFalhas
                ? "Nenhuma falha registrada para esta empresa."
                : "Assim que seu app enviar uma emissão para esta empresa, ela aparece aqui."}
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <Card className="gap-0 overflow-hidden p-0">
          <ul className="divide-y divide-border">
            {(tentativas ?? []).map((t) => (
              <LinhaTentativa key={t.id} t={t} />
            ))}
          </ul>
        </Card>
      )}
    </div>
  )
}

function LinhaTentativa({ t }: { t: TentativaEmissao }) {
  const [baixando, setBaixando] = useState(false)
  const Icon = t.sucesso ? CheckCircle2 : XCircle
  const fase = FASE_LABEL[t.fase] ?? t.fase

  return (
    <li className="flex items-start gap-3 px-4 py-3">
      <Icon
        className={`mt-0.5 size-4 shrink-0 ${t.sucesso ? "text-success" : "text-destructive"}`}
      />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <StatusBadge tom={t.sucesso ? "success" : "destructive"}>{fase}</StatusBadge>
          {t.codigoStatus && (
            <span className="font-mono text-xs font-semibold text-muted-foreground">
              cStat {t.codigoStatus}
            </span>
          )}
          {t.numero != null && (
            <span className="text-xs text-muted-foreground">
              Nº {t.numero} · Série {t.serie}
            </span>
          )}
          <span className="text-xs text-muted-foreground">Amb. {t.ambiente === 1 ? "Prod" : "Homolog"}</span>
        </div>

        <span className="text-sm font-medium text-pretty">{t.mensagem}</span>

        {t.detalhe && (
          <span className="text-xs text-muted-foreground text-pretty break-words">{t.detalhe}</span>
        )}

        <div className="flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-muted-foreground">
          {(t.destinatarioNome || t.destinatarioDocumento) && (
            <span>{t.destinatarioNome || t.destinatarioDocumento}</span>
          )}
          {t.valorTotal != null && <span>{moeda(t.valorTotal)}</span>}
          {t.chaveAcesso && <span className="font-mono break-all">{t.chaveAcesso}</span>}
        </div>

        {/* Resposta bruta da SEFAZ: so aparece quando a tentativa virou documento
            (chegou a transmitir). E o log completo para diagnosticar a rejeicao. */}
        <div className="mt-1 flex flex-wrap gap-2">
          <JsonRecebidoDialog path={`tentativas/nfe/${t.id}/json-recebido`} />
          {t.documentoFiscalId && (
            <Button
              variant="outline"
              size="sm"
              disabled={baixando}
              onClick={async () => {
                setBaixando(true)
                try {
                  await baixarArquivo(
                    `/api/proxy/nfe/${t.documentoFiscalId}/retorno-sefaz`,
                    `nfe-${t.numero ?? t.id}-retorno-sefaz.xml`,
                  )
                } catch (e) {
                  toast.error("Falha ao baixar a resposta da SEFAZ", {
                    description: e instanceof Error ? e.message : undefined,
                  })
                } finally {
                  setBaixando(false)
                }
              }}
            >
              <FileCode data-icon="inline-start" />
              {baixando ? "Baixando…" : "Baixar resposta da SEFAZ"}
            </Button>
          )}
        </div>
      </div>

      <span className="shrink-0 font-mono text-xs text-muted-foreground">
        {formatDateTime(t.criadoEm)}
      </span>
    </li>
  )
}
