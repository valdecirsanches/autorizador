import { type NextRequest, NextResponse } from "next/server"
import { COOKIE_SESSAO, verificarSessao } from "@/lib/auth-session"

// Rotas publicas (nao exigem sessao): a propria tela de login e as rotas de
// autenticacao. Todo o resto do painel fica protegido.
const ROTAS_PUBLICAS = ["/login", "/api/auth/login", "/api/auth/logout"]

export async function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl

  if (ROTAS_PUBLICAS.some((r) => pathname === r || pathname.startsWith(r + "/"))) {
    return NextResponse.next()
  }

  const sessao = await verificarSessao(req.cookies.get(COOKIE_SESSAO)?.value)
  if (sessao) return NextResponse.next()

  // Chamadas de API (proxy, lookup): responde 401 JSON, sem redirecionar, para
  // o fetch do cliente tratar (e mandar o usuario ao login).
  // O header x-sessao-expirada distingue este 401 (sessao do PAINEL invalida)
  // de um 401 vindo do backend .NET (ex.: token da API). So o primeiro deve
  // levar o usuario de volta ao login.
  if (pathname.startsWith("/api/")) {
    return NextResponse.json(
      { erro: "Sessao expirada ou ausente." },
      { status: 401, headers: { "x-sessao-expirada": "1" } },
    )
  }

  // Paginas: redireciona ao login preservando o destino.
  const url = req.nextUrl.clone()
  url.pathname = "/login"
  url.search = `?de=${encodeURIComponent(pathname + req.nextUrl.search)}`
  return NextResponse.redirect(url)
}

// Aplica a todas as rotas, exceto assets estaticos do Next e o favicon.
export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico|.*\\.(?:png|jpg|jpeg|svg|ico|webp)$).*)"],
}
