"use client"

import { useMemo, useState } from "react"
import useSWR from "swr"
import { Activity, AlertCircle, Check, ChevronDown, Clock3, Eye, RefreshCw, Search } from "lucide-react"
import { ApiError, fetcher } from "@/lib/api"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

type ApiLog = {
  id: number
  ocorridoEm: string
  usuario: string
  ip: string | null
  metodo: string
  rota: string
  statusHttp: number
  duracaoMs: number
  requisicaoJson: string | null
  resposta: string | null
  truncado: boolean
}

type LogsResponse = {
  registros: ApiLog[]
  proximoCursor: number | null
}

const PAGE_SIZE = 250
const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "medium",
  timeZone: "America/Sao_Paulo",
})

function formatPayload(payload: string | null) {
  if (!payload) return "Sem conteúdo capturado."
  try {
    return JSON.stringify(JSON.parse(payload), null, 2)
  } catch {
    return payload
  }
}

function statusVariant(status: number): "default" | "secondary" | "destructive" {
  if (status >= 500) return "destructive"
  if (status >= 400) return "secondary"
  return "default"
}

export function ApiMonitorDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const [search, setSearch] = useState("")
  const [olderLogs, setOlderLogs] = useState<ApiLog[]>([])
  // undefined = ainda nao carregou paginas antigas (usa o cursor da 1a pagina);
  // null = chegou ao fim. Antes, null caia no cursor da 1a pagina e o botao
  // "Carregar anteriores" reaparecia para sempre, recarregando a mesma pagina.
  const [nextCursor, setNextCursor] = useState<number | null | undefined>(undefined)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [olderError, setOlderError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ApiLog | null>(null)

  const { data, error, isLoading, isValidating, mutate } = useSWR<LogsResponse>(
    open ? `monitor/logs?limit=${PAGE_SIZE}` : null,
    fetcher,
    { refreshInterval: 15_000, revalidateOnFocus: true },
  )

  const allLogs = useMemo(() => {
    const unique = new Map<number, ApiLog>()
    for (const entry of [...(data?.registros ?? []), ...olderLogs]) unique.set(entry.id, entry)
    return [...unique.values()].sort((a, b) => b.id - a.id)
  }, [data?.registros, olderLogs])

  const filteredLogs = useMemo(() => {
    const term = search.trim().toLocaleLowerCase("pt-BR")
    if (!term) return allLogs
    return allLogs.filter((entry) =>
      [entry.usuario, entry.ip, entry.metodo, entry.rota, String(entry.statusHttp)]
        .filter(Boolean)
        .some((value) => value!.toLocaleLowerCase("pt-BR").includes(term)),
    )
  }, [allLogs, search])

  const failures = allLogs.filter((entry) => entry.statusHttp >= 400).length
  const cursor = nextCursor === undefined ? (data?.proximoCursor ?? null) : nextCursor
  const monitorUnauthorized = error instanceof ApiError && error.status === 401

  async function loadOlder() {
    if (cursor === null || loadingOlder) return
    setLoadingOlder(true)
    try {
      const response = await fetcher<LogsResponse>(
        `monitor/logs?limit=${PAGE_SIZE}&beforeId=${cursor}`,
      )
      setOlderLogs((current) => [...current, ...response.registros])
      setNextCursor(response.proximoCursor)
      setOlderError(null)
    } catch (loadError) {
      setOlderError(loadError instanceof Error ? loadError.message : "Não foi possível carregar os logs anteriores.")
    } finally {
      setLoadingOlder(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(aberto) => {
        // Ao fechar, descarta a paginacao e o detalhe: reabrir comeca do topo.
        if (!aberto) {
          setOlderLogs([])
          setNextCursor(undefined)
          setOlderError(null)
          setSelected(null)
        }
        onOpenChange(aberto)
      }}
    >
      <DialogContent className="flex max-h-[90dvh] w-[calc(100vw-1rem)] max-w-[calc(100vw-1rem)] flex-col gap-5 overflow-hidden p-4 sm:p-6 md:w-[min(96vw,1200px)] md:max-w-[1200px]">
        <DialogHeader className="shrink-0 pr-8">
          <DialogTitle className="flex items-center gap-2 text-lg">
            <Activity aria-hidden />
            Monitor geral da API
          </DialogTitle>
          <DialogDescription>
            Mostra falhas HTTP, operações que alteram dados e consultas lentas (2 s ou mais). Consultas comuns e chamadas do próprio monitor são ocultadas.
          </DialogDescription>
        </DialogHeader>

        <div className="flex shrink-0 flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
            <Badge variant="secondary">{allLogs.length} registros relevantes</Badge>
            <Badge variant={failures ? "destructive" : "outline"}>{failures} respostas com erro</Badge>
            <span className="inline-flex items-center gap-1.5 text-xs">
              <Clock3 aria-hidden />
              Atualiza a cada 15 s
            </span>
          </div>
          <div className="flex w-full gap-2 lg:w-auto">
            <div className="relative min-w-0 flex-1 lg:w-64 lg:flex-none">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input
                aria-label="Buscar nos logs por usuário, rota, IP ou status"
                placeholder="Buscar acessos..."
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                className="pl-9"
              />
            </div>
            <Button
              type="button"
              variant="outline"
              size="icon"
              aria-label="Atualizar logs"
              onClick={() => void mutate()}
              disabled={isValidating}
            >
              <RefreshCw className={isValidating ? "animate-spin" : undefined} aria-hidden />
            </Button>
          </div>
        </div>

        {error || olderError ? (
          <Alert variant="destructive" className="shrink-0">
            <AlertCircle aria-hidden />
            <AlertTitle>{monitorUnauthorized ? "Token da API recusado" : "Não foi possível carregar os logs"}</AlertTitle>
            <AlertDescription>
              {monitorUnauthorized
                ? "Confira a seção Configuração da API e execute o diagnóstico da conexão. O monitor só consegue ler os registros quando o token do painel coincide com o token aceito pelo backend."
                : error?.message ?? olderError}
            </AlertDescription>
          </Alert>
        ) : null}

        <div className="min-h-0 flex-1 overflow-auto rounded-lg border">
          {isLoading ? (
            <div className="flex min-h-52 items-center justify-center text-sm text-muted-foreground">
              Carregando acessos...
            </div>
          ) : error || olderError ? (
            <div className="flex min-h-52 items-center justify-center px-4 text-center text-sm text-muted-foreground">
              Não foi possível consultar os registros. Corrija a conexão e atualize o monitor.
            </div>
          ) : filteredLogs.length === 0 ? (
            <Empty className="min-h-56 border-0">
              <EmptyHeader>
                <EmptyMedia variant="icon"><Activity aria-hidden /></EmptyMedia>
                <EmptyTitle>{search ? "Nenhum resultado" : "Nenhum evento relevante"}</EmptyTitle>
                <EmptyDescription>
                  {search
                    ? "Tente outro termo de busca."
                    : "Salvar a configuração do painel não chama a API. Se o backend recebeu solicitações e o monitor continua vazio, publique a versão do backend .NET com a auditoria ativa."}
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          ) : (
            <Table>
              <TableHeader className="sticky top-0 z-10 bg-muted/95 backdrop-blur">
                <TableRow>
                  <TableHead>Data e hora</TableHead>
                  <TableHead>Usuário</TableHead>
                  <TableHead>Método / URL completa</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Duração</TableHead>
                  <TableHead className="text-right">Detalhes</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredLogs.map((entry) => (
                  <TableRow key={entry.id}>
                    <TableCell className="text-muted-foreground">
                      {dateFormatter.format(new Date(entry.ocorridoEm))}
                    </TableCell>
                    <TableCell>
                      <div className="max-w-40 truncate font-medium">{entry.usuario}</div>
                      {entry.ip ? <div className="text-xs text-muted-foreground">{entry.ip}</div> : null}
                    </TableCell>
                    <TableCell>
                      <div className="flex max-w-[520px] items-start gap-2">
                        <Badge variant="outline" className="shrink-0">{entry.metodo}</Badge>
                        <span className="break-all font-mono text-xs" title={entry.rota}>{entry.rota}</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant={statusVariant(entry.statusHttp)}>
                        {entry.statusHttp === 102 ? "Em andamento" : entry.statusHttp}
                      </Badge>
                    </TableCell>
                    <TableCell>{entry.duracaoMs} ms</TableCell>
                    <TableCell className="text-right">
                      <Button type="button" size="sm" variant="ghost" onClick={() => setSelected(entry)}>
                        <Eye data-icon="inline-start" aria-hidden />
                        Ver
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>

        <div className="flex shrink-0 flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-muted-foreground">
            {data?.registros.length === PAGE_SIZE || olderLogs.length > 0
              ? "Exibindo do mais recente para o mais antigo. Conteúdos maiores que 32 KB são truncados."
              : "Conteúdos maiores que 32 KB são truncados."}
          </p>
          {cursor !== null ? (
            <Button type="button" variant="outline" size="sm" onClick={() => void loadOlder()} disabled={loadingOlder}>
              {loadingOlder ? <RefreshCw data-icon="inline-start" className="animate-spin" aria-hidden /> : <ChevronDown data-icon="inline-start" aria-hidden />}
              Carregar anteriores
            </Button>
          ) : null}
        </div>

        {selected ? (
          <div className="absolute inset-0 flex flex-col overflow-hidden rounded-xl bg-popover p-5 sm:p-6" role="region" aria-label={`Detalhes do acesso ${selected.id}`}>
            <div className="flex items-start justify-between gap-4 border-b pb-4">
              <div className="min-w-0">
                <h3 className="font-semibold">Detalhes do acesso #{selected.id}</h3>
                <p className="mt-1 break-all font-mono text-xs text-muted-foreground">{selected.metodo} {selected.rota}</p>
              </div>
              <Button type="button" variant="outline" size="sm" onClick={() => setSelected(null)}>
                <Check data-icon="inline-start" aria-hidden />
                Voltar aos logs
              </Button>
            </div>
            <div className="grid min-h-0 flex-1 gap-4 overflow-auto py-4 lg:grid-cols-2">
              <section className="flex min-h-0 flex-col gap-2">
                <div className="flex items-center justify-between gap-2">
                  <h4 className="text-sm font-medium">Requisição enviada</h4>
                  <Badge variant="outline">HTTP {selected.statusHttp}</Badge>
                </div>
                <pre className="min-h-40 flex-1 overflow-auto rounded-lg bg-muted p-3 text-xs leading-relaxed whitespace-pre-wrap break-words">{formatPayload(selected.requisicaoJson)}</pre>
              </section>
              <section className="flex min-h-0 flex-col gap-2">
                <div className="flex items-center justify-between gap-2">
                  <h4 className="text-sm font-medium">Resposta da API</h4>
                  <span className="text-xs text-muted-foreground">{selected.duracaoMs} ms</span>
                </div>
                <pre className="min-h-40 flex-1 overflow-auto rounded-lg bg-muted p-3 text-xs leading-relaxed whitespace-pre-wrap break-words">{formatPayload(selected.resposta)}</pre>
              </section>
            </div>
            {selected.truncado ? (
              <p className="shrink-0 border-t pt-3 text-xs text-muted-foreground">O conteúdo foi truncado pelo limite de captura de 32 KB.</p>
            ) : null}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

export function ApiMonitorTrigger({ onClick }: { onClick: () => void }) {
  return (
    <Button type="button" variant="outline" size="sm" onClick={onClick}>
      <Activity data-icon="inline-start" aria-hidden />
      Monitor da API
    </Button>
  )
}
