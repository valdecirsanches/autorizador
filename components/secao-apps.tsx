"use client"

import { useState } from "react"
import useSWR from "swr"
import { Check, Copy, KeyRound, Pause, Play, Plus, RefreshCw, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { fetcher, postJson, putJson, del, ApiError } from "@/lib/api"
import type { Empresa } from "@/lib/types"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Switch } from "@/components/ui/switch"
import { Skeleton } from "@/components/ui/skeleton"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Empty, EmptyDescription, EmptyHeader, EmptyTitle } from "@/components/ui/empty"
import { formatCnpj, formatDateTime } from "@/lib/format"

/** App cadastrado na API (GET /api/clientes). */
type ClienteApi = {
  id: string
  nome: string
  identificadorChave: string
  podeCadastrarEmpresas: boolean
  ativo: boolean
  empresaIds: string[]
  criadoEm: string
  atualizadoEm: string | null
  ultimoUsoEm: string | null
}

type RespostaComChave = { cliente: ClienteApi; chave: string; aviso: string }

type FormApp = { nome: string; empresaIds: string[]; podeCadastrarEmpresas: boolean }

const formVazio: FormApp = { nome: "", empresaIds: [], podeCadastrarEmpresas: false }

function mensagemErro(e: unknown) {
  return e instanceof Error ? e.message : undefined
}

/**
 * Chaves de acesso por app. Cada sistema que consome a API recebe a sua chave
 * ("nfeapp_..."), que so enxerga as empresas liberadas aqui e nao acessa rotas
 * administrativas. O painel continua usando o token mestre.
 */
