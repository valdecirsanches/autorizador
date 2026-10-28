"use client"

import { useState } from "react"
import {
  Activity,
  Building2,
  ShieldCheck,
  FileSearch,
  FileText,
  KeyRound,
  ListChecks,
  ListOrdered,
  LogOut,
  Receipt,
  Settings2,
} from "lucide-react"
import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import { useEmpresa } from "./empresa-provider"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { SecaoStatus } from "./secao-status"
import { SecaoEmpresas } from "./secao-empresas"
import { SecaoCertificados } from "./secao-certificados"
import { SecaoConsulta } from "./secao-consulta"
import { SecaoNotas } from "./secao-notas"
import { SecaoTentativas } from "./secao-tentativas"
import { SecaoNumeracao } from "./secao-numeracao"
import { SecaoNfseNotas } from "./secao-nfse-notas"
import { SecaoNfseTentativas } from "./secao-nfse-tentativas"
import { SecaoNfseConsulta } from "./secao-nfse-consulta"
import { SecaoConfigApi } from "./secao-config-api"
import { SecaoApps } from "./secao-apps"
import { ApiMonitorDialog, ApiMonitorTrigger } from "./api-monitor-dialog"
import { Versoes } from "./versoes"

type Secao =
  | "status"
  | "empresas"
  | "certificados"
  | "consulta"
  | "notas"
  | "tentativas"
  | "numeracao"
  | "nfse-notas"
  | "nfse-tentativas"
  | "nfse-consulta"
  | "config-api"
  | "apps"

type NavItem = { id: Secao; label: string; icon: typeof Activity; hint: string }

/** Secoes da NF-e (inalteradas) e, em grupo separado, as da NFS-e. */
const navNfe: NavItem[] = [
  { id: "status", label: "Status SEFAZ", icon: Activity, hint: "Disponibilidade por UF" },
  { id: "notas", label: "Notas emitidas", icon: FileText, hint: "NF-e enviadas e seus resultados" },
  { id: "tentativas", label: "Tentativas", icon: ListChecks, hint: "Toda emissão, inclusive as que falharam" },
  { id: "consulta", label: "Consultar NF-e", icon: FileSearch, hint: "Situação por chave de acesso" },
  { id: "empresas", label: "Empresas", icon: Building2, hint: "Emitentes cadastrados" },
  { id: "certificados", label: "Certificados", icon: ShieldCheck, hint: "Certificados A1" },
  { id: "numeracao", label: "Numeração", icon: ListOrdered, hint: "Séries e próximo número" },
]

const navNfse: NavItem[] = [
  { id: "nfse-notas", label: "NFS-e emitidas", icon: Receipt, hint: "Notas de serviço e seus resultados" },
  { id: "nfse-tentativas", label: "Tentativas NFS-e", icon: ListChecks, hint: "Toda emissão de DPS, inclusive falhas" },
  { id: "nfse-consulta", label: "Consultar NFS-e", icon: FileSearch, hint: "Situação por chave de 50 dígitos" },
]

const navSistema: NavItem[] = [
  { id: "apps", label: "Apps integrados", icon: KeyRound, hint: "Chave de acesso de cada sistema" },
  { id: "config-api", label: "Configuração da API", icon: Settings2, hint: "Endereço e token da API .NET" },
]

const nav: NavItem[] = [...navNfe, ...navNfse, ...navSistema]

