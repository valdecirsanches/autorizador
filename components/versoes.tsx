"use client"

import useSWR from "swr"
import { cn } from "@/lib/utils"

type Versoes = {
  painel: { versao: string; commit: string | null; compiladoEm: string | null }
  api:
    | {
        online: true
        url: string
        versao: string | null
        commit: string | null
        compiladaEm: string | null
        iniciadaEm: string | null
        recursos: string[]
      }
    | { online: false; url: string; erro: string }
}

const fmt = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "America/Sao_Paulo",
})

function data(iso: string | null | undefined) {
  if (!iso) return null
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : fmt.format(d)
}

const fetcherVersoes = (url: string): Promise<Versoes> =>
  fetch(url, { cache: "no-store" }).then((r) => {
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    return r.json() as Promise<Versoes>
  })

/**
 * Versao do painel e da API, com data de compilacao, para conferir se o deploy
 * de cada um esta atualizado. Destaca em amarelo a API sem versao (build antigo).
 */
export function Versoes({ className }: { className?: string }) {
  const { data: v } = useSWR<Versoes>("/api/versao", fetcherVersoes, {
    refreshInterval: 60_000,
    revalidateOnFocus: true,
  })

  const painelData = data(v?.painel.compiladoEm)
  const api = v?.api

  return (
    <dl className={cn("grid grid-cols-[auto_1fr] gap-x-2 gap-y-1 text-[11px] leading-tight", className)}>
      <dt className="opacity-60">Painel</dt>
      <dd
        className="min-w-0 truncate font-mono"
        title={v ? `Painel ${v.painel.versao} · commit ${v.painel.commit ?? "-"} · build ${painelData ?? "-"}` : undefined}
      >
        {v ? (
          <>
            v{v.painel.versao}
            {painelData && <span className="opacity-60"> · {painelData}</span>}
          </>
        ) : (
          "…"
        )}
      </dd>

      <dt className="opacity-60">API</dt>
      <dd className="min-w-0 truncate font-mono">
        {!api ? (
          "…"
        ) : !api.online ? (
          <span className="text-destructive" title={`${api.url}: ${api.erro}`}>
            fora do ar
          </span>
        ) : api.versao ? (
          <span
            title={`API ${api.versao} · commit ${api.commit ?? "-"} · compilada ${data(api.compiladaEm) ?? "-"} · no ar desde ${data(api.iniciadaEm) ?? "-"} · ${api.url}`}
          >
            v{api.versao}
            {data(api.compiladaEm) && <span className="opacity-60"> · {data(api.compiladaEm)}</span>}
          </span>
        ) : (
          <span className="text-amber-500" title="A API não informa versão: está rodando um build anterior a 1.1.0.">
            antiga (sem versão)
          </span>
        )}
      </dd>
    </dl>
  )
}
