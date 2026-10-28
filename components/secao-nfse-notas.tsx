"use client"

import { useState, useEffect } from "react"
import useSWR from "swr"
import {
  FileText,
  FileCheck2,
  FileX2,
  Ban,
  RefreshCw,
  AlertTriangle,
  FileCode,
  Printer,
  Search,
} from "lucide-react"
import { toast } from "sonner"
import { fetcher, postJson, ApiError } from "@/lib/api"
import type { NotaServico, PaginaNfse, EventoNfse, MotivoCancelamentoNfse } from "@/lib/types-nfse"
import { formatChaveNfse } from "@/lib/types-nfse"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Separator } from "@/components/ui/separator"
import { Textarea } from "@/components/ui/textarea"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime, formatDate } from "@/lib/format"
import { DialogXml, type XmlView } from "./xml-viewer-dialog"

type Tom = "success" | "warning" | "destructive" | "muted"

/** Status da NFS-e (StatusNfse no backend) -> rotulo, cor e icone. */
export function statusNfseVisual(status: number, descricao: string): { tom: Tom; rotulo: string; Icon: typeof FileText } {
  switch (status) {
    case 100:
      return { tom: "success", rotulo: "Autorizada", Icon: FileCheck2 }
    case 200:
      return { tom: "destructive", rotulo: "Rejeitada", Icon: FileX2 }
    case 400:
      return { tom: "warning", rotulo: "Cancelada", Icon: Ban }
    case 900:
      return { tom: "destructive", rotulo: "Erro de transmissão", Icon: AlertTriangle }
    case 30:
      return { tom: "muted", rotulo: "Enviada", Icon: FileText }
    default:
      return { tom: "muted", rotulo: descricao || "Em processamento", Icon: FileText }
  }
}

function moeda(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })
}

export function SecaoNfseNotas() {
  const { empresas, empresaId, empresa } = useEmpresa()
  const [selecionada, setSelecionada] = useState<NotaServico | null>(null)
  const [busca, setBusca] = useState("")
  const [buscaAplicada, setBuscaAplicada] = useState("")

  useEffect(() => {
    setSelecionada(null)
  }, [empresaId])

  const qs = new URLSearchParams({ tamanho: "100" })
  if (buscaAplicada) qs.set("busca", buscaAplicada)

  const { data, isLoading, mutate } = useSWR<PaginaNfse<NotaServico>>(
    empresaId ? `empresas/${empresaId}/nfse?${qs.toString()}` : null,
    fetcher,
  )
  const notas = data?.itens ?? []
  const semEmpresas = empresas.length === 0

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium text-muted-foreground">NFS-e emitidas</CardTitle>
          {empresaId && (
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw data-icon="inline-start" className={isLoading ? "animate-spin" : undefined} />
              Atualizar
            </Button>
          )}
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="flex flex-col gap-0.5">
            <span className="text-xs text-muted-foreground">Prestador</span>
            <span className="text-sm">
              {empresa ? (
                <>
                  <span className="font-medium">{empresa.razaoSocial}</span>
                  <span className="text-muted-foreground"> — {empresa.uf}</span>
                </>
              ) : (
                <span className="text-muted-foreground">
                  Selecione a empresa no seletor do topo para ver suas notas de serviço.
                </span>
              )}
            </span>
          </div>

          {empresaId && (
            <form
              className="flex items-center gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                setBuscaAplicada(busca.trim())
              }}
            >
              <Input
                value={busca}
                onChange={(e) => setBusca(e.target.value)}
                placeholder="Buscar por chave, nº NFS-e, nº DPS, tomador ou CNPJ/CPF"
                className="max-w-md"
                aria-label="Buscar NFS-e"
              />
              <Button type="submit" variant="outline" size="sm">
                <Search data-icon="inline-start" />
                Buscar
              </Button>
              {buscaAplicada && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    setBusca("")
                    setBuscaAplicada("")
                  }}
                >
                  Limpar
                </Button>
              )}
            </form>
          )}

          {semEmpresas && (
            <p className="text-sm text-muted-foreground">
              Cadastre uma empresa com certificado ativo e configure a NFS-e para emitir notas de serviço.
            </p>
          )}
        </CardContent>
      </Card>

      {!empresaId ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Selecione uma empresa</EmptyTitle>
            <EmptyDescription>
              Escolha o prestador acima para listar as NFS-e enviadas e ver o resultado de cada uma.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : notas.length === 0 && !isLoading ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>{buscaAplicada ? "Nada encontrado" : "Nenhuma NFS-e ainda"}</EmptyTitle>
            <EmptyDescription>
              {buscaAplicada
                ? "Nenhuma nota corresponde à busca. Tente outro termo."
                : "Quando seu app enviar uma DPS para esta empresa, a NFS-e aparece aqui com o retorno do provedor."}
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_1.1fr]">
          <ListaNfse notas={notas} selecionadaId={selecionada?.id ?? null} onSelecionar={setSelecionada} />
          {selecionada ? (
            <DetalheNfse
              notaId={selecionada.id}
              onMudou={() => {
                mutate()
                setSelecionada(null)
              }}
            />
          ) : (
            <Empty className="rounded-lg border border-dashed border-border">
              <EmptyHeader>
                <EmptyTitle>Detalhe da NFS-e</EmptyTitle>
                <EmptyDescription>
                  Clique em uma nota à esquerda para ver o retorno do provedor, baixar o XML ou imprimir o DANFSe.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
        </div>
      )}
    </div>
  )
}

