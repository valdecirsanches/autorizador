"use client"

import { useState } from "react"
import { Search, FileCheck2, FileX2, Ban, Copy, Check, Download } from "lucide-react"
import { toast } from "sonner"
import { postJson, ApiError } from "@/lib/api"
import type { ConsultaNfse, PadraoNfse } from "@/lib/types-nfse"
import { formatChaveNfse, PADROES_NFSE } from "@/lib/types-nfse"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Separator } from "@/components/ui/separator"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatDateTime } from "@/lib/format"

const limparChave = (v: string) => v.replace(/\D/g, "").slice(0, 50)

type Tom = "success" | "warning" | "destructive" | "muted"

function veredito(r: ConsultaNfse): { tom: Tom; rotulo: string; Icon: typeof FileCheck2 } {
  if (!r.encontrada) return { tom: "destructive", rotulo: "Não encontrada", Icon: FileX2 }
  if (r.cancelada) return { tom: "warning", rotulo: "Cancelada", Icon: Ban }
  return { tom: "success", rotulo: "Autorizada", Icon: FileCheck2 }
}

export function SecaoNfseConsulta() {
  const { empresas, empresaId, empresa, ambiente: ambienteEmpresa } = useEmpresa()
  const [chave, setChave] = useState("")
  const [padrao, setPadrao] = useState<PadraoNfse>(1)
  const [ambienteSel, setAmbienteSel] = useState<1 | 2 | null>(null)
  const ambiente = ambienteSel ?? ambienteEmpresa
  const [osascoChave, setOsascoChave] = useState("")
  const [abrasfUrl, setAbrasfUrl] = useState("")
  const [abrasfUsuario, setAbrasfUsuario] = useState("")
  const [abrasfSenha, setAbrasfSenha] = useState("")
  const [consultando, setConsultando] = useState(false)
  const [resultado, setResultado] = useState<ConsultaNfse | null>(null)

  async function consultar() {
    if (!empresaId) {
      toast.error("Selecione a empresa emitente no topo")
      return
    }
    if (chave.length !== 50) {
      toast.error("A chave de acesso da NFS-e deve ter 50 dígitos")
      return
    }
    setConsultando(true)
    try {
      if (padrao === 3 && !osascoChave.trim()) {
        toast.error("Informe a chave de autenticação de Osasco")
        setConsultando(false)
        return
      }
      if (padrao === 2 && !abrasfUrl.trim()) {
        toast.error("Informe a URL do webservice ABRASF")
        setConsultando(false)
        return
      }
      const r = await postJson<ConsultaNfse>(`empresas/${empresaId}/nfse/consulta/${chave}`, {
        padrao,
        ambiente,
        configuracao: {
          osascoChaveAutenticacao: osascoChave.trim() || null,
          abrasfUrl: abrasfUrl.trim() || null,
          abrasfUsuario: abrasfUsuario.trim() || null,
          abrasfSenha: abrasfSenha || null,
        },
      })
      setResultado(r)
      if (r.encontrada) toast.success(r.cancelada ? "NFS-e encontrada (cancelada)" : "NFS-e encontrada e autorizada")
      else toast.warning("NFS-e não encontrada", { description: r.motivo ?? undefined })
    } catch (e) {
      setResultado(null)
      const msg = e instanceof ApiError ? e.message : "Erro inesperado"
      toast.error("Falha na consulta", { description: msg })
    } finally {
      setConsultando(false)
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <Card>
        <CardHeader>
          <CardTitle className="text-sm font-medium text-muted-foreground">
            Consultar NFS-e pela chave de acesso
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">Empresa emitente</span>
            <span className="text-sm">
              {empresa ? (
                <>
                  <span className="font-medium">{empresa.razaoSocial}</span>
                  <span className="text-muted-foreground"> — {empresa.uf} · usa o certificado dela</span>
                </>
              ) : (
                <span className="text-muted-foreground">Selecione a empresa no seletor do topo.</span>
              )}
            </span>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="consulta-padrao">Padrão da NFS-e</Label>
              <Select value={String(padrao)} onValueChange={(v) => v && setPadrao(Number(v) as PadraoNfse)}>
                <SelectTrigger id="consulta-padrao" className="w-full">
                  <SelectValue>{PADROES_NFSE[padrao]}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(PADROES_NFSE).map(([v, nome]) => (
                    <SelectItem key={v} value={v}>
                      {nome}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="consulta-ambiente">Ambiente</Label>
              <Select value={String(ambiente)} onValueChange={(v) => v && setAmbienteSel(Number(v) as 1 | 2)}>
                <SelectTrigger id="consulta-ambiente" className="w-full">
                  <SelectValue>{ambiente === 1 ? "Produção" : "Homologação"}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="2">Homologação</SelectItem>
                  <SelectItem value="1">Produção</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {padrao === 3 && (
              <div className="flex flex-col gap-1.5 sm:col-span-2">
                <Label htmlFor="consulta-osasco">Chave de autenticação (Osasco)</Label>
                <Input
                  id="consulta-osasco"
                  type="password"
                  autoComplete="off"
                  value={osascoChave}
                  onChange={(ev) => setOsascoChave(ev.target.value)}
                />
              </div>
            )}
            {padrao === 2 && (
              <>
                <div className="flex flex-col gap-1.5 sm:col-span-2">
                  <Label htmlFor="consulta-abrasf-url">URL do webservice ABRASF</Label>
                  <Input
                    id="consulta-abrasf-url"
                    value={abrasfUrl}
                    onChange={(ev) => setAbrasfUrl(ev.target.value)}
                  />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="consulta-abrasf-usuario">Usuário</Label>
                  <Input
                    id="consulta-abrasf-usuario"
                    autoComplete="off"
                    value={abrasfUsuario}
                    onChange={(ev) => setAbrasfUsuario(ev.target.value)}
                  />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="consulta-abrasf-senha">Senha</Label>
                  <Input
                    id="consulta-abrasf-senha"
                    type="password"
                    autoComplete="off"
                    value={abrasfSenha}
                    onChange={(ev) => setAbrasfSenha(ev.target.value)}
                  />
                </div>
              </>
            )}
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="chave-nfse">Chave de acesso (50 dígitos)</Label>
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
              <Input
                id="chave-nfse"
                inputMode="numeric"
                autoComplete="off"
                placeholder="Cole aqui os 50 dígitos da chave"
                value={chave}
                onChange={(ev) => setChave(limparChave(ev.target.value))}
                onKeyDown={(ev) => {
                  if (ev.key === "Enter" && !ev.nativeEvent.isComposing && ev.keyCode !== 229) consultar()
                }}
                className="font-mono tracking-tight sm:w-[30rem]"
              />
              <Button onClick={consultar} disabled={consultando}>
                <Search data-icon="inline-start" className={consultando ? "animate-pulse" : undefined} />
                {consultando ? "Consultando..." : "Consultar"}
              </Button>
            </div>
            <span className="text-xs text-muted-foreground">{chave.length}/50 dígitos</span>
          </div>

          {empresas.length === 0 && (
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
              Informe a chave de acesso de uma NFS-e para ver a situação atual no ambiente nacional.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      )}
    </div>
  )
}

function ResultadoConsulta({ r }: { r: ConsultaNfse }) {
  const { tom, rotulo, Icon } = veredito(r)
  const [copiado, setCopiado] = useState(false)

  async function copiarChave() {
    if (!r.chaveAcesso) return
    await navigator.clipboard.writeText(r.chaveAcesso)
    setCopiado(true)
    setTimeout(() => setCopiado(false), 1500)
  }

  function baixarXml() {
    if (!r.xmlBase64) return
    const bytes = Uint8Array.from(atob(r.xmlBase64), (c) => c.charCodeAt(0))
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/xml" }))
    const a = document.createElement("a")
    a.href = url
    a.download = `NFSe-${r.chaveAcesso ?? "consulta"}.xml`
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between">
        <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
          <Icon className="size-4" aria-hidden />
          Resultado da consulta
        </CardTitle>
        <StatusBadge tom={tom}>{rotulo}</StatusBadge>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Campo rotulo="Chave de acesso">
            <div className="flex items-center gap-2">
              <span className="break-all font-mono text-xs">{formatChaveNfse(r.chaveAcesso)}</span>
              {r.chaveAcesso && (
                <Button variant="ghost" size="icon-xs" onClick={copiarChave} aria-label="Copiar chave">
                  {copiado ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
                </Button>
              )}
            </div>
          </Campo>
          <Campo rotulo="Número da NFS-e">{r.numeroNfse ?? "—"}</Campo>
          <Campo rotulo="Código de retorno">{r.codigo ?? "—"}</Campo>
          <Campo rotulo="Motivo">{r.motivo ?? "—"}</Campo>
        </div>

        {r.local && (
          <>
            <Separator />
            <div className="flex flex-col gap-2">
              <span className="text-xs font-medium text-muted-foreground">Registro local</span>
              <div className="grid gap-4 sm:grid-cols-3">
                <Campo rotulo="DPS">
                  <span className="tabular-nums">
                    {r.local.serie}/{r.local.numeroDps}
                  </span>
                </Campo>
                <Campo rotulo="Tomador">{r.local.tomadorNome ?? "—"}</Campo>
                <Campo rotulo="Emitida em">{formatDateTime(r.local.dataEmissao)}</Campo>
              </div>
            </div>
          </>
        )}

        {r.xmlBase64 && (
          <div>
            <Button variant="outline" size="sm" onClick={baixarXml}>
              <Download data-icon="inline-start" />
              Baixar XML da NFS-e
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function Campo({ rotulo, children }: { rotulo: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs text-muted-foreground">{rotulo}</span>
      <span className="text-sm">{children}</span>
    </div>
  )
}
