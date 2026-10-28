// Sessao do painel: um cookie assinado com HMAC-SHA256 (Web Crypto), sem
// dependencias externas. Funciona tanto no Edge runtime (middleware) quanto no
// Node runtime (route handlers), pois usa apenas globalThis.crypto.subtle.
//
// Formato do token: base64url(payloadJson) + "." + base64url(hmac)
// O payload guarda o "assunto" (sub = usuario) e a expiracao (exp, epoch ms).

export interface SessaoPainel {
  sub: string
  exp: number
}

const NOME_COOKIE = "painel_sessao"
export const COOKIE_SESSAO = NOME_COOKIE

// Duracao padrao da sessao: 12 horas.
export const DURACAO_SESSAO_MS = 12 * 60 * 60 * 1000

function base64urlFromBytes(bytes: Uint8Array): string {
  let bin = ""
  for (const b of bytes) bin += String.fromCharCode(b)
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "")
}

function bytesFromBase64url(s: string): Uint8Array {
  const b64 = s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4)
  const bin = atob(b64)
  const out = new Uint8Array(bin.length)
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i)
  return out
}

function textoParaBytes(s: string): Uint8Array {
  return new TextEncoder().encode(s)
}

async function importarChave(segredo: string): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    "raw",
    textoParaBytes(segredo),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"],
  )
}

function segredo(): string {
  const s = process.env.PAINEL_SESSION_SECRET
  if (!s || s.length < 16) {
    throw new Error(
      "PAINEL_SESSION_SECRET ausente ou muito curto (>=16 chars). " +
        "Defina um segredo forte para assinar a sessao do painel.",
    )
  }
  return s
}

/** Assina uma sessao e devolve o valor do cookie. */
export async function assinarSessao(sessao: SessaoPainel): Promise<string> {
  const chave = await importarChave(segredo())
  const payload = base64urlFromBytes(textoParaBytes(JSON.stringify(sessao)))
  const assinatura = await crypto.subtle.sign("HMAC", chave, textoParaBytes(payload))
  return `${payload}.${base64urlFromBytes(new Uint8Array(assinatura))}`
}

/**
 * Verifica o valor do cookie. Retorna a sessao quando a assinatura confere e a
 * sessao ainda nao expirou; caso contrario, null. Nunca lanca.
 */
export async function verificarSessao(valor: string | undefined | null): Promise<SessaoPainel | null> {
  if (!valor) return null
  const ponto = valor.indexOf(".")
  if (ponto <= 0) return null

  const payload = valor.slice(0, ponto)
  const assinaturaRecebida = valor.slice(ponto + 1)

  try {
    const chave = await importarChave(segredo())
    const ok = await crypto.subtle.verify(
      "HMAC",
      chave,
      bytesFromBase64url(assinaturaRecebida),
      textoParaBytes(payload),
    )
    if (!ok) return null

    const sessao = JSON.parse(new TextDecoder().decode(bytesFromBase64url(payload))) as SessaoPainel
    if (typeof sessao.exp !== "number" || Date.now() > sessao.exp) return null
    return sessao
  } catch {
    return null
  }
}