export function PainelShell() {
  const [secao, setSecao] = useState<Secao>("status")
  const [saindo, setSaindo] = useState(false)
  const [monitorOpen, setMonitorOpen] = useState(false)
  const { empresas, empresaId, empresa, selecionar, ambiente, ambienteSalvando, definirAmbiente } = useEmpresa()

  async function sair() {
    if (saindo) return
    setSaindo(true)
    try {
      await fetch("/api/auth/logout", { method: "POST" })
    } catch {
      // Mesmo em falha de rede, mandamos ao login: sem sessao valida, o
      // middleware bloqueia o painel de qualquer forma.
    } finally {
      window.location.href = "/login"
    }
  }

  return (
    <div className="flex min-h-svh bg-background text-foreground">
      <aside className="hidden w-64 shrink-0 flex-col border-r border-sidebar-border bg-sidebar px-4 py-6 text-sidebar-foreground md:flex">
        <div className="mb-8 flex items-center gap-2.5 px-2">
          <div className="flex size-9 items-center justify-center rounded-md bg-sidebar-primary text-sidebar-primary-foreground">
            <Activity className="size-5" />
          </div>
          <div className="flex flex-col leading-tight">
            <span className="text-sm font-semibold">Autorizador NF-e</span>
            <span className="text-xs text-sidebar-foreground/60">Painel operacional</span>
          </div>
        </div>

        <nav className="mb-6 flex flex-col gap-5">
          {[
            { titulo: "NF-e", itens: navNfe },
            { titulo: "NFS-e", itens: navNfse },
            { titulo: "Sistema", itens: navSistema },
          ].map((grupo) => (
            <div key={grupo.titulo} className="flex flex-col gap-1">
              <span className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wider text-sidebar-foreground/40">
                {grupo.titulo}
              </span>
              {grupo.itens.map((item) => {
                const Icon = item.icon
                const active = secao === item.id
                return (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => setSecao(item.id)}
                    aria-current={active ? "page" : undefined}
                    className={cn(
                      "flex items-center gap-3 rounded-md px-3 py-2.5 text-left text-sm transition-colors",
                      active
                        ? "bg-sidebar-accent text-sidebar-accent-foreground"
                        : "text-sidebar-foreground/70 hover:bg-sidebar-accent/50 hover:text-sidebar-foreground",
                    )}
                  >
                    <Icon className="size-4 shrink-0" />
                    <span className="flex flex-col leading-tight">
                      <span className="font-medium">{item.label}</span>
                      <span className="text-xs text-sidebar-foreground/50">{item.hint}</span>
                    </span>
                  </button>
                )
              })}
            </div>
          ))}
        </nav>

        {/* Versao do painel e da API: confirma na tela que o deploy pegou. */}
        <Versoes className="mt-auto border-t border-sidebar-border px-2 pt-4 text-sidebar-foreground/80" />
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex flex-col gap-3 border-b border-border bg-card px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-col gap-0.5">
            <h1 className="text-lg font-semibold tracking-tight">
              {nav.find((n) => n.id === secao)?.label}
            </h1>
            <p className="text-sm text-muted-foreground">
              {nav.find((n) => n.id === secao)?.hint}
            </p>
            <Versoes className="mt-2 text-muted-foreground md:hidden" />
          </div>

          <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
            {/* Selecao unica de empresa: vale para todas as secoes ate que
                outra seja escolhida. */}
            <div className="flex items-center gap-2">
              <Building2 className="size-4 shrink-0 text-muted-foreground" />
              <Select
                value={empresaId}
                onValueChange={(v) => v && selecionar(v)}
                disabled={empresas.length === 0}
              >
                <SelectTrigger className="w-64" aria-label="Empresa em operação">
                  <SelectValue placeholder="Selecione a empresa">
                    {empresa ? `${empresa.razaoSocial} — ${empresa.uf}` : undefined}
                  </SelectValue>
                </SelectTrigger>
                <SelectContent>
                  {empresas.map((e) => (
                    <SelectItem key={e.id} value={e.id}>
                      {e.razaoSocial} — {e.uf}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {/* Ambiente e por empresa: alterar aqui grava o AmbientePadrao da
                empresa selecionada. */}
            <div className="flex items-center gap-3">
              <span className="text-xs font-medium text-muted-foreground" aria-live="polite">
                {ambienteSalvando ? "Salvando ambiente…" : `Ambiente confirmado: ${ambiente === 1 ? "Produção" : "Homologação"}`}
              </span>
              <ToggleGroup
                value={[String(ambiente)]}
                onValueChange={(v) => {
                  const next = v[0]
                  if (next && empresaId) void definirAmbiente(Number(next) as 1 | 2)
                }}
                className="rounded-md"
                disabled={!empresaId || ambienteSalvando}
              >
                <ToggleGroupItem value="2" className="text-xs">
                  Homologação
                </ToggleGroupItem>
                <ToggleGroupItem value="1" className="text-xs">
                  Produção
                </ToggleGroupItem>
              </ToggleGroup>
            </div>

            <ApiMonitorTrigger onClick={() => setMonitorOpen(true)} />

            <Button
              variant="ghost"
              size="sm"
              onClick={sair}
              disabled={saindo}
              className="text-muted-foreground hover:text-foreground"
            >
              <LogOut className="size-4" aria-hidden />
              {saindo ? "Saindo..." : "Sair"}
            </Button>
          </div>
        </header>

        {/* Nav mobile */}
        <div className="flex gap-1 overflow-x-auto border-b border-border bg-card px-4 py-2 md:hidden">
          {nav.map((item) => {
            const active = secao === item.id
            return (
              <button
                key={item.id}
                type="button"
                onClick={() => setSecao(item.id)}
                className={cn(
                  "shrink-0 rounded-md px-3 py-2 text-xs font-medium",
                  active ? "bg-accent text-accent-foreground" : "text-muted-foreground",
                )}
              >
                {item.label}
              </button>
            )
          })}
        </div>

        <main className="flex-1 overflow-auto px-6 py-6">
          {secao === "config-api" ? (
            <SecaoConfigApi />
          ) : secao === "apps" ? (
            <SecaoApps />
          ) : secao === "nfse-notas" ? (
            <SecaoNfseNotas />
          ) : secao === "nfse-tentativas" ? (
            <SecaoNfseTentativas />
          ) : secao === "nfse-consulta" ? (
            <SecaoNfseConsulta />

          ) : secao === "empresas" ? (
            <SecaoEmpresas />
          ) : secao === "certificados" ? (
            <SecaoCertificados />
          ) : secao === "consulta" ? (
            <SecaoConsulta />
          ) : secao === "notas" ? (
            <SecaoNotas />
          ) : secao === "tentativas" ? (
            <SecaoTentativas />
          ) : secao === "numeracao" ? (
            <SecaoNumeracao />
          ) : (
            <SecaoStatus />
          )}
        </main>
      </div>
      <ApiMonitorDialog open={monitorOpen} onOpenChange={setMonitorOpen} />
    </div>
  )
}
