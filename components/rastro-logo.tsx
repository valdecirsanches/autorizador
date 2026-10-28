"use client"

import { useState } from "react"
import { Activity, Check, ChevronDown, ChevronUp, Copy, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Card } from "@/components/ui/card"
import { limparRastro, relatorioRastro, useRastroLogo, type NivelRastro } from "@/lib/rastro-logo"

const corNivel: Record<NivelRastro, string> = {
  info: "text-muted-foreground",
  ok: "text-emerald-600 dark:text-emerald-400",
  aviso: "text-amber-600 dark:text-amber-400",
  erro: "text-destructive",
}

/**
 * Painel ao vivo do rastreador do logo, exibido na tela Empresas. Mostra cada
 * etapa do envio e permite copiar o relatorio para enviar ao suporte.
 */
export function RastroLogo() {
  const eventos = useRastroLogo()
  const [aberto, setAberto] = useState(true)
  const [copiado, setCopiado] = useState(false)

  async function copiar() {
    const texto = relatorioRastro()
    try {
      await navigator.clipboard.writeText(texto)
      setCopiado(true)
      setTimeout(() => setCopiado(false), 2000)
    } catch {
      // http sem HTTPS bloqueia a area de transferencia: baixa como arquivo.
      const url = URL.createObjectURL(new Blob([texto], { type: "text/plain" }))
      const a = document.createElement("a")
      a.href = url
      a.download = `rastro-logo-${Date.now()}.txt`
      document.body.appendChild(a)
      a.click()
      a.remove()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
      toast.info("A cópia foi bloqueada pelo navegador; o relatório foi baixado como arquivo.")
    }
  }

  return (
    <Card className="gap-0 overflow-hidden py-0">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border px-4 py-2.5">
        <button
          type="button"
          className="flex items-center gap-2 text-sm font-medium"
          onClick={() => setAberto((v) => !v)}
        >
          <Activity className="size-4 text-muted-foreground" aria-hidden />
          Rastreador do envio de logo
          <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
            {eventos.length} evento(s)
          </span>
          {aberto ? <ChevronUp className="size-4" /> : <ChevronDown className="size-4" />}
        </button>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={() => void copiar()} disabled={eventos.length === 0}>
            {copiado ? <Check data-icon="inline-start" /> : <Copy data-icon="inline-start" />}
            {copiado ? "Copiado" : "Copiar relatório"}
          </Button>
          <Button variant="ghost" size="sm" onClick={limparRastro} disabled={eventos.length === 0}>
            <Trash2 data-icon="inline-start" />
            Limpar
          </Button>
        </div>
      </div>
      {aberto ? (
        eventos.length === 0 ? (
          <p className="px-4 py-4 text-sm text-muted-foreground">
            Clique em <strong>Enviar logo</strong> (ou arraste a imagem para a coluna Logo). Cada passo aparece aqui
            em tempo real e também no log do servidor do painel.
          </p>
        ) : (
          <ol className="max-h-72 overflow-auto px-4 py-2 font-mono text-xs leading-relaxed">
            {[...eventos].reverse().map((e) => (
              <li key={e.id} className="flex gap-2 border-b border-border/50 py-1 last:border-0">
                <span className="shrink-0 text-muted-foreground">{e.hora}</span>
                <span className={`shrink-0 font-semibold ${corNivel[e.nivel]}`}>{e.etapa}</span>
                <span className="shrink-0 text-muted-foreground">· {e.empresa}</span>
                {e.detalhe ? <span className="min-w-0 break-all">{e.detalhe}</span> : null}
              </li>
            ))}
          </ol>
        )
      ) : null}
    </Card>
  )
}