export function SecaoApps() {
  const { data: apps, error, isLoading, mutate } = useSWR<ClienteApi[]>("clientes", fetcher)
  const { data: empresas } = useSWR<Empresa[]>("empresas", fetcher)
  const [editando, setEditando] = useState<ClienteApi | "novo" | null>(null)
  const [chaveGerada, setChaveGerada] = useState<{ nome: string; chave: string } | null>(null)
  const [excluindo, setExcluindo] = useState<ClienteApi | null>(null)
  const [ocupado, setOcupado] = useState<string | null>(null)

  const nomeEmpresa = new Map((empresas ?? []).map((e) => [e.id, e.razaoSocial]))

  async function alternarAtivo(app: ClienteApi) {
    setOcupado(app.id)
    try {
      await putJson(`clientes/${app.id}`, { ativo: !app.ativo })
      await mutate()
      toast.success(app.ativo ? `${app.nome} suspenso` : `${app.nome} reativado`)
    } catch (e) {
      toast.error("Não foi possível alterar o app", { description: mensagemErro(e) })
    } finally {
      setOcupado(null)
    }
  }

  async function novaChave(app: ClienteApi) {
    setOcupado(app.id)
    try {
      const r = await postJson<RespostaComChave>(`clientes/${app.id}/nova-chave`, {})
      await mutate()
      setChaveGerada({ nome: app.nome, chave: r.chave })
    } catch (e) {
      toast.error("Não foi possível gerar a nova chave", { description: mensagemErro(e) })
    } finally {
      setOcupado(null)
    }
  }

  const apiSemSuporte = error instanceof ApiError && error.status === 404

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <p className="max-w-2xl text-sm text-muted-foreground text-pretty">
          Cada sistema que emite notas por esta API recebe a <strong>sua própria chave</strong>. A chave
          só enxerga as empresas marcadas aqui e não acessa rotas administrativas (monitor, exclusão de
          empresa, pasta de XML, gestão de chaves). Suspender ou excluir um app corta o acesso dele na
          hora, sem afetar os outros.
        </p>
        <Button onClick={() => setEditando("novo")} disabled={apiSemSuporte}>
          <Plus data-icon="inline-start" />
          Novo app
        </Button>
      </div>

      {error ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>
              {apiSemSuporte ? "A API ainda não tem chaves por app" : "Não foi possível carregar os apps"}
            </EmptyTitle>
            <EmptyDescription>
              {apiSemSuporte
                ? "Publique a versão do backend .NET com o módulo de chaves por app (rota /api/clientes)."
                : String(error.message ?? "")}
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : isLoading ? (
        <Card className="p-4">
          <Skeleton className="h-40 w-full" />
        </Card>
      ) : (apps?.length ?? 0) === 0 ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Nenhum app cadastrado</EmptyTitle>
            <EmptyDescription>
              Crie um app para cada sistema integrado e entregue a chave ao desenvolvedor dele.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>App</TableHead>
                <TableHead>Chave</TableHead>
                <TableHead>Empresas liberadas</TableHead>
                <TableHead>Último uso</TableHead>
                <TableHead>Situação</TableHead>
                <TableHead className="text-right">Ações</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {apps!.map((app) => (
                <TableRow key={app.id}>
                  <TableCell className="font-medium">
                    {app.nome}
                    {app.podeCadastrarEmpresas && (
                      <span className="block text-xs font-normal text-muted-foreground">
                        Pode cadastrar empresas
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">
                    {app.identificadorChave}
                  </TableCell>
                  <TableCell className="max-w-64">
                    {app.empresaIds.length === 0 ? (
                      <span className="text-sm text-muted-foreground">Nenhuma</span>
                    ) : (
                      <span className="line-clamp-2 text-sm" title={app.empresaIds.map((id) => nomeEmpresa.get(id) ?? id).join("\n")}>
                        {app.empresaIds.map((id) => nomeEmpresa.get(id) ?? "Empresa removida").join(", ")}
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {app.ultimoUsoEm ? formatDateTime(app.ultimoUsoEm) : "Nunca"}
                  </TableCell>
                  <TableCell>
                    <StatusBadge tom={app.ativo ? "success" : "muted"}>
                      {app.ativo ? "Ativo" : "Suspenso"}
                    </StatusBadge>
                  </TableCell>
                  <TableCell>
                    <div className="flex justify-end gap-1">
                      <Button variant="ghost" size="sm" onClick={() => setEditando(app)} disabled={ocupado === app.id}>
                        Editar
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => void novaChave(app)}
                        disabled={ocupado === app.id}
                        aria-label={`Gerar nova chave para ${app.nome}`}
                        title="Gerar nova chave (a atual deixa de valer)"
                      >
                        <KeyRound className="size-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => void alternarAtivo(app)}
                        disabled={ocupado === app.id}
                        aria-label={app.ativo ? `Suspender ${app.nome}` : `Reativar ${app.nome}`}
                        title={app.ativo ? "Suspender" : "Reativar"}
                      >
                        {app.ativo ? <Pause className="size-4" /> : <Play className="size-4" />}
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => setExcluindo(app)}
                        disabled={ocupado === app.id}
                        aria-label={`Excluir ${app.nome}`}
                      >
                        <Trash2 className="size-4 text-destructive" />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}

      {editando !== null && (
        <DialogApp
          app={editando === "novo" ? null : editando}
          empresas={empresas ?? []}
          onFechar={() => setEditando(null)}
          onSalvo={async (r) => {
            await mutate()
            setEditando(null)
            if (r) setChaveGerada({ nome: r.cliente.nome, chave: r.chave })
          }}
        />
      )}

      <DialogChave dados={chaveGerada} onFechar={() => setChaveGerada(null)} />

      <DialogExcluir
        app={excluindo}
        onFechar={() => setExcluindo(null)}
        onExcluido={async () => {
          await mutate()
          setExcluindo(null)
        }}
      />
    </div>
  )
}

function DialogApp({
  app,
  empresas,
  onFechar,
  onSalvo,
}: {
  app: ClienteApi | null
  empresas: Empresa[]
  onFechar: () => void
  /** Recebe a chave quando o app acabou de ser criado. */
  onSalvo: (r: RespostaComChave | null) => Promise<void>
}) {
  const [form, setForm] = useState<FormApp>(
    app
      ? { nome: app.nome, empresaIds: app.empresaIds, podeCadastrarEmpresas: app.podeCadastrarEmpresas }
      : formVazio,
  )
  const [salvando, setSalvando] = useState(false)
  const [filtro, setFiltro] = useState("")

  const termo = filtro.trim().toLocaleLowerCase("pt-BR")
  const digitos = termo.replace(/\D/g, "")
  const visiveis = termo
    ? empresas.filter(
        (e) =>
          e.razaoSocial.toLocaleLowerCase("pt-BR").includes(termo) ||
          (digitos.length > 0 && e.cnpj.includes(digitos)),
      )
    : empresas

  function alternarEmpresa(id: string, marcada: boolean) {
    setForm((f) => ({
      ...f,
      empresaIds: marcada ? [...new Set([...f.empresaIds, id])] : f.empresaIds.filter((x) => x !== id),
    }))
  }

  async function salvar() {
    if (!form.nome.trim()) {
      toast.error("Informe o nome do app")
      return
    }
    setSalvando(true)
    try {
      const corpo = {
        nome: form.nome.trim(),
        empresaIds: form.empresaIds,
        podeCadastrarEmpresas: form.podeCadastrarEmpresas,
      }
      if (app) {
        await putJson(`clientes/${app.id}`, corpo)
        toast.success("App atualizado")
        await onSalvo(null)
      } else {
        const r = await postJson<RespostaComChave>("clientes", corpo)
        await onSalvo(r)
      }
    } catch (e) {
      toast.error("Não foi possível salvar o app", { description: mensagemErro(e) })
    } finally {
      setSalvando(false)
    }
  }

  return (
    <Dialog open onOpenChange={(o) => (!o && !salvando ? onFechar() : undefined)}>
      <DialogContent className="max-h-[90svh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{app ? `Editar ${app.nome}` : "Novo app"}</DialogTitle>
          <DialogDescription>
            {app
              ? "Alterar empresas e permissões vale na hora. A chave continua a mesma."
              : "A chave será exibida uma única vez depois de criar."}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <Label htmlFor="nome-app">Nome do app</Label>
            <Input
              id="nome-app"
              value={form.nome}
              onChange={(e) => setForm((f) => ({ ...f, nome: e.target.value }))}
              placeholder="Ex.: ERP Loja Centro"
              maxLength={120}
              disabled={salvando}
            />
          </div>

          <div className="flex flex-col gap-2">
            <div className="flex items-center justify-between gap-2">
              <Label>Empresas liberadas</Label>
              <span className="text-xs text-muted-foreground">{form.empresaIds.length} selecionada(s)</span>
            </div>
            {empresas.length > 6 && (
              <Input
                value={filtro}
                onChange={(e) => setFiltro(e.target.value)}
                placeholder="Filtrar por nome ou CNPJ"
                aria-label="Filtrar empresas"
              />
            )}
            <div className="max-h-56 overflow-y-auto rounded-md border border-border">
              {visiveis.length === 0 ? (
                <p className="px-3 py-4 text-sm text-muted-foreground">Nenhuma empresa encontrada.</p>
              ) : (
                <ul className="divide-y divide-border">
                  {visiveis.map((e) => {
                    const marcada = form.empresaIds.includes(e.id)
                    return (
                      <li key={e.id}>
                        <label className="flex cursor-pointer items-center gap-3 px-3 py-2 text-sm hover:bg-muted/50">
                          <input
                            type="checkbox"
                            className="size-4 accent-primary"
                            checked={marcada}
                            onChange={(ev) => alternarEmpresa(e.id, ev.target.checked)}
                            disabled={salvando}
                          />
                          <span className="min-w-0 flex-1 truncate">{e.razaoSocial}</span>
                          <span className="shrink-0 font-mono text-xs text-muted-foreground">{formatCnpj(e.cnpj)}</span>
                        </label>
                      </li>
                    )
                  })}
                </ul>
              )}
            </div>
          </div>

          <div className="flex items-start gap-3">
            <Switch
              id="pode-cadastrar"
              checked={form.podeCadastrarEmpresas}
              onCheckedChange={(v) => setForm((f) => ({ ...f, podeCadastrarEmpresas: v }))}
              disabled={salvando}
            />
            <Label htmlFor="pode-cadastrar" className="flex flex-col items-start gap-0.5 font-normal">
              <span className="text-sm font-medium">Pode cadastrar empresas</span>
              <span className="text-xs text-muted-foreground">
                Libera POST /api/empresas e /onboarding. Empresas criadas pelo app ficam liberadas para ele;
                um CNPJ de outro app continua bloqueado.
              </span>
            </Label>
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={salvando}>
            Cancelar
          </Button>
          <Button onClick={() => void salvar()} disabled={salvando}>
            {salvando && <RefreshCw data-icon="inline-start" className="animate-spin" />}
            {app ? "Salvar" : "Criar e gerar chave"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DialogChave({ dados, onFechar }: { dados: { nome: string; chave: string } | null; onFechar: () => void }) {
  const [copiada, setCopiada] = useState(false)

  async function copiar() {
    if (!dados) return
    try {
      await navigator.clipboard.writeText(dados.chave)
      setCopiada(true)
      window.setTimeout(() => setCopiada(false), 2000)
    } catch {
      toast.error("Não foi possível copiar. Selecione o texto e copie manualmente.")
    }
  }

  return (
    <Dialog open={dados !== null} onOpenChange={(o) => (!o ? onFechar() : undefined)}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Chave de {dados?.nome}</DialogTitle>
          <DialogDescription>
            Copie agora e entregue ao desenvolvedor do app. <strong>Ela não será exibida de novo</strong>: o
            servidor guarda só o hash. Se perder, gere uma nova.
          </DialogDescription>
        </DialogHeader>
        <pre className="overflow-x-auto rounded-md border border-border bg-muted p-3 font-mono text-xs select-all">
          {dados?.chave}
        </pre>
        <p className="text-xs text-muted-foreground">
          Uso: <span className="font-mono">Authorization: Bearer {"<chave>"}</span>. O app pode conferir o acesso em{" "}
          <span className="font-mono">GET /api/clientes/eu</span>.
        </p>
        <DialogFooter>
          <Button variant="outline" onClick={() => void copiar()}>
            {copiada ? <Check data-icon="inline-start" /> : <Copy data-icon="inline-start" />}
            {copiada ? "Copiada" : "Copiar chave"}
          </Button>
          <Button onClick={onFechar}>Já guardei</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DialogExcluir({
  app,
  onFechar,
  onExcluido,
}: {
  app: ClienteApi | null
  onFechar: () => void
  onExcluido: () => Promise<void>
}) {
  const [excluindo, setExcluindo] = useState(false)

  async function confirmar() {
    if (!app) return
    setExcluindo(true)
    try {
      await del(`clientes/${app.id}`)
      toast.success(`${app.nome} excluído`)
      await onExcluido()
    } catch (e) {
      toast.error("Não foi possível excluir", { description: mensagemErro(e) })
    } finally {
      setExcluindo(false)
    }
  }

  return (
    <Dialog open={app !== null} onOpenChange={(o) => (!o && !excluindo ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Excluir {app?.nome}?</DialogTitle>
          <DialogDescription>
            A chave deixa de funcionar imediatamente e o app perde o acesso a todas as empresas. Para cortar o
            acesso só temporariamente, use Suspender.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={excluindo}>
            Cancelar
          </Button>
          <Button variant="destructive" onClick={() => void confirmar()} disabled={excluindo}>
            <Trash2 data-icon="inline-start" />
            Excluir
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
