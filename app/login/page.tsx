"use client"

import { Suspense, useState } from "react"
import { useRouter, useSearchParams } from "next/navigation"
import { Activity, Loader2, LogIn } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Alert, AlertDescription } from "@/components/ui/alert"

export default function LoginPage() {
  return (
    <Suspense>
      <FormularioLogin />
    </Suspense>
  )
}

function FormularioLogin() {
  const router = useRouter()
  const params = useSearchParams()
  const destino = params.get("de") || "/"

  const [usuario, setUsuario] = useState("")
  const [senha, setSenha] = useState("")
  const [erro, setErro] = useState<string | null>(null)
  const [enviando, setEnviando] = useState(false)

  async function entrar(e: React.FormEvent) {
    e.preventDefault()
    if (enviando) return
    setErro(null)
    setEnviando(true)
    try {
      const res = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ usuario, senha }),
      })
      if (res.ok) {
        // replace() para o login sair do historico do navegador.
        router.replace(destino)
        router.refresh()
        return
      }
      const dados = await res.json().catch(() => ({}))
      setErro(dados.erro ?? "Nao foi possivel entrar. Tente novamente.")
    } catch {
      setErro("Falha de conexao. Verifique sua rede e tente novamente.")
    } finally {
      setEnviando(false)
    }
  }

  return (
    <main className="flex min-h-svh items-center justify-center bg-background px-4 py-10 text-foreground">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <div className="flex size-12 items-center justify-center rounded-xl bg-primary text-primary-foreground">
            <Activity className="size-6" />
          </div>
          <div>
            <h1 className="text-lg font-semibold text-balance">Autorizador NF-e</h1>
            <p className="text-sm text-muted-foreground">Acesso restrito ao painel operacional</p>
          </div>
        </div>

        <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
          <form onSubmit={entrar} className="flex flex-col gap-4" noValidate>
            <div className="flex flex-col gap-2">
              <Label htmlFor="usuario">Usuário</Label>
              <Input
                id="usuario"
                name="usuario"
                autoComplete="username"
                autoFocus
                value={usuario}
                onChange={(e) => setUsuario(e.target.value)}
                disabled={enviando}
                required
              />
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="senha">Senha</Label>
              <Input
                id="senha"
                name="senha"
                type="password"
                autoComplete="current-password"
                value={senha}
                onChange={(e) => setSenha(e.target.value)}
                disabled={enviando}
                required
              />
            </div>

            {erro && (
              <Alert variant="destructive" role="alert">
                <AlertDescription>{erro}</AlertDescription>
              </Alert>
            )}

            <Button type="submit" className="mt-1 w-full" disabled={enviando}>
              {enviando ? (
                <>
                  <Loader2 className="size-4 animate-spin" aria-hidden />
                  Entrando...
                </>
              ) : (
                <>
                  <LogIn className="size-4" aria-hidden />
                  Entrar
                </>
              )}
            </Button>
          </form>
        </div>

        <p className="mt-6 text-center text-xs text-muted-foreground text-pretty">
          Suas credenciais são verificadas no servidor. A sessão expira automaticamente após um
          período de inatividade.
        </p>
      </div>
    </main>
  )
}
