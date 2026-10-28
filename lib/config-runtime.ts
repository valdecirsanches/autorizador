/**
 * Configuracao de runtime do painel (URL e token da API .NET), editavel pela
 * propria interface — sem depender das variaveis de ambiente do Vercel/servidor.
 *
 * Por que existe: alternar o destino da API pelo painel de "Vars" se mostrou
 * confuso e propenso a erro (valores duplicados _2/_3, precedencia do Next). Aqui
 * o valor definido na tela e gravado num pequeno JSON no servidor e tem
 * PRIORIDADE sobre a env; se nada foi definido na tela, caimos na env
 * (NFE_API_URL / NFE_API_TOKEN) e, por fim, num padrao local.
 *
 * Roda apenas no servidor (Node runtime): proxy, diag e a rota /api/config.
 */

import { createHash } from "node:crypto"
import { promises as fs } from "node:fs"
import path from "node:path"

export interface ConfigRuntime {
  apiUrl?: string
  apiToken?: string
}

/** Origem do valor efetivo, para a UI explicar de onde ele veio. */
export type OrigemValor = "painel" | "ambiente" | "padrao" | "preview"

const PADRAO_API = "http://127.0.0.1:5240"
const PREVIEW_API = "https://nfe-api.x-star.com.br"

export function isPreviewApiFixa(): boolean {
  return process.env.VERCEL_ENV === "preview" || process.env.NODE_ENV === "development"
}

// Diretorio de dados persistente. Em producao (EC2) aponte PAINEL_DATA_DIR para
// uma pasta fora do projeto (ex.: /var/lib/aprovador) para sobreviver a deploys.
const DATA_DIR = process.env.PAINEL_DATA_DIR?.trim() || path.join(process.cwd(), ".painel-data")
const ARQUIVO = path.join(DATA_DIR, "config.json")

/** Remove espacos/quebras nas pontas e a barra final da URL. */
export function limparUrl(u: string): string {
  return u.trim().replace(/\/+$/, "")
}

/** Aceita apenas http/https absolutos. */
export function urlValida(u: string): boolean {
  try {
    const p = new URL(u.trim())
    return p.protocol === "http:" || p.protocol === "https:"
  } catch {
    return false
  }
}

/**
 * Normaliza o token do .env/arquivo: tira espacos/quebras nas pontas e aspas
 * externas. Sem isso, um valor salvo com aspas ou \n final faz o painel enviar
 * bytes diferentes do que a API espera — 401 mesmo "parecendo" igual.
 */
export function normalizarToken(bruto: string | undefined | null): string {
  if (!bruto) return ""
  let t = bruto.trim()
  if (
    t.length >= 2 &&
    ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'")))
  ) {
    t = t.slice(1, -1).trim()
  }
  return t
}

async function lerArquivo(): Promise<ConfigRuntime> {
  try {
    const txt = await fs.readFile(ARQUIVO, "utf8")
    const dado = JSON.parse(txt) as ConfigRuntime
    return dado && typeof dado === "object" ? dado : {}
  } catch {
    // Arquivo ainda nao existe ou invalido: sem override.
    return {}
  }
}

async function gravarArquivo(cfg: ConfigRuntime): Promise<void> {
  await fs.mkdir(DATA_DIR, { recursive: true })
  await fs.writeFile(ARQUIVO, JSON.stringify(cfg, null, 2), "utf8")
}

/** URL efetiva da API: painel (arquivo) > ambiente (env) > padrao local. */
export async function resolverApiBase(): Promise<{ url: string; origem: OrigemValor }> {
  if (isPreviewApiFixa()) return { url: PREVIEW_API, origem: "preview" }

  const cfg = await lerArquivo()
  if (cfg.apiUrl && urlValida(cfg.apiUrl)) return { url: limparUrl(cfg.apiUrl), origem: "painel" }
  const env = process.env.NFE_API_URL
  if (env && urlValida(env)) return { url: limparUrl(env), origem: "ambiente" }
  return { url: PADRAO_API, origem: "padrao" }
}

