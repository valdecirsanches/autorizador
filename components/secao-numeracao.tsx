"use client"

import { useEffect, useState } from "react"
import useSWR from "swr"
import { Hash, ListOrdered, Plus, Ban } from "lucide-react"
import { toast } from "sonner"
import { fetcher, putJson, postJson, ApiError } from "@/lib/api"
import { MODELOS, type Ambiente, type SerieNumeracao, type Inutilizacao } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Field, FieldGroup, FieldLabel, FieldDescription } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Textarea } from "@/components/ui/textarea"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { StatusBadge } from "./status-badge"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"

export function SecaoNumeracao() {
  const { empresaId, empresa, ambiente } = useEmpresa()
  const {
    data: series,
    isLoading,
    mutate,
  } = useSWR<SerieNumeracao[]>(empresaId ? `empresas/${empresaId}/series` : null, fetcher)
  const {
    data: inutilizacoes,
    isLoading: carregandoInut,
    mutate: mutarInut,
  } = useSWR<Inutilizacao[]>(
    empresaId ? `empresas/${empresaId}/inutilizacoes` : null,
    fetcher,
  )
  const [aberto, setAberto] = useState(false)
  const [inutAberto, setInutAberto] = useState(false)

  if (!empresaId) {
    return (
      <Empty>
        <EmptyHeader>
          <EmptyTitle>Selecione uma empresa</EmptyTitle>
          <EmptyDescription>
            A numeração é controlada por empresa, modelo, série e ambiente. Escolha uma
            empresa no topo para ver e inicializar seus contadores.
          </EmptyDescription>
        </EmptyHeader>
      </Empty>
    )
  }

  return (
    <div className="flex flex-col gap-6">
      <Card>
        <CardHeader className="flex-row items-center justify-between gap-3">
          <div className="flex items-center gap-2.5">
            <ListOrdered className="size-5 text-muted-foreground" />
            <CardTitle className="text-base">
              Numeração de {empresa?.razaoSocial ?? "—"}
            </CardTitle>
          </div>
          <Button size="sm" onClick={() => setAberto(true)}>
            <Plus data-icon="inline-start" />
            Inicializar numeração
          </Button>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <p className="text-sm text-muted-foreground">
            A numeração é controlada pelo seu sistema (o número vai no pedido de emissão). O
            <strong> último número</strong> abaixo reflete a maior NF-e efetivamente emitida
            (autorizada, cancelada ou denegada) em cada série e ambiente. Use a inicialização
            apenas ao migrar de outro sistema, informando qual será o próximo número.
          </p>

          {isLoading ? (
            <div className="flex flex-col gap-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : !series || series.length === 0 ? (
            <Empty>
              <EmptyHeader>
                <EmptyTitle>Nenhuma numeração ainda</EmptyTitle>
                <EmptyDescription>
                  Esta empresa ainda não tem NF-e emitida nem contador inicializado. Assim que
                  a primeira nota for autorizada, a série aparece aqui com o último número
                  usado. Você também pode inicializar um valor específico agora (migração).
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Modelo</TableHead>
                  <TableHead>Série</TableHead>
                  <TableHead>Ambiente</TableHead>
                  <TableHead className="text-right">Último número</TableHead>
                  <TableHead className="text-right">Próximo número</TableHead>
                  <TableHead>Atualizado em</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {series.map((s) => (
                  <TableRow key={`${s.modelo}-${s.serie}-${s.ambiente}`}>
                    <TableCell>{MODELOS[s.modelo] ?? s.modelo}</TableCell>
                    <TableCell className="font-mono">{s.serie}</TableCell>
                    <TableCell>
                      <StatusBadge tom={s.ambiente === 1 ? "success" : "muted"}>
                        {s.ambiente === 1 ? "Produção" : "Homologação"}
                      </StatusBadge>
                    </TableCell>
                    <TableCell className="text-right font-mono">{s.ultimoNumero}</TableCell>
                    <TableCell className="text-right font-mono font-semibold">
                      {s.proximoNumero}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {formatDateTime(s.atualizadoEm)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex-row items-center justify-between gap-3">
          <div className="flex items-center gap-2.5">
            <Ban className="size-5 text-muted-foreground" />
            <CardTitle className="text-base">Inutilização de numeração</CardTitle>
          </div>
          <Button
            size="sm"
            variant="outline"
            className="text-destructive hover:text-destructive"
            onClick={() => setInutAberto(true)}
          >
            <Ban data-icon="inline-start" />
            Inutilizar faixa
          </Button>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <p className="text-sm text-muted-foreground">
            Declare à SEFAZ que uma faixa de números <strong>não</strong> será usada — por
            exemplo, quando há quebra de sequência por falha de sistema. A inutilização é{" "}
            <strong>irreversível</strong> e não pode conter notas já autorizadas ou canceladas.
          </p>

          {carregandoInut ? (
            <div className="flex flex-col gap-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : !inutilizacoes || inutilizacoes.length === 0 ? (
            <Empty>
              <EmptyHeader>
                <EmptyTitle>Nenhuma inutilização registrada</EmptyTitle>
                <EmptyDescription>
                  As faixas inutilizadas aparecerão aqui, com o protocolo e a situação
                  retornada pela SEFAZ.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Modelo</TableHead>
                  <TableHead>Série</TableHead>
                  <TableHead>Ambiente</TableHead>
                  <TableHead>Faixa</TableHead>
                  <TableHead>Situação</TableHead>
                  <TableHead>Protocolo</TableHead>
                  <TableHead>Registrada em</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {inutilizacoes.map((i) => (
                  <TableRow key={i.id}>
                    <TableCell>{MODELOS[i.modelo] ?? i.modelo}</TableCell>
                    <TableCell className="font-mono">{i.serie}</TableCell>
                    <TableCell>
                      <StatusBadge tom={i.ambiente === 1 ? "success" : "muted"}>
                        {i.ambiente === 1 ? "Produção" : "Homologação"}
                      </StatusBadge>
                    </TableCell>
                    <TableCell className="font-mono">
                      {i.numeroInicial}–{i.numeroFinal}
                    </TableCell>
                    <TableCell>
                      <StatusBadge tom={i.homologado ? "success" : "destructive"}>
                        {i.homologado ? "Homologada" : `Rejeitada${i.codigoStatus ? ` (${i.codigoStatus})` : ""}`}
                      </StatusBadge>
                    </TableCell>
                    <TableCell className="font-mono text-muted-foreground">
                      {i.protocolo ?? "—"}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {formatDateTime(i.criadoEm)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <DialogInicializar
        empresaId={empresaId}
        ambientePadrao={ambiente}
        aberto={aberto}
        onFechar={() => setAberto(false)}
        onSalvo={() => void mutate()}
      />

      <DialogInutilizar
        empresaId={empresaId}
        uf={empresa?.uf ?? ""}
        cnpj={empresa?.cnpj ?? ""}
        ufAutorizadorOverride={empresa?.ufAutorizadorOverride ?? null}
        ambientePadrao={ambiente}
        aberto={inutAberto}
        onFechar={() => setInutAberto(false)}
        onSalvo={() => void mutarInut()}
      />
    </div>
  )
}

function DialogInicializar({
  empresaId,
  ambientePadrao,
  aberto,
  onFechar,
  onSalvo,
}: {
  empresaId: string
  ambientePadrao: Ambiente
  aberto: boolean
  onFechar: () => void
  onSalvo: () => void
}) {
  const [modelo, setModelo] = useState("55")
  const [serie, setSerie] = useState("1")
  const [ambiente, setAmbiente] = useState(String(ambientePadrao))
  // O dialogo fica montado: ao abrir, segue o ambiente escolhido no topo
  // (antes ficava com o valor da primeira renderizacao).
  useEffect(() => {
    if (aberto) setAmbiente(String(ambientePadrao))
  }, [aberto, ambientePadrao])
  const [proximo, setProximo] = useState("")
  const [salvando, setSalvando] = useState(false)

  const serieNum = Number(serie)
  const proximoNum = Number(proximo)
  const valido =
    Number.isInteger(serieNum) &&
    serieNum >= 0 &&
    serieNum <= 999 &&
    Number.isInteger(proximoNum) &&
    proximoNum >= 1

  async function salvar() {
    if (!valido || salvando) return
    setSalvando(true)
    try {
      await putJson<SerieNumeracao>(`empresas/${empresaId}/series`, {
        modelo: Number(modelo),
        serie: serieNum,
        ambiente: Number(ambiente),
        proximoNumero: proximoNum,
      })
      toast.success("Numeração inicializada", {
        description: `Próxima nota (modelo ${modelo}, série ${serie}) será a nº ${proximoNum}.`,
      })
      setProximo("")
      onFechar()
      onSalvo()
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : "Falha ao salvar a numeração."
      toast.error("Não foi possível inicializar", { description: msg })
    } finally {
      setSalvando(false)
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !salvando ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Inicializar numeração</DialogTitle>
          <DialogDescription>
            Defina qual será o próximo número a emitir para a combinação de modelo, série e
            ambiente. Comum ao migrar de outro sistema.
          </DialogDescription>
        </DialogHeader>

        <FieldGroup>
          <div className="grid grid-cols-2 gap-4">
            <Field>
              <FieldLabel htmlFor="modelo-num">Modelo</FieldLabel>
              <Select value={modelo} onValueChange={(v) => v && setModelo(v)} disabled={salvando}>
                <SelectTrigger id="modelo-num">
                  <SelectValue>{MODELOS[Number(modelo)] ?? modelo}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(MODELOS).map(([cod, rot]) => (
                    <SelectItem key={cod} value={cod}>
                      {rot}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel htmlFor="ambiente-num">Ambiente</FieldLabel>
              <Select value={ambiente} onValueChange={(v) => v && setAmbiente(v)} disabled={salvando}>
                <SelectTrigger id="ambiente-num">
                  <SelectValue>{ambiente === "1" ? "Produção" : "Homologação"}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="2">Homologação</SelectItem>
                  <SelectItem value="1">Produção</SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel htmlFor="serie-num">Série</FieldLabel>
              <Input
                id="serie-num"
                type="number"
                min={0}
                max={999}
                value={serie}
                onChange={(e) => setSerie(e.target.value)}
                disabled={salvando}
              />
              <FieldDescription>0 a 999</FieldDescription>
            </Field>

            <Field>
              <FieldLabel htmlFor="proximo-num">Próximo número</FieldLabel>
              <Input
                id="proximo-num"
                type="number"
                min={1}
                value={proximo}
                onChange={(e) => setProximo(e.target.value)}
                placeholder="Ex.: 1001"
                disabled={salvando}
              />
              <FieldDescription>Número da próxima nota</FieldDescription>
            </Field>
          </div>
        </FieldGroup>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={salvando}>
            Voltar
          </Button>
          <Button onClick={salvar} disabled={!valido || salvando}>
            <Hash data-icon="inline-start" />
            {salvando ? "Salvando…" : "Salvar numeração"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

const JUST_MIN = 15
const JUST_MAX = 255

function DialogInutilizar({
  empresaId,
  uf,
  cnpj,
  ufAutorizadorOverride,
  ambientePadrao,
  aberto,
  onFechar,
  onSalvo,
}: {
  empresaId: string
  uf: string
  cnpj: string
  ufAutorizadorOverride: string | null
  ambientePadrao: Ambiente
  aberto: boolean
  onFechar: () => void
  onSalvo: () => void
}) {
  const anoCorrente = new Date().getFullYear()
  const [modelo, setModelo] = useState("55")
  const [serie, setSerie] = useState("1")
  const [ambiente, setAmbiente] = useState(String(ambientePadrao))
  // O dialogo fica montado: ao abrir, segue o ambiente escolhido no topo
  // (antes ficava com o valor da primeira renderizacao).
  useEffect(() => {
    if (aberto) setAmbiente(String(ambientePadrao))
  }, [aberto, ambientePadrao])
  const [ano, setAno] = useState(String(anoCorrente))
  const [inicial, setInicial] = useState("")
  const [final, setFinal] = useState("")
  const [justificativa, setJustificativa] = useState("")
  const [enviando, setEnviando] = useState(false)

  const serieNum = Number(serie)
  const iniNum = Number(inicial)
  const fimNum = Number(final)
  const anoNum = Number(ano)
  const justLen = justificativa.trim().length
  const valido =
    uf.length === 2 &&
    cnpj.replace(/\D/g, "").length === 14 &&
    Number.isInteger(serieNum) &&
    serieNum >= 0 &&
    serieNum <= 999 &&
    Number.isInteger(iniNum) &&
    iniNum >= 1 &&
    Number.isInteger(fimNum) &&
    fimNum >= iniNum &&
    Number.isInteger(anoNum) &&
    justLen >= JUST_MIN &&
    justLen <= JUST_MAX

  const quantidade = valido ? fimNum - iniNum + 1 : 0

  async function confirmar() {
    if (!valido || enviando) return
    setEnviando(true)
    try {
      const r = await postJson<Inutilizacao>(`empresas/${empresaId}/inutilizacoes`, {
        uf,
        cnpj: cnpj.replace(/\D/g, ""),
        ufAutorizadorOverride,
        modelo: Number(modelo),
        serie: serieNum,
        ambiente: Number(ambiente),
        ano: anoNum,
        numeroInicial: iniNum,
        numeroFinal: fimNum,
        justificativa: justificativa.trim(),
      })
      if (r.homologado) {
        toast.success("Faixa inutilizada", {
          description: `Números ${r.numeroInicial}–${r.numeroFinal} · protocolo ${r.protocolo ?? "—"}`,
        })
        setInicial("")
        setFinal("")
        setJustificativa("")
        onFechar()
        onSalvo()
      } else {
        toast.error("Inutilização não homologada", {
          description: `${r.codigoStatus ? r.codigoStatus + " · " : ""}${r.motivo ?? "Tente novamente."}`,
        })
        onSalvo()
      }
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : "Falha ao inutilizar a faixa."
      toast.error("Não foi possível inutilizar", { description: msg })
    } finally {
      setEnviando(false)
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={(o) => (!o && !enviando ? onFechar() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Inutilizar faixa de numeração</DialogTitle>
          <DialogDescription>
            Operação <strong>irreversível</strong>: declara à SEFAZ que estes números não
            serão usados. A faixa não pode conter notas já autorizadas ou canceladas.
          </DialogDescription>
        </DialogHeader>

        <FieldGroup>
          <div className="grid grid-cols-2 gap-4">
            <Field>
              <FieldLabel htmlFor="modelo-inut">Modelo</FieldLabel>
              <Select value={modelo} onValueChange={(v) => v && setModelo(v)} disabled={enviando}>
                <SelectTrigger id="modelo-inut">
                  <SelectValue>{MODELOS[Number(modelo)] ?? modelo}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(MODELOS).map(([cod, rot]) => (
                    <SelectItem key={cod} value={cod}>
                      {rot}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel htmlFor="ambiente-inut">Ambiente</FieldLabel>
              <Select value={ambiente} onValueChange={(v) => v && setAmbiente(v)} disabled={enviando}>
                <SelectTrigger id="ambiente-inut">
                  <SelectValue>{ambiente === "1" ? "Produção" : "Homologação"}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="2">Homologação</SelectItem>
                  <SelectItem value="1">Produção</SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel htmlFor="serie-inut">Série</FieldLabel>
              <Input
                id="serie-inut"
                type="number"
                min={0}
                max={999}
                value={serie}
                onChange={(e) => setSerie(e.target.value)}
                disabled={enviando}
              />
              <FieldDescription>0 a 999</FieldDescription>
            </Field>

            <Field>
              <FieldLabel htmlFor="ano-inut">Ano</FieldLabel>
              <Input
                id="ano-inut"
                type="number"
                value={ano}
                onChange={(e) => setAno(e.target.value)}
                disabled={enviando}
              />
              <FieldDescription>Ano da numeração</FieldDescription>
            </Field>

            <Field>
              <FieldLabel htmlFor="ini-inut">Número inicial</FieldLabel>
              <Input
                id="ini-inut"
                type="number"
                min={1}
                value={inicial}
                onChange={(e) => setInicial(e.target.value)}
                placeholder="Ex.: 105"
                disabled={enviando}
              />
            </Field>

            <Field>
              <FieldLabel htmlFor="fim-inut">Número final</FieldLabel>
              <Input
                id="fim-inut"
                type="number"
                min={1}
                value={final}
                onChange={(e) => setFinal(e.target.value)}
                placeholder="Ex.: 110"
                disabled={enviando}
              />
            </Field>
          </div>

          <Field>
            <FieldLabel htmlFor="just-inut">Justificativa</FieldLabel>
            <Textarea
              id="just-inut"
              value={justificativa}
              onChange={(e) => setJustificativa(e.target.value)}
              maxLength={JUST_MAX}
              rows={3}
              placeholder="Ex.: Quebra de sequência por falha no sistema emissor durante contingência."
              disabled={enviando}
            />
            <FieldDescription>
              {justLen} / {JUST_MAX} caracteres
              {justLen > 0 && justLen < JUST_MIN ? ` · mínimo ${JUST_MIN}` : ""}
              {quantidade > 0 ? ` · ${quantidade} número(s) na faixa` : ""}
            </FieldDescription>
          </Field>
        </FieldGroup>

        <DialogFooter>
          <Button variant="outline" onClick={onFechar} disabled={enviando}>
            Voltar
          </Button>
          <Button
            variant="destructive"
            onClick={confirmar}
            disabled={!valido || enviando}
          >
            <Ban data-icon="inline-start" />
            {enviando ? "Enviando…" : "Confirmar inutilização"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
