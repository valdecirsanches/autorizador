"use client"

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react"
import useSWR from "swr"
import { toast } from "sonner"
import { fetcher, putJson, ApiError } from "@/lib/api"
import type { Empresa, Ambiente } from "@/lib/types"

type EmpresaCtx = {
  /** Lista de empresas cadastradas. */
  empresas: Empresa[]
  carregando: boolean
  /** Id da empresa em operacao (selecionada globalmente). "" se nenhuma. */
  empresaId: string
  /** A empresa selecionada, ou null. */
  empresa: Empresa | null
  /** Seleciona a empresa que sera usada em todo o painel. Persiste a escolha. */
  selecionar: (id: string) => void
  /** Ambiente da empresa selecionada (1 producao / 2 homologacao). Default 2. */
  ambiente: Ambiente
  /** Indica que a API ainda está confirmando a troca de ambiente. */
  ambienteSalvando: boolean
  /** Altera o ambiente padrao da empresa selecionada no servidor. */
  definirAmbiente: (a: Ambiente) => Promise<void>
  /** Reobtem a lista de empresas do servidor. */
  recarregar: () => void
}

const Ctx = createContext<EmpresaCtx | null>(null)

// A escolha de empresa vale "ate que outra seja selecionada": guardamos no
// localStorage para sobreviver a recargas e a navegacao entre secoes.
const CHAVE = "nfe:empresaSelecionada"

export function EmpresaProvider({ children }: { children: ReactNode }) {
  const { data, isLoading, mutate } = useSWR<Empresa[]>("empresas", fetcher, {
    // Acompanha mudanças feitas fora do painel (ex.: PUT {"ambiente": 2} via API).
    // 15 s basta para isso; 3 s gerava ~1.200 chamadas/hora por aba aberta.
    refreshInterval: 15_000,
    revalidateOnFocus: true,
  })
  const empresas = useMemo(() => data ?? [], [data])
  const [empresaId, setEmpresaId] = useState("")
  const [ambienteSalvando, setAmbienteSalvando] = useState(false)

  // Quando a lista chega (ou muda), garante que ha uma empresa selecionada e
  // que ela ainda existe: restaura a escolha salva ou cai na primeira.
  useEffect(() => {
    if (empresas.length === 0) {
      if (empresaId) setEmpresaId("")
      return
    }
    setEmpresaId((atual) => {
      if (atual && empresas.some((e) => e.id === atual)) return atual
      const salvo = typeof window !== "undefined" ? localStorage.getItem(CHAVE) : null
      if (salvo && empresas.some((e) => e.id === salvo)) return salvo
      return empresas[0].id
    })
  }, [empresas, empresaId])

  const selecionar = useCallback((id: string) => {
    setEmpresaId(id)
    if (typeof window !== "undefined") localStorage.setItem(CHAVE, id)
  }, [])

  const empresa = useMemo(
    () => empresas.find((e) => e.id === empresaId) ?? null,
    [empresas, empresaId],
  )

  const ambiente: Ambiente = empresa?.ambientePadrao === 1 ? 1 : 2

  const definirAmbiente = useCallback(
    async (a: Ambiente) => {
      if (!empresaId || ambienteSalvando) return
      setAmbienteSalvando(true)
      try {
        const confirmado = await putJson<Empresa>(`empresas/${empresaId}/ambiente`, { ambiente: a })
        if (confirmado.id !== empresaId || confirmado.ambientePadrao !== a) {
          throw new Error("A API não confirmou o ambiente solicitado. A tela manteve o valor anterior.")
        }

        await mutate(
          (listaAtual) =>
            (listaAtual ?? []).map((item) => item.id === empresaId ? confirmado : item),
          { revalidate: false },
        )
        toast.success(`Ambiente confirmado pela API: ${confirmado.ambientePadraoDescricao}`, {
          description: "O painel já está atualizado. As próximas emissões usarão este ambiente, salvo se a requisição informar outro.",
        })
      } catch (e) {
        try {
          await mutate()
        } catch {
          // Mantém o estado em tela e mostra o erro original da operação.
        }
        // Nao relanca: o clique no toggle usa `void`, entao um throw viraria
        // erro nao tratado. Mostramos uma mensagem clara e mantemos o estado.
        if (e instanceof ApiError && e.status === 404) {
          toast.error("Troca de ambiente indisponível", {
            description:
              "A API ainda não expõe este recurso (endpoint não encontrado). " +
              "Atualize/reimplante o backend para habilitar a troca de ambiente.",
          })
        } else {
          toast.error("Não foi possível confirmar o ambiente", {
            description: e instanceof Error ? e.message : undefined,
          })
        }
      } finally {
        setAmbienteSalvando(false)
      }
    },
    [empresaId, ambienteSalvando, mutate],
  )

  const value = useMemo<EmpresaCtx>(
    () => ({
      empresas,
      carregando: isLoading,
      empresaId,
      empresa,
      selecionar,
      ambiente,
      ambienteSalvando,
      definirAmbiente,
      recarregar: () => void mutate(),
    }),
    [empresas, isLoading, empresaId, empresa, selecionar, ambiente, ambienteSalvando, definirAmbiente, mutate],
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useEmpresa() {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error("useEmpresa deve ser usado dentro de EmpresaProvider")
  return ctx
}
