"use client"

import { useEffect, useState } from "react"
import useSWR from "swr"
import { Settings2, Save, RotateCcw } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Badge } from "@/components/ui/badge"
import { DiagnosticoConexao } from "./diagnostico-conexao"

type ConfigEstado = {
  apiUrl: string
  apiUrlOrigem: "painel" | "ambiente" | "padrao" | "preview"
  apiUrlFixaPreview: boolean
  tokenPresente: boolean
  tokenLength: number
  tokenFingerprint: string | null
  tokenOrigem: "painel" | "ambiente" | "padrao"
  arquivo: string
}

const fetcherConfig = (url: string): Promise<ConfigEstado> =>
  fetch(url, { cache: "no-store" }).then((r) => {
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    return r.json() as Promise<ConfigEstado>
  })

function BadgeOrigem({ origem }: { origem: ConfigEstado["apiUrlOrigem"] }) {
  const texto =
    origem === "preview"
      ? "API de teste (preview)"
      : origem === "painel"
        ? "Definido no painel"
        : origem === "ambiente"
          ? "Variável de ambiente"
          : "Padrão"
  return (
    <Badge variant={origem === "painel" ? "default" : "secondary"} className="text-[11px]">
      {texto}
    </Badge>
  )
}

export function SecaoConfigApi() {
  const { data, mutate, isLoading } = useSWR<ConfigEstado>("/api/config", fetcherConfig)
  const [url, setUrl] = useState("")
  const [token, setToken] = useState("")
  const [salvando, setSalvando] = useState(false)
  const [restaurando, setRestaurando] = useState(false)

  // Pre-preenche o campo de URL com o valor efetivo atual, uma vez carregado.
  useEffect(() => {
    if (data) setUrl(data.apiUrl)
  }, [data])

  async function salvar() {
    if (salvando) return
    setSalvando(true)
    try {
      // Token so e enviado quando digitado: campo vazio mantem o token atual.
      const corpo: { apiUrl?: string; apiToken?: string } = {}
      if (!data?.apiUrlFixaPreview) corpo.apiUrl = url.trim()
      if (token.trim()) corpo.apiToken = token.trim()

      const r = await fetch("/api/config", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(corpo),
      })
      const json = await r.json()
      if (!r.ok) throw new Error(json?.erro ?? `HTTP ${r.status}`)

      setToken("")
      await mutate(json as ConfigEstado, { revalidate: false })
      toast.success("Configuração salva", {
        description: data?.apiUrlFixaPreview
          ? "Este preview continuará usando a API de teste; apenas o token informado foi atualizado."
          : "Esta tela salvou a conexão no painel; as próximas chamadas encaminhadas ao backend aparecerão no monitor.",
      })
    } catch (e) {
      toast.error("Não foi possível salvar", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setSalvando(false)
    }
  }

  async function restaurar() {
    if (restaurando) return
    setRestaurando(true)
    try {
      const r = await fetch("/api/config", { method: "DELETE" })
      const json = await r.json()
      if (!r.ok) throw new Error(json?.erro ?? `HTTP ${r.status}`)
      setToken("")
      await mutate(json as ConfigEstado, { revalidate: false })
      toast.success("Voltou a usar as variáveis de ambiente")
    } catch (e) {
      toast.error("Não foi possível restaurar", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setRestaurando(false)
    }
  }

  const algumOverride = data?.apiUrlOrigem === "painel" || data?.tokenOrigem === "painel"

  return (
    <div className="mx-auto flex w-full max-w-xl flex-col gap-6">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-sm font-medium">
            <Settings2 className="size-4" />
            Configuração da API
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <p className="text-sm text-muted-foreground">
            {data?.apiUrlFixaPreview
              ? "Este preview sempre chama a API de teste, sem alterar o destino da versão de produção."
              : <>Defina aqui para qual endereço o painel envia as chamadas da API .NET. Salvar esta configuração é uma operação do painel, não uma chamada ao backend; por isso ela não aparece no monitor. O valor salvo tem prioridade sobre <span className="font-mono">NFE_API_URL</span>.</>}
          </p>

          <div className="flex flex-col gap-2">
            <Label htmlFor="apiUrl">URL da API</Label>
            <Input
              id="apiUrl"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              readOnly={data?.apiUrlFixaPreview}
              placeholder="https://facil-api.x-star.com.br"
              autoComplete="off"
              spellCheck={false}
              className="font-mono text-sm"
            />
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <span>Origem atual:</span>
              {data ? <BadgeOrigem origem={data.apiUrlOrigem} /> : <span>...</span>}
            </div>
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="apiToken">Token da API (opcional)</Label>
            <Input
              id="apiToken"
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder={
                data?.tokenPresente ? "•••••• (deixe vazio para manter o atual)" : "Defina o token Bearer"
              }
              autoComplete="off"
              spellCheck={false}
              className="font-mono text-sm"
            />
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              {data?.tokenPresente ? (
                <>
                  <span>
                    Atual: <span className="font-mono">len {data.tokenLength}</span>
                    {data.tokenFingerprint && (
                      <>
                        {" · "}fp <span className="font-mono">{data.tokenFingerprint}</span>
                      </>
                    )}
                  </span>
                  {data && <BadgeOrigem origem={data.tokenOrigem} />}
                </>
              ) : (
                <span className="text-destructive">Nenhum token definido — a API responderá 401.</span>
              )}
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button
              onClick={salvar}
              disabled={salvando || isLoading || (data?.apiUrlFixaPreview ? !token.trim() : !url.trim())}
            >
              <Save className="size-4" aria-hidden />
              {salvando ? "Salvando..." : "Salvar"}
            </Button>
            <Button
              variant="outline"
              onClick={restaurar}
              disabled={restaurando || !algumOverride}
              title={algumOverride ? undefined : "Nenhum valor definido no painel para restaurar"}
            >
              <RotateCcw className="size-4" aria-hidden />
              {restaurando ? "Restaurando..." : "Usar variáveis de ambiente"}
            </Button>
          </div>

          <p className="text-xs text-muted-foreground">
            Salvo no servidor em <span className="font-mono break-all">{data?.arquivo ?? "…"}</span>. Em
            produção, defina <span className="font-mono">PAINEL_DATA_DIR</span> para uma pasta fora do
            projeto se quiser que sobreviva a novos deploys.
          </p>
        </CardContent>
      </Card>

      <DiagnosticoConexao />
    </div>
  )
}
