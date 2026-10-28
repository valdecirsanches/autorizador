import type { Ambiente } from "./types"

export function formatCnpj(cnpj: string): string {
  const d = (cnpj ?? "").replace(/\D/g, "").padStart(14, "0")
  if (d.length !== 14) return cnpj
  return `${d.slice(0, 2)}.${d.slice(2, 5)}.${d.slice(5, 8)}/${d.slice(8, 12)}-${d.slice(12)}`
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—"
  const dt = new Date(iso)
  if (Number.isNaN(dt.getTime())) return "—"
  return dt.toLocaleString("pt-BR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  })
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return "—"
  const dt = new Date(iso)
  if (Number.isNaN(dt.getTime())) return "—"
  return dt.toLocaleDateString("pt-BR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
  })
}

export function ambienteLabel(amb: Ambiente): string {
  return amb === 1 ? "Produção" : "Homologação"
}

/** Dias restantes ate a data (negativo = ja vencido). */
export function daysUntil(iso: string | null | undefined): number | null {
  if (!iso) return null
  const dt = new Date(iso)
  if (Number.isNaN(dt.getTime())) return null
  const ms = dt.getTime() - Date.now()
  return Math.floor(ms / (1000 * 60 * 60 * 24))
}