type ApiAuthFingerprint = {
  tokenLength?: unknown
  tokenFingerprint?: unknown
}

let tokenLookupCache: {
  apiUrl: string
  expiresAt: number
  token: string | null
} | null = null
let tokenLookupInFlight: {
  apiUrl: string
  promise: Promise<string | null>
} | null = null

function fingerprintToken(token: string): string {
  return createHash("sha256").update(token, "utf8").digest("hex").slice(0, 8)
}

async function encontrarTokenDeAmbienteCorrespondente(apiUrl: string): Promise<string | null> {
  const agora = Date.now()
  if (tokenLookupCache?.apiUrl === apiUrl && tokenLookupCache.expiresAt > agora)
    return tokenLookupCache.token

  if (tokenLookupInFlight?.apiUrl === apiUrl)
    return tokenLookupInFlight.promise

  const variaveis = Object.entries(process.env)
    .filter(([nome, valor]) => /^NFE_API_TOKEN(?:_\d+)?$/.test(nome) && Boolean(valor))
    .sort(([a], [b]) => {
      const ordem = (nome: string) => nome === "NFE_API_TOKEN" ? 0 : Number(nome.slice("NFE_API_TOKEN_".length))
      return ordem(a) - ordem(b)
    })

  const candidatos = [...new Set(variaveis.map(([, valor]) => normalizarToken(valor)))]
    .filter(Boolean)
  if (candidatos.length < 2) return null

  const promise = (async () => {
    try {
      const response = await fetch(`${limparUrl(apiUrl)}/diag/auth`, {
        cache: "no-store",
        signal: AbortSignal.timeout(2500),
      })
      if (!response.ok) return null

      const remoto = (await response.json()) as ApiAuthFingerprint
      if (typeof remoto.tokenLength !== "number" || typeof remoto.tokenFingerprint !== "string")
        return null

      return candidatos.find(
        (token) => token.length === remoto.tokenLength && fingerprintToken(token) === remoto.tokenFingerprint,
      ) ?? null
    } catch {
      return null
    }
  })()

  tokenLookupInFlight = { apiUrl, promise }
  const token = await promise
  tokenLookupCache = { apiUrl, expiresAt: agora + 5 * 60_000, token }
  if (tokenLookupInFlight?.promise === promise) tokenLookupInFlight = null
  return token
}

/** Token efetivo: valor definido no painel > variável que confere com a API > NFE_API_TOKEN. */
export async function resolverApiToken(): Promise<{ token: string; origem: OrigemValor }> {
  const cfg = await lerArquivo()
  const doPainel = normalizarToken(cfg.apiToken)
  if (doPainel) return { token: doPainel, origem: "painel" }

  const { url } = await resolverApiBase()
  const tokenCorrespondente = await encontrarTokenDeAmbienteCorrespondente(url)
  if (tokenCorrespondente) return { token: tokenCorrespondente, origem: "ambiente" }

  const doEnv = normalizarToken(process.env.NFE_API_TOKEN)
  if (doEnv) return { token: doEnv, origem: "ambiente" }
  return { token: "", origem: "padrao" }
}

/** Define (ou limpa, com null) a URL sobreposta pelo painel. */
export async function definirApiUrl(url: string | null): Promise<void> {
  const cfg = await lerArquivo()
  if (url === null) delete cfg.apiUrl
  else cfg.apiUrl = limparUrl(url)
  await gravarArquivo(cfg)
}

/** Define (ou limpa, com null) o token sobreposto pelo painel. */
export async function definirApiToken(token: string | null): Promise<void> {
  const cfg = await lerArquivo()
  if (token === null) delete cfg.apiToken
  else cfg.apiToken = token.trim()
  await gravarArquivo(cfg)
}

/** Caminho do arquivo de configuracao (para mensagens de diagnostico). */
export function caminhoConfig(): string {
  return ARQUIVO
}
