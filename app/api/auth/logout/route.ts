import { type NextRequest, NextResponse } from "next/server"
import { COOKIE_SESSAO } from "@/lib/auth-session"

export async function POST(req: NextRequest) {
  // Espelha os atributos usados no login (SameSite/Secure dependem do protocolo)
  // para o navegador de fato remover o cookie, inclusive dentro de um iframe.
  const proto = (req.headers.get("x-forwarded-proto") ?? req.nextUrl.protocol.replace(":", "")).split(",")[0].trim()
  const https = proto === "https"

  const res = NextResponse.json({ ok: true })
  // Expira o cookie imediatamente.
  res.cookies.set(COOKIE_SESSAO, "", {
    httpOnly: true,
    sameSite: https ? "none" : "lax",
    secure: https,
    path: "/",
    maxAge: 0,
  })
  return res
}
