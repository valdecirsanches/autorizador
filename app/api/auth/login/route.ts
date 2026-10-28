import { type NextRequest, NextResponse } from "next/server"
import { assinarSessao, COOKIE_SESSAO, DURACAO_SESSAO_MS } from "@/lib/auth-session"

// Compara duas strings em tempo constante (independe de onde diferem).
// Igualamos o tamanho comparando digests HMAC de mesmo comprimento.
async function iguaisTempoConstante(a: string, b: string): Promise<boolean> {
  const enc = new TextEncoder()
  const chave = await crypto.subtle.importKey(
    "raw",
    enc.encode(process.env.PAINEL_SESSION_SECRET ?? "sem-segredo"),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  )
  const [da, db] = await Promise.all([
    crypto.subtle.sign("HMAC", chave, enc.encode(a)),
    crypto.subtle.sign("HMAC", chave, enc.encode(b)),
  ])
  const va = new Uint8Array(da)
  const vb = new Uint8Array(db)
  let diff = 0
  for (let i = 0; i < va.length; i++) diff |= va[i] ^ vb[i]
  return diff === 0
}

export async function POST(req: NextRequest) {
  const usuarioEsperado = process.env.PAINEL_ADMIN_USUARIO
  const senhaEsperada = process.env.PAINEL_ADMIN_SENHA

  if (!usuarioEsperado || !senhaEsperada || !process.env.PAINEL_SESSION_SECRET) {
    return NextResponse.json(
      { erro: "Login nao configurado no servidor (variaveis de ambiente ausentes)." },
      { status: 500 },
    )
  }

  let corpo: { usuario?: string; senha?: string }
  try {
    corpo = await req.json()
  } catch {
    return NextResponse.json({ erro: "Requisicao invalida." }, { status: 400 })
  }

  const usuario = (corpo.usuario ?? "").trim()
  const senha = corpo.senha ?? ""

  const [usuarioOk, senhaOk] = await Promise.all([
    iguaisTempoConstante(usuario, usuarioEsperado),
    iguaisTempoConstante(senha, senhaEsperada),
  ])

  if (!usuarioOk || !senhaOk) {
    // Pequeno atraso para desacelerar tentativas de forca bruta.
    await new Promise((r) => setTimeout(r, 400))
    return NextResponse.json({ erro: "Usuario ou senha invalidos." }, { status: 401 })
  }

  const exp = Date.now() + DURACAO_SESSAO_MS
  const token = await assinarSessao({ sub: usuario, exp })

  // Atributos do cookie decididos pelo protocolo real da requisicao (atras de
  // proxy, confiamos no x-forwarded-proto). Em HTTPS usamos SameSite=None+Secure
  // para que a sessao funcione tambem quando o painel roda embutido num iframe
  // de outra origem (ex.: preview do v0). Em HTTP puro (ex.: EC2 sem TLS)
  // mantemos SameSite=Lax sem Secure, senao o navegador descartaria o cookie.
  const proto = (req.headers.get("x-forwarded-proto") ?? req.nextUrl.protocol.replace(":", "")).split(",")[0].trim()
  const https = proto === "https"

  const res = NextResponse.json({ ok: true })
  res.cookies.set(COOKIE_SESSAO, token, {
    httpOnly: true,
    sameSite: https ? "none" : "lax",
    secure: https,
    path: "/",
    maxAge: Math.floor(DURACAO_SESSAO_MS / 1000),
  })
  return res
}
