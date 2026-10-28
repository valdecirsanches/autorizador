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
  PencilLine,
  Upload,
} from "lucide-react"
import { toast } from "sonner"
import { fetcher, postJson, ApiError } from "@/lib/api"
import type { DocumentoFiscal, EventoFiscal } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Label } from "@/components/ui/label"
import { Separator } from "@/components/ui/separator"
import { Textarea } from "@/components/ui/textarea"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"
import { DiagnosticoConexao } from "./diagnostico-conexao"
import { JsonRecebidoDialog } from "./json-recebido-dialog"
import { DialogAnexarXml } from "./dialog-anexar-xml"
import { DialogXml, type XmlView } from "./xml-viewer-dialog"
import { baixarArquivo } from "@/lib/baixar"

type Tom = "success" | "warning" | "destructive" | "muted"

/** Traduz o codigo de status do documento em rotulo + cor do badge. */
function statusVisual(status: number, descricao: string): { tom: Tom; rotulo: string; Icon: typeof FileText } {
  switch (status) {
    case 100:
      return { tom: "success", rotulo: "Autorizada", Icon: FileCheck2 }
    case 200:
      return { tom: "destructive", rotulo: "Rejeitada", Icon: FileX2 }
    case 300:
      return { tom: "destructive", rotulo: "Denegada", Icon: FileX2 }
    case 400:
      return { tom: "warning", rotulo: "Cancelada", Icon: Ban }
    case 500:
      return { tom: "muted", rotulo: "Inutilizada", Icon: Ban }
    case 900:
      return { tom: "destructive", rotulo: "Erro de transmissão", Icon: AlertTriangle }
    default:
      return { tom: "muted", rotulo: descricao || "Em processamento", Icon: FileText }
  }
}

/** Status internos (StatusDocumento) que encerram a vida da nota. */
const STATUS_FINAIS = new Set([100, 300, 400, 500])

function moeda(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })
}

export function SecaoNotas() {
  const { empresas, empresaId, empresa } = useEmpresa()
  const [selecionada, setSelecionada] = useState<DocumentoFiscal | null>(null)

  // Trocar de empresa no topo limpa a nota aberta (era de outro emitente).
  useEffect(() => {
    setSelecionada(null)
  }, [empresaId])

  const {
    data: notas,
    isLoading,
    mutate,
  } = useSWR<DocumentoFiscal[]>(
    empresaId ? `empresas/${empresaId}/nfe` : null,
    fetcher,
  )

  const semEmpresas = empresas.length === 0

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Notas emitidas
          </CardTitle>
          {empresaId && (
            <Button variant="outline" size="sm" onClick={() => mutate()}>
              <RefreshCw data-icon="inline-start" className={isLoading ? "animate-spin" : undefined} />
              Atualizar
            </Button>
          )}
        </CardHeader>
        <CardContent className="flex flex-col gap-2">
          <span className="text-xs text-muted-foreground">Empresa emitente</span>
          <span className="text-sm">
            {empresa ? (
              <>
                <span className="font-medium">{empresa.razaoSocial}</span>
                <span className="text-muted-foreground"> — {empresa.uf}</span>
              </>
            ) : (
              <span className="text-muted-foreground">
                Selecione a empresa no seletor do topo para ver suas notas.
              </span>
            )}
          </span>

          {semEmpresas && (
            <p className="text-sm text-muted-foreground">
              Cadastre uma empresa com certificado ativo para emitir e visualizar notas.
            </p>
          )}
        </CardContent>
      </Card>

      {!empresaId ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Selecione uma empresa</EmptyTitle>
            <EmptyDescription>
              Escolha o emitente acima para listar as NF-e enviadas e ver o motivo de cada resultado.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (notas?.length ?? 0) === 0 && !isLoading ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Nenhuma nota ainda</EmptyTitle>
            <EmptyDescription>
              Quando seu app enviar uma NF-e para esta empresa, ela aparece aqui com o status da SEFAZ.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_1.1fr]">
          <ListaNotas
            notas={notas ?? []}
            selecionadaId={selecionada?.id ?? null}
            onSelecionar={setSelecionada}
          />
          {selecionada ? (
            <DetalheNota
              nota={selecionada}
              onMudou={() => {
                mutate()
                setSelecionada(null)
              }}
            />
          ) : (
            <Empty className="rounded-lg border border-dashed border-border">
              <EmptyHeader>
                <EmptyTitle>Detalhe da nota</EmptyTitle>
                <EmptyDescription>
                  Clique em uma nota à esquerda para ver o motivo do resultado e baixar o XML.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
        </div>
      )}

      {/* Diagnostico opcional: quando a contagem de notas nao bate com o banco
          esperado, revela a QUAL MySQL/base a API esta de fato conectada. */}
      {empresaId && (
        <details className="text-sm">
          <summary className="cursor-pointer text-muted-foreground hover:text-foreground">
            As notas não batem com o seu banco? Verificar conexão
          </summary>
          <div className="mt-3">
            <DiagnosticoConexao />
          </div>
        </details>
      )}
    </div>
  )
}