function ListaNfse({
  notas,
  selecionadaId,
  onSelecionar,
}: {
  notas: NotaServico[]
  selecionadaId: string | null
  onSelecionar: (n: NotaServico) => void
}) {
  return (
    <Card className="gap-0 overflow-hidden p-0">
      <ul className="divide-y divide-border">
        {notas.map((n) => {
          const { tom, rotulo, Icon } = statusNfseVisual(n.status, n.statusDescricao)
          const ativo = n.id === selecionadaId
          return (
            <li key={n.id}>
              <button
                type="button"
                onClick={() => onSelecionar(n)}
                className={`flex w-full items-center gap-3 px-4 py-3 text-left transition-colors hover:bg-muted/50 ${
                  ativo ? "bg-muted" : ""
                }`}
              >
                <Icon className="size-4 shrink-0 text-muted-foreground" />
                <div className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate text-sm font-medium">
                    {n.numeroNfse ? `NFS-e ${n.numeroNfse}` : `DPS ${n.numeroDps}`} · Série {n.serie}
                  </span>
                  <span className="truncate text-xs text-muted-foreground">
                    {n.tomadorNome || n.tomadorDocumento || "Tomador não identificado"} · {formatDate(n.dataEmissao)}
                  </span>
                </div>
                <div className="flex flex-col items-end gap-1">
                  <StatusBadge tom={tom}>{rotulo}</StatusBadge>
                  <span className="text-xs text-muted-foreground">{moeda(n.valorServico)}</span>
                </div>
              </button>
            </li>
          )
        })}
      </ul>
    </Card>
  )
}

