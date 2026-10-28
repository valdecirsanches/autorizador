"use client"

import useSWR from "swr"
import { useEffect, useState } from "react"
import { Upload } from "lucide-react"
import { toast } from "sonner"
import { fetcher, postForm } from "@/lib/api"
import type { Empresa, Certificado } from "@/lib/types"
import { useEmpresa } from "./empresa-provider"
import { StatusBadge } from "./status-badge"
import { Button } from "@/components/ui/button"
import { Card } from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Field, FieldGroup, FieldLabel, FieldDescription } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty"
import { formatCnpj, formatDate, daysUntil } from "@/lib/format"

function tomValidade(validoAte: string, ativo: boolean): {
  tom: "success" | "warning" | "destructive" | "muted"
  texto: string
} {
  const dias = daysUntil(validoAte)
  if (dias == null) return { tom: "muted", texto: "—" }
  if (dias < 0) return { tom: "destructive", texto: "Vencido" }
  if (dias <= 30) return { tom: "warning", texto: `Vence em ${dias}d` }
  return { tom: ativo ? "success" : "muted", texto: `${dias}d restantes` }
}

export function SecaoCertificados() {
  const { data: empresas } = useSWR<Empresa[]>("empresas", fetcher)
  const { data, error, isLoading, mutate } = useSWR<Certificado[]>("certificados", fetcher)
  // Empresa selecionada globalmente (seletor do topo): e o ALVO padrao do
  // upload, para o certificado nunca cair em outra empresa por engano.
  const { empresaId: empresaSelecionada } = useEmpresa()
  const [aberto, setAberto] = useState(false)
  const [enviando, setEnviando] = useState(false)
  const [empresaAlvo, setEmpresaAlvo] = useState("")

  // Ao abrir o dialogo, ancora o alvo na empresa selecionada no topo.
  useEffect(() => {
    if (aberto) setEmpresaAlvo(empresaSelecionada)
  }, [aberto, empresaSelecionada])

  const alvo = (empresas ?? []).find((e) => e.id === empresaAlvo) ?? null

  async function enviar(form: FormData) {
    // O alvo vem do estado controlado (nao do FormData): assim exibimos o
    // CNPJ de destino e confirmamos exatamente para quem estamos enviando.
    const empresaId = empresaAlvo
    const senha = String(form.get("senha") ?? "")
    const arquivo = form.get("arquivo") as File | null
    if (!empresaId || !arquivo || arquivo.size === 0) {
      toast.error("Selecione a empresa e o arquivo .pfx")
      return
    }
    setEnviando(true)
    try {
      // O .pfx e a senha viajam para a API .NET (via proxy same-origin), que
      // cifra a chave com AES-GCM antes de persistir. Senha nunca e guardada.
      const fd = new FormData()
      fd.set("empresaId", empresaId)
      fd.set("senha", senha)
      fd.set("arquivo", arquivo)
      await postForm("certificados", fd)
      await mutate()
      setAberto(false)
      toast.success(
        alvo ? `Certificado importado e ativado para ${alvo.razaoSocial}` : "Certificado importado e ativado",
      )
    } catch (e) {
      toast.error("Falha ao importar certificado", {
        description: e instanceof Error ? e.message : undefined,
      })
    } finally {
      setEnviando(false)
    }
  }

  const nomeEmpresa = new Map((empresas ?? []).map((e) => [e.id, e.razaoSocial]))

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end">
        <Dialog open={aberto} onOpenChange={setAberto}>
          <DialogTrigger render={<Button><Upload data-icon="inline-start" />Importar A1</Button>} />
          <DialogContent className="sm:max-w-lg">
            <DialogHeader>
              <DialogTitle>Importar certificado A1</DialogTitle>
              <DialogDescription>
                Arquivo .pfx/.p12 e a senha. A chave privada é cifrada com AES-GCM
                no servidor; a senha não é armazenada.
              </DialogDescription>
            </DialogHeader>
            <form action={enviar} id="form-cert">
              <FieldGroup>
                <Field>
                  <FieldLabel htmlFor="empresaId">Empresa</FieldLabel>
                  <Select value={empresaAlvo} onValueChange={(v) => setEmpresaAlvo(v ?? "")}>
                    <SelectTrigger id="empresaId">
                      <SelectValue placeholder="Selecione a empresa">
                        {alvo ? `${alvo.razaoSocial} — ${formatCnpj(alvo.cnpj)}` : undefined}
                      </SelectValue>
                    </SelectTrigger>
                    <SelectContent>
                      {(empresas ?? []).map((e) => (
                        <SelectItem key={e.id} value={e.id}>
                          {e.razaoSocial} — {formatCnpj(e.cnpj)}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <FieldDescription>
                    {alvo ? (
                      <>
                        O certificado será importado e ativado para{" "}
                        <span className="font-medium text-foreground">{alvo.razaoSocial}</span>{" "}
                        (CNPJ <span className="font-mono">{formatCnpj(alvo.cnpj)}</span>). Confira antes de enviar.
                      </>
                    ) : (
                      "Selecione a empresa de destino."
                    )}
                  </FieldDescription>
                </Field>
                <Field>
                  <FieldLabel htmlFor="arquivo">Arquivo .pfx</FieldLabel>
                  <Input id="arquivo" name="arquivo" type="file" accept=".pfx,.p12" required />
                </Field>
                <Field>
                  <FieldLabel htmlFor="senha">Senha do certificado</FieldLabel>
                  <Input id="senha" name="senha" type="password" required />
                  <FieldDescription>Usada apenas para abrir o arquivo agora.</FieldDescription>
                </Field>
              </FieldGroup>
            </form>
            <DialogFooter>
              <Button type="submit" form="form-cert" disabled={enviando}>
                {enviando ? "Importando..." : "Importar e ativar"}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>

      {error ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Não foi possível carregar os certificados</EmptyTitle>
            <EmptyDescription>{String(error.message ?? "")}</EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : isLoading ? (
        <Card className="p-4">
          <Skeleton className="h-40 w-full" />
        </Card>
      ) : (data?.length ?? 0) === 0 ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Nenhum certificado importado</EmptyTitle>
            <EmptyDescription>Importe um certificado A1 para habilitar a emissão.</EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Titular</TableHead>
                <TableHead>Empresa</TableHead>
                <TableHead>CNPJ</TableHead>
                <TableHead>Válido até</TableHead>
                <TableHead className="text-right">Situação</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data!.map((c) => {
                const v = tomValidade(c.validoAte, c.ativo)
                return (
                  <TableRow key={c.id}>
                    <TableCell className="max-w-56 truncate font-medium">{c.subject}</TableCell>
                    <TableCell className="max-w-40 truncate text-muted-foreground">
                      {nomeEmpresa.get(c.empresaId) ?? "—"}
                    </TableCell>
                    <TableCell className="font-mono text-xs">
                      {c.cnpjCertificado ? formatCnpj(c.cnpjCertificado) : "—"}
                    </TableCell>
                    <TableCell className="font-mono text-xs">{formatDate(c.validoAte)}</TableCell>
                    <TableCell className="text-right">
                      <div className="flex items-center justify-end gap-2">
                        {c.ativo && <StatusBadge tom="success">Ativo</StatusBadge>}
                        <StatusBadge tom={v.tom}>{v.texto}</StatusBadge>
                      </div>
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  )
}