function ListaNotas({
  notas,
  selecionadaId,
  onSelecionar,
}: {
  notas: DocumentoFiscal[]
  selecionadaId: string | null
  onSelecionar: (n: DocumentoFiscal) => void
}) {
  return (
    <Card className="gap-0 overflow-hidden p-0">
      <ul className="divide-y divide-border">
        {notas.map((n) => {
          const { tom, rotulo, Icon } = statusVisual(n.status, n.statusDescricao)
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
                    Nº {n.numero || "—"} · Série {n.serie}
                  </span>
                  <span className="truncate text-xs text-muted-foreground">
                    {n.destinatarioNome || n.destinatarioDocumento || n.naturezaOperacao}
                  </span>
                </div>
                <div className="flex flex-col items-end gap-1">
                  <StatusBadge tom={tom}>{rotulo}</StatusBadge>
                  <span className="text-xs text-muted-foreground">{moeda(n.valorTotal)}</span>
                </div>
              </button>
            </li>
          )
        })}
      </ul>
    </Card>
  )
}

function DetalheNota({ nota, onMudou }: { nota: DocumentoFiscal; onMudou: () => void }) {
  const { tom, rotulo, Icon } = statusVisual(nota.status, nota.statusDescricao)
  const houveProblema = nota.status === 200 || nota.status === 300 || nota.status === 900
  const motivo = nota.motivoRetorno || nota.ultimoErro
  const [cancelarAberto, setCancelarAberto] = useState(false)
  const [corrigirAberto, setCorrigirAberto] = useState(false)
  const [reenviando, setReenviando] = useState(false)
  const [anexarAberto, setAnexarAberto] = useState(false)
  // XML aberto no visualizador inline (null = fechado).
  const [xmlView, setXmlView] = useState<XmlView | null>(null)

  // So notas autorizadas (status 100) podem ser canceladas.
  const podeCancelar = nota.status === 100 && !!nota.chaveAcesso

  // Carta de correcao tambem exige nota autorizada. As demais regras (limite de
  // 20, texto valido) sao validadas no backend/dialogo.
  const podeCorrigir = nota.status === 100 && !!nota.chaveAcesso

  // Uma nota NAO autorizada com XML pode ser reenviada. O backend regera o
  // emitente com o cadastro atual (CRT/IE/endereco) e reassina, entao corrigir o
  // cadastro e reenviar resolve rejeicoes como a 481. Cancelada nao reenvia.
  // Status finais nao reenviam: autorizada (100), denegada (300), cancelada
  // (400) e inutilizada (500). Antes o teste usava cStat da SEFAZ (101/135),
  // que nunca aparece em `status`, e liberava o reenvio de nota cancelada.
  const podeReenviar =
    !nota.autorizado && !STATUS_FINAIS.has(nota.status) && (nota.temXml ?? !!nota.chaveAcesso)

  async function reenviar() {
    setReenviando(true)
    try {
      const resp = await fetch(`/api/proxy/nfe/${nota.id}/reenviar`, { method: "POST" })
      const texto = await resp.text()
      let dado: Record<string, unknown> = {}
      try {
        dado = texto ? JSON.parse(texto) : {}
      } catch {
        dado = {}
      }

      if (resp.ok && (dado.autorizado || dado.status === 100)) {
        toast.success("Nota autorizada no reenvio")
      } else {
        // 422 devolve o proprio documento com o motivo atualizado da SEFAZ.
        const motivo =
          (dado.motivoRetorno as string) ||
          (dado.erro as string) ||
          (dado.ultimoErro as string) ||
          `A SEFAZ nao autorizou (HTTP ${resp.status}).`
        toast.error("Reenvio não autorizado", { description: motivo })
      }
    } catch (e) {
      toast.error("Falha ao reenviar", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setReenviando(false)
      onMudou()
    }
  }

  // Eventos fiscais oficiais (cancelamento/CC-e) da nota.
  const { data: eventosFiscais, mutate: recarregarEventos } = useSWR<EventoFiscal[]>(
    `nfe/${nota.id}/eventos`,
    fetcher,
  )

  return (
    <Card className="gap-4">
      <CardHeader className="flex-row items-center justify-between">
        <div className="flex items-center gap-2.5">
          <Icon className="size-5 text-muted-foreground" />
          <CardTitle className="text-base">
            Nota nº {nota.numero || "—"} · Série {nota.serie}
          </CardTitle>
        </div>
        <StatusBadge tom={tom}>{rotulo}</StatusBadge>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {/* Bloco de motivo: e a resposta para "por que deu certo/errado". */}
        {motivo && (
          <div
            className={`rounded-md border px-3 py-2.5 text-sm ${
              houveProblema
                ? "border-destructive/30 bg-destructive/10 text-destructive"
                : "border-border bg-muted/40 text-foreground"
            }`}
          >
            <div className="flex items-center gap-2">
              {nota.codigoStatusRetorno && (
                <span className="font-mono text-xs font-semibold">{nota.codigoStatusRetorno}</span>
              )}
              <span>{motivo}</span>
            </div>
          </div>
        )}

        <div className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
          <Campo rotulo="Chave de acesso" mono>{nota.chaveAcesso ?? "—"}</Campo>
          <Campo rotulo="Protocolo">{nota.protocolo ?? "—"}</Campo>
          <Campo rotulo="Destinatário">{nota.destinatarioNome ?? "—"}</Campo>
          <Campo rotulo="Documento">{nota.destinatarioDocumento ?? "—"}</Campo>
          <Campo rotulo="Natureza da operação">{nota.naturezaOperacao}</Campo>
          <Campo rotulo="Ambiente">{nota.ambienteDescricao}</Campo>
          <Campo rotulo="Valor total">{moeda(nota.valorTotal)}</Campo>
          <Campo rotulo="Autorização">{formatDateTime(nota.dataAutorizacao)}</Campo>
          <Campo rotulo="Criada em">{formatDateTime(nota.criadoEm)}</Campo>
          <Campo rotulo="Tentativas de envio">{String(nota.tentativasEnvio)}</Campo>
        </div>

        <div className="flex flex-wrap gap-2">
          <JsonRecebidoDialog
            path={`nfe/${nota.id}/json-recebido`}
            label="Ver JSON"
            variant="default"
          />
          {nota.autorizado && nota.chaveAcesso && nota.temXml && (
            <Button
              render={<a href={`/danfe/${nota.id}`} target="_blank" rel="noopener noreferrer" />}
              nativeButton={false}
              variant="default"
              size="sm"
            >
              <Printer data-icon="inline-start" />
              Imprimir DANFE
            </Button>
          )}
          {/* Autorizada sem XML (cadastrada pela consulta): o DANFE depende do
              XML, que a SEFAZ nao devolve — ele e anexado a partir do original. */}
          {nota.autorizado && nota.chaveAcesso && !nota.temXml && (
            <Button variant="default" size="sm" onClick={() => setAnexarAberto(true)}>
              <Upload data-icon="inline-start" />
              Anexar XML para o DANFE
            </Button>
          )}
          {/* Reenvio de nota nao autorizada: o backend regera o emitente com o
              cadastro atual e reassina antes de transmitir. Assim, corrigir o
              cadastro (ex.: CRT/regime) e reenviar resolve a rejeicao. */}
          {podeReenviar && (
            <Button variant="default" size="sm" onClick={reenviar} disabled={reenviando}>
              <RefreshCw data-icon="inline-start" className={reenviando ? "animate-spin" : undefined} />
              {reenviando ? "Reenviando..." : "Reenviar à SEFAZ"}
            </Button>
          )}
          {/* XML da nota (assinado enviado ou nfeProc autorizado). Abre um
              VISUALIZADOR inline: baixar via <a download> nao funciona dentro do
              preview/iframe, entao o conteudo e mostrado na tela para analise —
              com copiar e baixar dentro do proprio dialogo.
              Aparece tanto na nota autorizada quanto na rejeitada: usa temXml
              quando o backend informa e cai para "tem chave de acesso" como
              fallback (a chave so existe depois de assinar). */}
          {(nota.temXml ?? !!nota.chaveAcesso) && (
            <Button
              variant={nota.autorizado ? "outline" : "default"}
              size="sm"
              onClick={() =>
                setXmlView({
                  titulo: nota.autorizado ? "XML autorizado (nfeProc)" : "XML enviado à SEFAZ",
                  url: `/api/proxy/nfe/${nota.id}/xml`,
                  nomeArquivo: `nfe-${nota.numero || nota.id}${nota.autorizado ? "-procNFe" : "-enviado"}.xml`,
                })
              }
            >
              <FileCode data-icon="inline-start" />
              {nota.autorizado ? "Ver XML autorizado" : "Ver XML enviado"}
            </Button>
          )}
          {/* Resposta bruta da SEFAZ: o "log completo" para diagnosticar
              rejeicoes. Aparece sempre que ha retorno, autorizado ou nao. */}
          {nota.temRetornoSefaz && (
            <Button
              variant="outline"
              size="sm"
              onClick={() =>
                setXmlView({
                  titulo: "Resposta da SEFAZ",
                  url: `/api/proxy/nfe/${nota.id}/retorno-sefaz`,
                  nomeArquivo: `nfe-${nota.numero || nota.id}-retorno-sefaz.xml`,
                })
              }
            >
              <FileCode data-icon="inline-start" />
              Ver resposta da SEFAZ
            </Button>
          )}
          {podeCorrigir && (
            <Button variant="outline" size="sm" onClick={() => setCorrigirAberto(true)}>
              <PencilLine data-icon="inline-start" />
              Carta de correção
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
              Cancelar NF-e
            </Button>
          )}
        </div>

        {/* Eventos fiscais oficiais (cancelamento homologado etc.). */}
        {eventosFiscais && eventosFiscais.length > 0 && (
          <>
            <Separator />
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">Eventos fiscais</h3>
              <ul className="flex flex-col gap-2">
                {eventosFiscais.map((ev) => (
                  <li
                    key={ev.id}
                    className="flex items-start justify-between gap-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm"
                  >
                    <div className="flex min-w-0 flex-col gap-0.5">
                      <span className="font-medium">
                        {ev.tipoEventoDescricao}
                        {ev.tipoEvento === 110110 ? ` nº ${ev.sequenciaEvento}` : ""}
                      </span>
                      {ev.justificativa && (
                        <span className="text-xs text-muted-foreground">{ev.justificativa}</span>
                      )}
                      {ev.correcao && (
                        <span className="text-xs text-muted-foreground">{ev.correcao}</span>
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
                      {ev.homologado && ev.tipoEvento === 110111 && (
                        <>
                          <LinkDownload url={`/api/proxy/nfe/${nota.id}/cancelamento.xml`} nome={`nfe-${nota.numero || nota.id}-cancelamento.xml`}>
                            Baixar XML
                          </LinkDownload>
                          <LinkDownload url={`/api/proxy/nfe/${nota.id}/cancelamento.pdf`} nome={`nfe-${nota.numero || nota.id}-comprovante-cancelamento.pdf`}>
                            Comprovante PDF
                          </LinkDownload>
                          <LinkDownload url={`/api/proxy/nfe/${nota.id}/danfe-cancelamento.pdf`} nome={`nfe-${nota.numero || nota.id}-danfe-cancelada.pdf`}>
                            DANFE cancelada
                          </LinkDownload>
                        </>
                      )}
                      {ev.homologado && ev.tipoEvento === 110110 && (
                        <LinkDownload
                          url={`/api/proxy/nfe/${nota.id}/carta-correcao.xml?seq=${ev.sequenciaEvento}`}
                          nome={`nfe-${nota.numero || nota.id}-cce-${ev.sequenciaEvento}.xml`}
                        >
                          Baixar XML
                        </LinkDownload>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}

        {nota.eventos && nota.eventos.length > 0 && (
          <>
            <Separator />
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-medium">Trilha de eventos</h3>
              <ul className="flex flex-col gap-2">
                {nota.eventos.map((ev) => (
                  <li
                    key={ev.id}
                    className="flex items-start justify-between gap-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm"
                  >
                    <div className="flex min-w-0 flex-col">
                      <span className="font-medium">{ev.descricao}</span>
                      {ev.detalhe && (
                        <span className="text-xs text-muted-foreground">{ev.detalhe}</span>
                      )}
                    </div>
                    <span className="shrink-0 font-mono text-xs text-muted-foreground">
                      {formatDateTime(ev.ocorridoEm)}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}
      </CardContent>

      <DialogCancelamento
        nota={nota}
        aberto={cancelarAberto}
        onFechar={() => setCancelarAberto(false)}
        onCancelado={() => {
          recarregarEventos()
          onMudou()
        }}
      />

      <DialogAnexarXml
        nota={nota}
        aberto={anexarAberto}
        onFechar={() => setAnexarAberto(false)}
        onAnexado={onMudou}
      />

      <DialogCartaCorrecao
        nota={nota}
        aberto={corrigirAberto}
        onFechar={() => setCorrigirAberto(false)}
        onCorrigido={() => {
          recarregarEventos()
          onMudou()
        }}
      />

      <DialogXml view={xmlView} onFechar={() => setXmlView(null)} />
    </Card>
  )
}

const JUSTIFICATIVA_MIN = 15
const JUSTIFICATIVA_MAX = 255

function DialogCancelamento({
  nota,
  aberto,
  onFechar,
  onCancelado,
}: {
  nota: DocumentoFiscal
  aberto: boolean
  onFechar: () => void
  onCancelado: () => void
}) {
  const [justificativa, setJustificativa] = useState("")
  const [enviando, setEnviando] = useState(false)
  const tamanho = justificativa.trim().length
  const valido = tamanho >= JUSTIFICATIVA_MIN && tamanho <= JUSTIFICATIVA_MAX

  async function confirmar() {
    if (!valido || enviando) return
    setEnviando(true)
    try {
      const ev = await postJson<EventoFiscal>(`nfe/${nota.id}/cancelar`, {
        justificativa: justificativa.trim(),
      })
      if (ev.homologado) {
        toast.success("NF-e cancelada", {
          description: `Protocolo ${ev.protocoloEvento ?? "—"} · ${ev.motivo ?? ""}`,
        })
        setJustificativa("")
        onFechar()
        onCancelado()
      } else {
        // 422 vem como ApiError; este ramo cobre um raro 200 nao-homologado.
        toast.error("Cancelamento não homologado", {
          description: `${ev.codigoStatus ? ev.codigoStatus + " · " : ""}${ev.motivo ?? "Tente novamente."}`,
        })
      }
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
          <DialogTitle>Cancelar NF-e nº {nota.numero || "—"}</DialogTitle>
          <DialogDescription>
            O cancelamento é definitivo e enviado à SEFAZ como evento oficial. Informe a
            justificativa (entre {JUSTIFICATIVA_MIN} e {JUSTIFICATIVA_MAX} caracteres).
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-2">
          <Label htmlFor="justificativa-cancelamento">Justificativa</Label>
          <Textarea
            id="justificativa-cancelamento"
            value={justificativa}
            onChange={(e) => setJustificativa(e.target.value)}
            maxLength={JUSTIFICATIVA_MAX}
            rows={4}
            placeholder="Ex.: Erro na emissão da nota, dados do destinatário incorretos."
            disabled={enviando}
          />
          <span
            className={`text-xs ${tamanho > 0 && !valido ? "text-destructive" : "text-muted-foreground"}`}
          >
            {tamanho} / {JUSTIFICATIVA_MAX} caracteres
            {tamanho > 0 && tamanho < JUSTIFICATIVA_MIN
              ? ` · mínimo ${JUSTIFICATIVA_MIN}`
              : ""}
          </span>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={enviando}>
            Voltar
          </Button>
          <Button
            variant="destructive"
            onClick={confirmar}
            disabled={!valido || enviando}
          >
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

const CORRECAO_MIN = 15
const CORRECAO_MAX = 1000

function DialogCartaCorrecao({
  nota,
  aberto,
  onFechar,
  onCorrigido,
}: {
  nota: DocumentoFiscal
  aberto: boolean
  onFechar: () => void
  onCorrigido: () => void
}) {
  const [correcao, setCorrecao] = useState("")
  const [enviando, setEnviando] = useState(false)
  const tamanho = correcao.trim().length
  const valido = tamanho >= CORRECAO_MIN && tamanho <= CORRECAO_MAX

  async function confirmar() {
    if (!valido || enviando) return
    setEnviando(true)
    try {
      const ev = await postJson<EventoFiscal>(`nfe/${nota.id}/carta-correcao`, {
        correcao: correcao.trim(),
      })
      if (ev.homologado) {
        toast.success("Carta de correção registrada", {
          description: `Sequência ${ev.sequenciaEvento} · protocolo ${ev.protocoloEvento ?? "—"}`,
        })
        setCorrecao("")
        onFechar()
        onCorrigido()
      } else {
        toast.error("Correção não homologada", {
          description: `${ev.codigoStatus ? ev.codigoStatus + " · " : ""}${ev.motivo ?? "Tente novamente."}`,
        })
      }
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : "Falha ao enviar a correção. Tente novamente."
      toast.error("Não foi possível corrigir", { description: msg })
    } finally {
      setEnviando(false)
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !enviando ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Carta de correção · Nota nº {nota.numero || "—"}</DialogTitle>
          <DialogDescription>
            A carta de correção é um evento oficial da SEFAZ para sanar erros que{" "}
            <strong>não</strong> alterem valores, impostos, remetente/destinatário ou a data
            de emissão. Descreva a correção ({CORRECAO_MIN} a {CORRECAO_MAX} caracteres).
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-2">
          <Label htmlFor="texto-correcao">Texto da correção</Label>
          <Textarea
            id="texto-correcao"
            value={correcao}
            onChange={(e) => setCorrecao(e.target.value)}
            maxLength={CORRECAO_MAX}
            rows={5}
            placeholder="Ex.: No campo Informações Complementares, onde se lê 'à vista', leia-se 'a prazo em 30 dias'."
            disabled={enviando}
          />
          <span
            className={`text-xs ${tamanho > 0 && !valido ? "text-destructive" : "text-muted-foreground"}`}
          >
            {tamanho} / {CORRECAO_MAX} caracteres
            {tamanho > 0 && tamanho < CORRECAO_MIN ? ` · mínimo ${CORRECAO_MIN}` : ""}
          </span>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={enviando}>
            Voltar
          </Button>
          <Button onClick={confirmar} disabled={!valido || enviando}>
            {enviando ? (
              <>
                <RefreshCw data-icon="inline-start" className="animate-spin" />
                Enviando…
              </>
            ) : (
              <>
                <PencilLine data-icon="inline-start" />
                Enviar correção
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
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

/** Link de download que funciona dentro de iframe e mostra o erro do servidor. */
function LinkDownload({ url, nome, children }: { url: string; nome: string; children: React.ReactNode }) {
  const [baixando, setBaixando] = useState(false)
  return (
    <button
      type="button"
      disabled={baixando}
      onClick={async () => {
        setBaixando(true)
        try {
          await baixarArquivo(url, nome)
        } catch (e) {
          toast.error("Falha no download", { description: e instanceof Error ? e.message : undefined })
        } finally {
          setBaixando(false)
        }
      }}
      className="text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground disabled:opacity-50"
    >
      {baixando ? "Baixando…" : children}
    </button>
  )
}
