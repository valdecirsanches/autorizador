"use client"

/**
 * Rastreador do envio de logo (depuracao).
 *
 * Cada etapa do fluxo (clique, seletor de arquivo, arrastar, conversao, POST,
 * conferencia, atualizacao da lista, carregamento da imagem) vira um evento:
 *  - exibido ao vivo na tela Empresas (componente RastroLogo);
 *  - guardado no sessionStorage (sobrevive a recarregar a pagina);
 *  - enviado ao servidor do painel (/api/rastro), que escreve no log do
 *    servico: `journalctl -u nfe-app | grep rastro-logo`.
 */

import { useSyncExternalStore } from "react"

export type NivelRastro = "info" | "ok" | "aviso" | "erro"

export type EventoRastro = {
  id: number
  hora: string
  empresa: string
  etapa: string
  detalhe?: string
  nivel: NivelRastro
}

const CHAVE = "nfe:rastro-logo"
const LIMITE = 300

let eventos: EventoRastro[] = carregar()
let proximoId = eventos.reduce((m, e) => Math.max(m, e.id), 0) + 1
const ouvintes = new Set<() => void>()

function carregar(): EventoRastro[] {
  if (typeof window === "undefined") return []
  try {
    const bruto = sessionStorage.getItem(CHAVE)
    return bruto ? (JSON.parse(bruto) as EventoRastro[]) : []
  } catch {
    return []
  }
}

function salvar() {
  try {
    sessionStorage.setItem(CHAVE, JSON.stringify(eventos))
  } catch {
    // sem storage (modo restrito): fica so em memoria
  }
}

function avisar() {
  for (const o of ouvintes) o()
}

function horaAgora() {
  const d = new Date()
  const p = (n: number, t = 2) => String(n).padStart(t, "0")
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}

export function rastrear(empresa: string, etapa: string, detalhe?: string, nivel: NivelRastro = "info") {
  const evento: EventoRastro = { id: proximoId++, hora: horaAgora(), empresa, etapa, detalhe, nivel }
  eventos = [...eventos, evento].slice(-LIMITE)
  salvar()
  avisar()
  console.info(`[rastro-logo] ${evento.hora} ${empresa} | ${etapa}${detalhe ? ` | ${detalhe}` : ""}`)
  try {
    void fetch("/api/rastro", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ...evento, pagina: location.href, navegador: navigator.userAgent }),
      keepalive: true,
    }).catch(() => undefined)
  } catch {
    // o rastro nunca pode atrapalhar o envio do logo
  }
}

export function limparRastro() {
  eventos = []
  salvar()
  avisar()
}

export function relatorioRastro(): string {
  const cabecalho = [
    "RASTRO DO ENVIO DE LOGO",
    `Painel v${process.env.NEXT_PUBLIC_APP_VERSION} (build ${process.env.NEXT_PUBLIC_APP_BUILD_LABEL})`,
    `Pagina: ${typeof location !== "undefined" ? location.href : "-"}`,
    `Navegador: ${typeof navigator !== "undefined" ? navigator.userAgent : "-"}`,
    `Tela: ${typeof window !== "undefined" ? `${window.innerWidth}x${window.innerHeight}` : "-"}`,
    "",
  ]
  const linhas = eventos.map(
    (e) => `${e.hora} [${e.nivel.toUpperCase()}] ${e.empresa} | ${e.etapa}${e.detalhe ? ` | ${e.detalhe}` : ""}`,
  )
  return [...cabecalho, ...linhas].join("\n")
}

const vazio: EventoRastro[] = []

export function useRastroLogo(): EventoRastro[] {
  return useSyncExternalStore(
    (cb) => {
      ouvintes.add(cb)
      return () => ouvintes.delete(cb)
    },
    () => eventos,
    () => vazio,
  )
}