function DetalheNfse({ notaId, onMudou }: { notaId: string; onMudou: () => void }) {
  // Recarrega o detalhe com eventos (a listagem nao os inclui).
  const { data: nota, mutate: recarregar } = useSWR<NotaServico>(`nfse/${notaId}`, fetcher)
  const [cancelarAberto, setCancelarAberto] = useState(false)
  const [xmlView, setXmlView] = useState<XmlView | null>(null)

  if (!nota) {
    return (
      <Card>
        <CardContent className="flex items-center gap-2 py-10 text-sm text-muted-foreground">
          <RefreshCw className="size-4 animate-spin" />
          Carregando detalhe…
        </CardContent>
      </Card>
    )
  }

  const { tom, rotulo, Icon } = statusNfseVisual(nota.status, nota.statusDescricao)
  const houveProblema = nota.status === 200 || nota.status === 900
  const motivo = nota.motivoRetorno || nota.ultimoErro
  const autorizada = nota.status === 100
  const podeCancelar = autorizada && !!nota.chaveAcesso
  const temDanfse = nota.status === 100 || nota.status === 400
  const temXml = nota.status >= 20

  return (
    <Card className="gap-4">
      <CardHeader className="flex-row items-center justify-between">
        <div className="flex items-center gap-2.5">
          <Icon className="size-5 text-muted-foreground" />
          <CardTitle className="text-base">
            {nota.numeroNfse ? `NFS-e nº ${nota.numeroNfse}` : `DPS nº ${nota.numeroDps}`} · Série {nota.serie}
          </CardTitle>
        </div>
        <StatusBadge tom={tom}>{rotulo}</StatusBadge>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {motivo && (
          <div
            className={`rounded-md border px-3 py-2.5 text-sm ${
              houveProblema
                ? "border-destructive/30 bg-destructive/10 text-destructive"
                : "border-border bg-muted/40 text-foreground"
            }`}
          >
            <div className="flex items-start gap-2">
              {nota.codigoRetorno && (
                <span className="shrink-0 font-mono text-xs font-semibold">{nota.codigoRetorno}</span>
              )}
              <span className="whitespace-pre-wrap">{motivo}</span>
            </div>
          </div>
        )}

        <div className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
          <div className="sm:col-span-2">
            <Campo rotulo="Chave de acesso (50 dígitos)" mono>{formatChaveNfse(nota.chaveAcesso)}</Campo>
          </div>
          <Campo rotulo="Nº DPS">{String(nota.numeroDps)}</Campo>
          <Campo rotulo="Padrão">{nota.padrao === 1 ? "Nacional" : "ABRASF"}</Campo>
          <Campo rotulo="Tomador">{nota.tomadorNome ?? "—"}</Campo>
          <Campo rotulo="Documento">{nota.tomadorDocumento ?? "—"}</Campo>
          <Campo rotulo="Código de tributação nacional" mono>{nota.codigoTributacaoNacional || "—"}</Campo>
          <Campo rotulo="Ambiente">{nota.ambiente === 1 ? "Produção" : "Produção restrita (homologação)"}</Campo>
          <Campo rotulo="Valor do serviço">{moeda(nota.valorServico)}</Campo>
          <Campo rotulo="ISSQN">
            {moeda(nota.valorIss)} ({nota.aliquotaIss.toLocaleString("pt-BR")}%){nota.issRetido ? " · retido" : ""}
          </Campo>
          <Campo rotulo="Competência">{formatDate(nota.dataCompetencia)}</Campo>
          <Campo rotulo="Emissão">{formatDateTime(nota.dataEmissao)}</Campo>
          <Campo rotulo="Autorização">{formatDateTime(nota.dataAutorizacao)}</Campo>
          <Campo rotulo="Tentativas de envio">{String(nota.tentativasEnvio)}</Campo>
          <div className="sm:col-span-2">
            <Campo rotulo="Discriminação do serviço">
              <span className="whitespace-pre-wrap">{nota.descricaoServico || "—"}</span>
            </Campo>
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          {temDanfse && (
            <Button
              render={<a href={`/danfse/${nota.id}`} target="_blank" rel="noopener noreferrer" />}
              nativeButton={false}
              variant="default"
              size="sm"
            >
              <Printer data-icon="inline-start" />
              Imprimir DANFSe
            </Button>
          )}
          {temXml && (
            <Button
              variant={autorizada ? "outline" : "default"}
              size="sm"
              onClick={() =>
                setXmlView({
                  titulo: autorizada || nota.status === 400 ? "XML da NFS-e" : "XML da DPS assinada",
                  url: `/api/proxy/nfse/${nota.id}/xml`,
                  nomeArquivo: `${nota.chaveAcesso ?? `dps-${nota.numeroDps}`}.xml`,
                })
              }
            >
              <FileCode data-icon="inline-start" />
              {autorizada || nota.status === 400 ? "Ver XML da NFS-e" : "Ver XML da DPS"}
            </Button>
          )}
          {podeCancelar && (
            <Button
              variant="outline"
              size="sm"
              className="text-destructive hover:text-destructive"
              onClick={() => setCancelarAberto(true)}
            >
              <Ban data-icon="inline-start" />
              Cancelar NFS-e
            </Button>
          )}
        </div>

        {nota.eventos && nota.eventos.length > 0 && (
          <>
            <Separator />
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">Eventos</h3>
              <ul className="flex flex-col gap-2">
                {nota.eventos.map((ev) => (
                  <li
                    key={ev.id}
                    className="flex items-start justify-between gap-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm"
                  >
                    <div className="flex min-w-0 flex-col gap-0.5">
                      <span className="font-medium">
                        {ev.tipoEvento === "101101" ? "Cancelamento" : `Evento ${ev.tipoEvento}`} · seq. {ev.sequencia}
                      </span>
                      {ev.justificativa && (
                        <span className="text-xs text-muted-foreground">{ev.justificativa}</span>
                      )}
                      {ev.motivo && (
                        <span className="text-xs text-muted-foreground">
                          {ev.codigoStatus ? `${ev.codigoStatus} · ` : ""}
                          {ev.motivo}
                        </span>
                      )}
                    </div>
                    <div className="flex shrink-0 flex-col items-end gap-1">
                      <StatusBadge tom={ev.homologado ? "success" : "destructive"}>
                        {ev.homologado ? "Homologado" : "Rejeitado"}
                      </StatusBadge>
                      <span className="font-mono text-xs text-muted-foreground">{formatDateTime(ev.criadoEm)}</span>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}
      </CardContent>

      <DialogCancelamentoNfse
        nota={nota}
        aberto={cancelarAberto}
        onFechar={() => setCancelarAberto(false)}
        onCancelado={() => {
          recarregar()
          onMudou()
        }}
      />

      <DialogXml view={xmlView} onFechar={() => setXmlView(null)} />
    </Card>
  )
}

const MOTIVOS_PADRAO: MotivoCancelamentoNfse[] = [
  { codigo: "1", descricao: "Erro na emissão" },
  { codigo: "2", descricao: "Serviço não prestado" },
  { codigo: "9", descricao: "Outros" },
]

const JUSTIFICATIVA_MIN = 15
const JUSTIFICATIVA_MAX = 255

function DialogCancelamentoNfse({
  nota,
  aberto,
  onFechar,
  onCancelado,
}: {
  nota: NotaServico
  aberto: boolean
  onFechar: () => void
  onCancelado: () => void
}) {
  const [codigoMotivo, setCodigoMotivo] = useState("1")
  const [justificativa, setJustificativa] = useState("")
  const [enviando, setEnviando] = useState(false)
  const { data: motivosApi } = useSWR<MotivoCancelamentoNfse[]>(aberto ? "nfse/motivos-cancelamento" : null, fetcher)
  const motivos = motivosApi ?? MOTIVOS_PADRAO
  const motivoAtual = motivos.find((m) => m.codigo === codigoMotivo)
  const tamanho = justificativa.trim().length
  const valido = tamanho >= JUSTIFICATIVA_MIN && tamanho <= JUSTIFICATIVA_MAX

  async function confirmar() {
    if (!valido || enviando) return
    setEnviando(true)
    try {
      const r = await postJson<{ nota: NotaServico; evento: EventoNfse }>(
        `empresas/${nota.empresaId}/nfse/${nota.id}/cancelar`,
        { codigoMotivo, justificativa: justificativa.trim() },
      )
      toast.success("NFS-e cancelada", {
        description: `${r.evento.protocolo ? `Protocolo ${r.evento.protocolo} · ` : ""}${r.evento.motivo ?? ""}`,
      })
      setJustificativa("")
      onFechar()
      onCancelado()
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : "Falha ao cancelar. Tente novamente."
      toast.error("Não foi possível cancelar", { description: msg })
    } finally {
      setEnviando(false)
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !enviando ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Cancelar NFS-e nº {nota.numeroNfse ?? "—"}</DialogTitle>
          <DialogDescription>
            O cancelamento é registrado no provedor como evento oficial (e101101 no padrão nacional) e é
            definitivo. Escolha o motivo e descreva a justificativa ({JUSTIFICATIVA_MIN} a {JUSTIFICATIVA_MAX}{" "}
            caracteres).
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <Label htmlFor="motivo-cancelamento-nfse">Motivo</Label>
            <Select value={codigoMotivo} onValueChange={(v) => v && setCodigoMotivo(v)} disabled={enviando}>
              <SelectTrigger id="motivo-cancelamento-nfse">
                <SelectValue>{motivoAtual ? `${motivoAtual.codigo} — ${motivoAtual.descricao}` : codigoMotivo}</SelectValue>
              </SelectTrigger>
              <SelectContent>
                {motivos.map((m) => (
                  <SelectItem key={m.codigo} value={m.codigo}>
                    {m.codigo} — {m.descricao}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="justificativa-cancelamento-nfse">Justificativa</Label>
            <Textarea
              id="justificativa-cancelamento-nfse"
              value={justificativa}
              onChange={(e) => setJustificativa(e.target.value)}
              maxLength={JUSTIFICATIVA_MAX}
              rows={4}
              placeholder="Ex.: Nota emitida em duplicidade para o mesmo serviço."
              disabled={enviando}
            />
            <span className={`text-xs ${tamanho > 0 && !valido ? "text-destructive" : "text-muted-foreground"}`}>
              {tamanho} / {JUSTIFICATIVA_MAX} caracteres
              {tamanho > 0 && tamanho < JUSTIFICATIVA_MIN ? ` · mínimo ${JUSTIFICATIVA_MIN}` : ""}
            </span>
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={enviando}>
            Voltar
          </Button>
          <Button variant="destructive" onClick={confirmar} disabled={!valido || enviando}>
            {enviando ? (
              <>
                <RefreshCw data-icon="inline-start" className="animate-spin" />
                Cancelando…
              </>
            ) : (
              <>
                <Ban data-icon="inline-start" />
                Confirmar cancelamento
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function Campo({ rotulo, children, mono }: { rotulo: string; children: React.ReactNode; mono?: boolean }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-xs text-muted-foreground">{rotulo}</span>
      <span className={mono ? "break-all font-mono text-xs" : undefined}>{children}</span>
    </div>
  )
}
