"use client"

import { useState } from "react"
import { Stethoscope, CheckCircle2, XCircle, Database } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { useEmpresa } from "./empresa-provider"

type Diag = {
  painel: {
    apiUrl: string
    apiUrlOrigem?: "painel" | "ambiente" | "padrao" | "preview"
    tokenPresente: boolean
    tokenLength: number
    tokenFingerprint: string | null
  }
  apiNoAr: boolean
  diagDisponivel: boolean
  apiToken: { tokenLength: number; tokenFingerprint: string } | null
  teste: { status: number; ok: boolean } | null
  banco: {
    host: string
    port: number
    database: string
    totalDocumentos: number
    documentosEmpresa: number | null
  } | null
  veredito: string
}

function Linha({ ok, children }: { ok: boolean | null; children: React.ReactNode }) {
  return (
    <div className="flex items-start gap-2 text-sm">
      {ok === null ? (
        <span className="mt-0.5 size-4 shrink-0 rounded-full border border-muted-foreground/40" />
      ) : ok ? (
        <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-500" />
      ) : (
        <XCircle className="mt-0.5 size-4 shrink-0 text-destructive" />
      )}
      <span className="min-w-0">{children}</span>
    </div>
  )
}

export function DiagnosticoConexao() {
  const { empresaId } = useEmpresa()
  const [carregando, setCarregando] = useState(false)
  const [diag, setDiag] = useState<Diag | null>(null)
  const [erro, setErro] = useState<string | null>(null)

  async function executar() {
    setCarregando(true)
    setErro(null)
    try {
      const url = empresaId ? `/api/diag?empresaId=${encodeURIComponent(empresaId)}` : "/api/diag"
      const r = await fetch(url, { cache: "no-store" })
      if (!r.ok) {
        setErro(`Falha ao executar diagnóstico (HTTP ${r.status}).`)
        setDiag(null)
        return
      }
      setDiag((await r.json()) as Diag)
    } catch (e) {
      setErro(e instanceof Error ? e.message : "Falha de rede no diagnóstico.")
    } finally {
      setCarregando(false)
    }
  }

  const conexaoOk = diag?.teste?.ok === true

  return (
    <Card className="mx-auto w-full max-w-xl text-left">
      <CardHeader className="flex-row items-center justify-between gap-3">
        <CardTitle className="flex items-center gap-2 text-sm font-medium">
          <Stethoscope className="size-4" />
          Diagnóstico da conexão com a API
        </CardTitle>
        <Button size="sm" variant="outline" onClick={executar} disabled={carregando}>
          {carregando ? "Verificando..." : "Executar diagnóstico"}
        </Button>
      </CardHeader>

      {(diag || erro) && (
        <CardContent className="flex flex-col gap-3">
          {erro && <p className="text-sm text-destructive">{erro}</p>}

          {diag && (
            <>
              <div
                className={
                  conexaoOk
                    ? "rounded-md border border-emerald-500/30 bg-emerald-500/10 p-3 text-sm"
                    : "rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
                }
              >
                {diag.veredito}
              </div>

              <div className="flex flex-col gap-1 rounded-md border border-border bg-muted/40 p-3">
                <span className="text-xs uppercase tracking-wide text-muted-foreground">
                  URL da API que o painel está usando AGORA
                </span>
                <span className="break-all font-mono text-sm text-foreground">
                  {diag.painel.apiUrl}
                </span>
                <span className="text-xs text-muted-foreground">
                  {diag.painel.apiUrlOrigem === "preview"
                    ? "URL fixa da API de teste para preview/desenvolvimento; produção não é alterada."
                    : diag.painel.apiUrlOrigem === "painel"
                      ? "Definido na tela Configuração da API (tem prioridade sobre a variável de ambiente)."
                      : diag.painel.apiUrlOrigem === "ambiente"
                        ? "Veio da variável de ambiente NFE_API_URL. Para trocar sem mexer em Vars, use a tela Configuração da API."
                        : "Valor padrão (nenhuma URL definida na tela nem em NFE_API_URL)."}
                </span>
              </div>

              <div className="flex flex-col gap-1.5">
                <Linha ok={diag.painel.tokenPresente}>
                  Token no painel:{" "}
                  {diag.painel.tokenPresente ? (
                    <span className="font-mono">
                      len {diag.painel.tokenLength} · fp {diag.painel.tokenFingerprint}
                    </span>
                  ) : (
                    "ausente"
                  )}
                </Linha>

                <Linha ok={diag.apiNoAr}>
                  API no ar em <span className="font-mono">{diag.painel.apiUrl}</span>
                </Linha>

                {diag.diagDisponivel && diag.apiToken ? (
                  <Linha
                    ok={diag.apiToken.tokenFingerprint === diag.painel.tokenFingerprint}
                  >
                    Token na API:{" "}
                    <span className="font-mono">
                      len {diag.apiToken.tokenLength} · fp {diag.apiToken.tokenFingerprint}
                    </span>
                  </Linha>
                ) : (
                  <Linha ok={null}>
                    Token na API: indisponível (atualize a API para expor /diag/auth)
                  </Linha>
                )}

                {diag.teste && (
                  <Linha ok={diag.teste.ok}>
                    Teste em <span className="font-mono">/api/empresas</span> → HTTP{" "}
                    {diag.teste.status}
                  </Linha>
                )}
              </div>

              {diag.banco && (
                <div className="flex flex-col gap-1.5 rounded-md border border-border bg-muted/40 p-3 text-sm">
                  <div className="flex items-center gap-2 font-medium">
                    <Database className="size-4 shrink-0 text-muted-foreground" />
                    Banco a que a API está conectada
                  </div>
                  <p className="text-muted-foreground">
                    Host <span className="font-mono text-foreground">{diag.banco.host}:{diag.banco.port}</span>
                    {" · "}base <span className="font-mono text-foreground">{diag.banco.database}</span>
                  </p>
                  <p className="text-muted-foreground">
                    Notas neste banco:{" "}
                    <span className="font-mono text-foreground">{diag.banco.totalDocumentos}</span> no total
                    {diag.banco.documentosEmpresa !== null && (
                      <>
                        {" · "}
                        <span className="font-mono text-foreground">{diag.banco.documentosEmpresa}</span> da empresa
                        selecionada
                      </>
                    )}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    Se estes números não batem com o MySQL que você abriu, a API está conectada a
                    outro servidor/base — ajuste a <span className="font-mono">MYSQL_CONNECTION_STRING</span> da
                    API e reinicie-a.
                  </p>
                </div>
              )}
            </>
          )}
        </CardContent>
      )}
    </Card>
  )
}
