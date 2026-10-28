"use client";

import useSWR from "swr";
import { useId, useRef, useState } from "react";
import {
  Plus,
  Search,
  ImageOff,
  ImageUp,
  Trash2,
  FolderOpen,
} from "lucide-react";
import { ConfigPastaXml } from "./config-pasta-xml";
import { toast } from "sonner";
import { fetcher, postJson, del, ApiError } from "@/lib/api";
import type { Empresa, RegimeTributario } from "@/lib/types";
import { prepararLogo } from "@/lib/preparar-logo";
import {
  LogoUploadDiagnosticDialog,
  type LogoUploadDiagnostic,
} from "./logo-upload-diagnostic-dialog";
import { StatusBadge } from "./status-badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Field,
  FieldGroup,
  FieldLabel,
  FieldDescription,
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import {
  InputGroup,
  InputGroupInput,
  InputGroupAddon,
  InputGroupButton,
} from "@/components/ui/input-group";
import { Spinner } from "@/components/ui/spinner";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Empty,
  EmptyHeader,
  EmptyTitle,
  EmptyDescription,
} from "@/components/ui/empty";
import { formatCnpj } from "@/lib/format";

const regimes: { value: RegimeTributario; label: string }[] = [
  { value: 1, label: "Simples Nacional" },
  { value: 2, label: "Simples Nacional — excesso" },
  { value: 3, label: "Regime Normal" },
  { value: 4, label: "MEI" },
];

type FormEmpresa = {
  cnpj: string;
  razaoSocial: string;
  nomeFantasia: string;
  inscricaoEstadual: string;
  regimeTributario: string;
  logradouro: string;
  numero: string;
  bairro: string;
  codigoMunicipio: string;
  nomeMunicipio: string;
  uf: string;
  cep: string;
  telefone: string;
};

const vazio: FormEmpresa = {
  cnpj: "",
  razaoSocial: "",
  nomeFantasia: "",
  inscricaoEstadual: "",
  regimeTributario: "3",
  logradouro: "",
  numero: "",
  bairro: "",
  codigoMunicipio: "",
  nomeMunicipio: "",
  uf: "",
  cep: "",
  telefone: "",
};

async function lookup(path: string) {
  const resp = await fetch(path, { cache: "no-store" });
  const texto = await resp.text();
  let dado: unknown = null;
  try {
    dado = texto ? JSON.parse(texto) : null;
  } catch {
    dado = null;
  }
  if (!resp.ok) {
    const msg =
      dado && typeof dado === "object" && "erro" in dado
        ? String((dado as { erro?: unknown }).erro)
        : `Erro ${resp.status}`;
    throw new Error(msg);
  }
  return dado as Record<string, unknown>;
}

export function SecaoEmpresas() {
  const { data, error, isLoading, mutate } = useSWR<Empresa[]>(
    "empresas",
    fetcher,
  );
  const [aberto, setAberto] = useState(false);
  const [salvando, setSalvando] = useState(false);
  const [form, setForm] = useState<FormEmpresa>(vazio);
  const [buscandoCnpj, setBuscandoCnpj] = useState(false);
  const [buscandoCep, setBuscandoCep] = useState(false);
  // Digitos do CNPJ que geraram os dados atualmente preenchidos (razao social,
  // endereco, etc). Serve para detectar quando o CNPJ foi trocado depois da
  // busca, evitando enviar CNPJ de uma empresa com dados de outra.
  const [cnpjConsultado, setCnpjConsultado] = useState("");

  function set<K extends keyof FormEmpresa>(campo: K, valor: FormEmpresa[K]) {
    setForm((f) => ({ ...f, [campo]: valor }));
  }

  // Monta o payload EXATAMENTE como sera enviado ao backend: CNPJ e CEP apenas
  // com digitos (o backend rejeita valores formatados), UF em maiusculas e
  // opcionais como null quando vazios. Usado tanto no envio quanto na
  // pre-visualizacao de transparencia.
  function montarPayload() {
    return {
      cnpj: form.cnpj.replace(/\D/g, ""),
      razaoSocial: form.razaoSocial.trim(),
      nomeFantasia: form.nomeFantasia.trim() || null,
      inscricaoEstadual: form.inscricaoEstadual.trim(),
      regimeTributario: Number(form.regimeTributario),
      logradouro: form.logradouro.trim(),
      numero: form.numero.trim(),
      bairro: form.bairro.trim(),
      codigoMunicipio: form.codigoMunicipio.replace(/\D/g, ""),
      nomeMunicipio: form.nomeMunicipio.trim(),
      uf: form.uf.trim().toUpperCase(),
      cep: form.cep.replace(/\D/g, ""),
      telefone: form.telefone.replace(/\D/g, "") || null,
    };
  }

  // CNPJ preenchido difere do que gerou os dados? (dados possivelmente de outra
  // empresa). So alerta quando ha dados autopreenchidos por uma busca anterior.
  const cnpjAtual = form.cnpj.replace(/\D/g, "");
  const dadosDivergentes =
    cnpjConsultado !== "" &&
    cnpjAtual !== cnpjConsultado &&
    form.razaoSocial.trim() !== "";

  // Campos cadastrais que a busca por CNPJ preenche. Sao limpos a cada busca
  // para NUNCA misturarem dados de uma empresa anterior com um CNPJ novo.
  function limparCamposDerivados() {
    setCnpjConsultado("");
    setForm((f) => ({
      ...f,
      razaoSocial: "",
      nomeFantasia: "",
      regimeTributario: "3",
      logradouro: "",
      numero: "",
      bairro: "",
      nomeMunicipio: "",
      uf: "",
      cep: "",
      codigoMunicipio: "",
      telefone: "",
    }));
  }

  async function buscarCnpj() {
    const cnpj = form.cnpj.replace(/\D/g, "");
    if (cnpj.length !== 14) {
      toast.error("Informe um CNPJ com 14 dígitos para buscar.");
      return;
    }
    setBuscandoCnpj(true);
    // Zera os campos derivados ANTES de buscar. Se a busca falhar (CNPJ
    // inexistente), o formulario fica em branco em vez de manter os dados da
    // empresa buscada anteriormente — evitando cadastrar CNPJ e razao social
    // de empresas diferentes.
    limparCamposDerivados();
    try {
      const d = await lookup(`/api/lookup/cnpj/${cnpj}`);
      // Confere que o CNPJ retornado bate com o pesquisado (defesa extra).
      const cnpjRetornado = String(d.cnpj ?? "").replace(/\D/g, "");
      if (cnpjRetornado && cnpjRetornado !== cnpj) {
        throw new Error("O serviço retornou um CNPJ diferente do pesquisado.");
      }
      setForm((f) => ({
        ...f,
        razaoSocial: (d.razaoSocial as string) ?? "",
        nomeFantasia: (d.nomeFantasia as string) ?? "",
        regimeTributario: d.regimeTributario ? String(d.regimeTributario) : "3",
        logradouro: (d.logradouro as string) ?? "",
        numero: (d.numero as string) ?? "",
        bairro: (d.bairro as string) ?? "",
        nomeMunicipio: (d.nomeMunicipio as string) ?? "",
        uf: (d.uf as string) ?? "",
        cep: (d.cep as string) ?? "",
        codigoMunicipio: (d.codigoMunicipio as string) ?? "",
        telefone: (d.telefone as string) ?? "",
      }));
      // Registra o CNPJ que gerou estes dados, para detectar troca posterior.
      setCnpjConsultado(cnpj);
      toast.success("Dados do CNPJ preenchidos");
    } catch (e) {
      // Campos ja foram limpos acima; so avisamos o motivo.
      toast.error("CNPJ não encontrado — confira o número", {
        description: e instanceof Error ? e.message : undefined,
      });
    } finally {
      setBuscandoCnpj(false);
    }
  }

  async function buscarCep() {
    const cep = form.cep.replace(/\D/g, "");
    if (cep.length !== 8) {
      toast.error("Informe um CEP com 8 dígitos para buscar.");
      return;
    }
    setBuscandoCep(true);
    try {
      const d = await lookup(`/api/lookup/cep/${cep}`);
      setForm((f) => ({
        ...f,
        logradouro: (d.logradouro as string) || f.logradouro,
        bairro: (d.bairro as string) || f.bairro,
        nomeMunicipio: (d.nomeMunicipio as string) || f.nomeMunicipio,
        uf: (d.uf as string) || f.uf,
        codigoMunicipio: (d.codigoMunicipio as string) || f.codigoMunicipio,
      }));
      toast.success("Endereço do CEP preenchido");
    } catch (e) {
      toast.error("Não foi possível buscar o CEP", {
        description: e instanceof Error ? e.message : undefined,
      });
    } finally {
      setBuscandoCep(false);
    }
  }

  async function criar(e: React.FormEvent) {
    e.preventDefault();

    // Validacao client-side com mensagens claras — evita o 400 cru do backend
    // e deixa explicito qual campo falta.
    const cnpjDigitos = form.cnpj.replace(/\D/g, "");
    if (cnpjDigitos.length !== 14) {
      toast.error("CNPJ inválido", {
        description: "Informe um CNPJ com 14 dígitos.",
      });
      return;
    }
    if (!form.razaoSocial.trim()) {
      toast.error("Razão social obrigatória", {
        description:
          "Busque o CNPJ na Receita ou preencha a razão social manualmente.",
      });
      return;
    }
    if (form.uf.trim().length !== 2) {
      toast.error("UF obrigatória", {
        description: "Informe a UF com 2 letras (ex.: SP).",
      });
      return;
    }

    // Barra o envio quando o CNPJ foi trocado depois da busca: os dados
    // preenchidos podem ser de OUTRA empresa. Exige refazer a busca.
    if (dadosDivergentes) {
      toast.error("CNPJ não confere com os dados preenchidos", {
        description:
          `Os dados foram buscados para o CNPJ ${formatCnpj(cnpjConsultado)}, mas o campo ` +
          `agora tem outro CNPJ. Clique em Buscar novamente para confirmar.`,
      });
      return;
    }

    const payload = montarPayload();
    setSalvando(true);
    try {
      const resp = await postJson<{
        atualizado?: boolean;
        camposAlterados?: string[];
      }>("empresas", payload);
      await mutate();
      setAberto(false);
      setForm(vazio);
      setCnpjConsultado("");

      if (resp?.atualizado) {
        // CNPJ ja existia: o backend comparou e atualizou os dados divergentes.
        const campos = resp.camposAlterados ?? [];
        toast.success("Empresa já cadastrada — dados atualizados", {
          description:
            campos.length > 0
              ? `Campos atualizados: ${campos.join(", ")}.`
              : "Os dados enviados já estavam atualizados; nada foi alterado.",
        });
      } else {
        toast.success("Empresa cadastrada");
      }
    } catch (err) {
      toast.error("Falha ao cadastrar empresa", {
        description: err instanceof Error ? err.message : undefined,
      });
    } finally {
      setSalvando(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end">
        <Dialog
          open={aberto}
          onOpenChange={(o) => {
            setAberto(o);
            if (!o) {
              setForm(vazio);
              setCnpjConsultado("");
            }
          }}
        >
          <DialogTrigger
            render={
              <Button>
                <Plus data-icon="inline-start" />
                Nova empresa
              </Button>
            }
          />
          <DialogContent className="max-h-[90svh] overflow-auto sm:max-w-2xl">
            <DialogHeader>
              <DialogTitle>Nova empresa emitente</DialogTitle>
              <DialogDescription>
                Digite o CNPJ ou o CEP e clique em buscar para preencher
                automaticamente.
              </DialogDescription>
            </DialogHeader>
            <form onSubmit={criar} id="form-empresa">
              <FieldGroup>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <Field>
                    <FieldLabel htmlFor="cnpj">CNPJ</FieldLabel>
                    <InputGroup>
                      <InputGroupInput
                        id="cnpj"
                        value={form.cnpj}
                        onChange={(e) => set("cnpj", e.target.value)}
                        required
                        placeholder="00.000.000/0000-00"
                      />
                      <InputGroupAddon align="inline-end">
                        <InputGroupButton
                          type="button"
                          onClick={buscarCnpj}
                          disabled={buscandoCnpj}
                          aria-label="Buscar dados do CNPJ"
                        >
                          {buscandoCnpj ? <Spinner /> : <Search />}
                          Buscar
                        </InputGroupButton>
                      </InputGroupAddon>
                    </InputGroup>
                    <FieldDescription>
                      Busca na Receita (BrasilAPI)
                    </FieldDescription>
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="inscricaoEstadual">
                      Inscrição estadual
                    </FieldLabel>
                    <Input
                      id="inscricaoEstadual"
                      value={form.inscricaoEstadual}
                      onChange={(e) => set("inscricaoEstadual", e.target.value)}
                      required
                      placeholder="ISENTO ou número"
                    />
                  </Field>
                </div>
                <Field>
                  <FieldLabel htmlFor="razaoSocial">Razão social</FieldLabel>
                  <Input
                    id="razaoSocial"
                    value={form.razaoSocial}
                    onChange={(e) => set("razaoSocial", e.target.value)}
                    required
                  />
                </Field>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <Field>
                    <FieldLabel htmlFor="nomeFantasia">
                      Nome fantasia
                    </FieldLabel>
                    <Input
                      id="nomeFantasia"
                      value={form.nomeFantasia}
                      onChange={(e) => set("nomeFantasia", e.target.value)}
                    />
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="regimeTributario">
                      Regime tributário
                    </FieldLabel>
                    <Select
                      value={form.regimeTributario}
                      onValueChange={(v) => v && set("regimeTributario", v)}
                    >
                      <SelectTrigger id="regimeTributario">
                        <SelectValue>
                          {regimes.find((r) => String(r.value) === form.regimeTributario)?.label}
                        </SelectValue>
                      </SelectTrigger>
                      <SelectContent>
                        {regimes.map((r) => (
                          <SelectItem key={r.value} value={String(r.value)}>
                            {r.label}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </Field>
                </div>
                <Field>
                  <FieldLabel htmlFor="cep">CEP</FieldLabel>
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-[240px_1fr]">
                    <InputGroup>
                      <InputGroupInput
                        id="cep"
                        value={form.cep}
                        onChange={(e) => set("cep", e.target.value)}
                        required
                        placeholder="90000-000"
                      />
                      <InputGroupAddon align="inline-end">
                        <InputGroupButton
                          type="button"
                          onClick={buscarCep}
                          disabled={buscandoCep}
                          aria-label="Buscar endereço do CEP"
                        >
                          {buscandoCep ? <Spinner /> : <Search />}
                          Buscar
                        </InputGroupButton>
                      </InputGroupAddon>
                    </InputGroup>
                  </div>
                </Field>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_120px]">
                  <Field>
                    <FieldLabel htmlFor="logradouro">Logradouro</FieldLabel>
                    <Input
                      id="logradouro"
                      value={form.logradouro}
                      onChange={(e) => set("logradouro", e.target.value)}
                      required
                    />
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="numero">Número</FieldLabel>
                    <Input
                      id="numero"
                      value={form.numero}
                      onChange={(e) => set("numero", e.target.value)}
                      required
                    />
                  </Field>
                </div>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                  <Field>
                    <FieldLabel htmlFor="bairro">Bairro</FieldLabel>
                    <Input
                      id="bairro"
                      value={form.bairro}
                      onChange={(e) => set("bairro", e.target.value)}
                      required
                    />
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="nomeMunicipio">Município</FieldLabel>
                    <Input
                      id="nomeMunicipio"
                      value={form.nomeMunicipio}
                      onChange={(e) => set("nomeMunicipio", e.target.value)}
                      required
                    />
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="codigoMunicipio">Cód. IBGE</FieldLabel>
                    <Input
                      id="codigoMunicipio"
                      value={form.codigoMunicipio}
                      onChange={(e) => set("codigoMunicipio", e.target.value)}
                      required
                      placeholder="Ex.: 3550308"
                    />
                    <FieldDescription>7 dígitos</FieldDescription>
                  </Field>
                </div>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                  <Field>
                    <FieldLabel htmlFor="uf">UF</FieldLabel>
                    <Input
                      id="uf"
                      value={form.uf}
                      onChange={(e) => set("uf", e.target.value.toUpperCase())}
                      required
                      maxLength={2}
                      placeholder="Ex.: SP"
                    />
                  </Field>
                  <Field>
                    <FieldLabel htmlFor="telefone">Telefone</FieldLabel>
                    <Input
                      id="telefone"
                      value={form.telefone}
                      onChange={(e) => set("telefone", e.target.value)}
                    />
                  </Field>
                </div>
              </FieldGroup>
            </form>

            {dadosDivergentes && (
              <div
                role="alert"
                className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
              >
                Atenção: os dados abaixo foram buscados para o CNPJ{" "}
                <strong>{formatCnpj(cnpjConsultado)}</strong>, mas o campo CNPJ
                agora tem <strong>{formatCnpj(cnpjAtual) || cnpjAtual}</strong>.
                Clique em <strong>Buscar</strong> de novo para não cadastrar
                dados de empresas diferentes.
              </div>
            )}

            {/* Transparencia: mostra EXATAMENTE o JSON que sera enviado ao
                backend, ja normalizado (CNPJ/CEP so com digitos). */}
            <details className="rounded-md border border-border bg-muted/40 text-sm">
              <summary className="cursor-pointer px-3 py-2 font-medium text-muted-foreground">
                Ver dados que serão enviados
              </summary>
              <pre className="overflow-auto border-t border-border px-3 py-2 font-mono text-xs leading-relaxed text-foreground">
                {JSON.stringify(montarPayload(), null, 2)}
              </pre>
            </details>

            <DialogFooter>
              <Button
                type="submit"
                form="form-empresa"
                disabled={salvando || dadosDivergentes}
              >
                {salvando ? "Salvando..." : "Cadastrar"}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>

      {error ? (
        <Empty>
          <EmptyHeader>
            <EmptyTitle>Não foi possível carregar as empresas</EmptyTitle>
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
            <EmptyTitle>Nenhuma empresa cadastrada</EmptyTitle>
            <EmptyDescription>
              Cadastre a primeira empresa emitente para começar.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <Card className="overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>CNPJ</TableHead>
                <TableHead>Razão social</TableHead>
                <TableHead>UF</TableHead>
                <TableHead>Autorizador</TableHead>
                <TableHead>Logo</TableHead>
                <TableHead>Certificado</TableHead>
                <TableHead>Pasta de XMLs</TableHead>
                <TableHead className="text-right">Ações</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data!.map((e) => (
                <TableRow key={e.id}>
                  <TableCell className="font-mono text-xs">
                    {formatCnpj(e.cnpj)}
                  </TableCell>
                  <TableCell className="max-w-56 truncate font-medium">
                    {e.razaoSocial}
                  </TableCell>
                  <TableCell className="font-mono">{e.uf}</TableCell>
                  <TableCell className="font-mono text-muted-foreground">
                    {e.ufAutorizadorOverride ?? e.uf}
                  </TableCell>
                  <TableCell>
                    <LogoUpload empresa={e} onMudou={() => mutate()} />
                  </TableCell>
                  <TableCell>
                    {e.temCertificadoAtivo ? (
                      <StatusBadge tom="success">Ativo</StatusBadge>
                    ) : (
                      <StatusBadge tom="warning">Ausente</StatusBadge>
                    )}
                  </TableCell>
                  <TableCell>
                    <PastaXmlCelula empresa={e} onMudou={() => mutate()} />
                  </TableCell>
                  <TableCell className="text-right">
                    <ExcluirEmpresa empresa={e} onExcluida={() => mutate()} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  );
}

function PastaXmlCelula({
  empresa,
  onMudou,
}: {
  empresa: Empresa;
  onMudou: () => void;
}) {
  const [aberto, setAberto] = useState(false);
  return (
    <>
      <Button
        variant="ghost"
        size="sm"
        className="max-w-48 justify-start gap-2"
        onClick={() => setAberto(true)}
        title={empresa.pastaXmlBackup ?? undefined}
      >
        <FolderOpen className="size-4 shrink-0" />
        <span className="truncate text-xs">
          {empresa.pastaXmlBackup ? empresa.pastaXmlBackup : "Configurar"}
        </span>
      </Button>
      {aberto ? (
        <ConfigPastaXml
          key={empresa.pastaXmlBackup ?? ""}
          empresa={empresa}
          aberto={aberto}
          onFechar={() => setAberto(false)}
          onSalvo={onMudou}
        />
      ) : null}
    </>
  );
}

/**
 * Descadastro de empresa. Abre um dialogo de confirmacao e chama
 * DELETE /api/empresas/{id}. Empresa que ja emitiu notas nao pode ser excluida
 * (guarda fiscal): o backend responde 409 e mostramos a orientacao de desativar.
 */
function ExcluirEmpresa({
  empresa,
  onExcluida,
}: {
  empresa: Empresa;
  onExcluida: () => void;
}) {
  const [aberto, setAberto] = useState(false);
  const [excluindo, setExcluindo] = useState(false);

  async function confirmar() {
    setExcluindo(true);
    try {
      await del(`empresas/${empresa.id}`);
      setAberto(false);
      onExcluida();
      toast.success("Empresa descadastrada");
    } catch (e) {
      // 409 = possui documentos fiscais (guarda obrigatoria): a mensagem do
      // backend ja orienta a desativar em vez de excluir.
      const conflito = e instanceof ApiError && e.status === 409;
      toast.error(conflito ? "Não é possível excluir" : "Falha ao excluir", {
        description: e instanceof Error ? e.message : undefined,
      });
    } finally {
      setExcluindo(false);
    }
  }

  return (
    <Dialog open={aberto} onOpenChange={setAberto}>
      <DialogTrigger
        render={
          <Button
            variant="ghost"
            size="icon"
            aria-label={`Excluir ${empresa.razaoSocial}`}
          >
            <Trash2 className="size-4 text-destructive" />
          </Button>
        }
      />
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Descadastrar empresa</DialogTitle>
          <DialogDescription>
            {`Excluir "${empresa.razaoSocial}" (${formatCnpj(empresa.cnpj)})? Isso remove o cadastro, o certificado e o logo. `}
            Empresas que já emitiram notas não podem ser excluídas — nesse caso,
            desative a empresa para preservar o histórico.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button
            variant="outline"
            onClick={() => setAberto(false)}
            disabled={excluindo}
          >
            Cancelar
          </Button>
          <Button
            variant="destructive"
            onClick={confirmar}
            disabled={excluindo}
          >
            {excluindo ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Trash2 data-icon="inline-start" />
            )}
            Excluir
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Upload do logotipo do emitente, impresso no DANFE. Sem logo, o DANFE imprime
 * apenas o texto do emitente. A API valida os bytes reais e faz upsert.
 */
function formatDiagnosticBody(text: string) {
  if (!text) return "";
  let formatted = text;
  try {
    formatted = JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    // Respostas HTML/texto de proxy também são úteis no diagnóstico.
  }
  return formatted.length > 12000
    ? `${formatted.slice(0, 12000)}\n… resposta truncada`
    : formatted;
}

function LogoUpload({
  empresa,
  onMudou,
}: {
  empresa: Empresa;
  onMudou: () => void;
}) {
  const fileInputId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [enviando, setEnviando] = useState(false);
  const [diagnostico, setDiagnostico] = useState<LogoUploadDiagnostic | null>(
    null,
  );
  const [diagnosticoAberto, setDiagnosticoAberto] = useState(false);
  const [nomeArquivoSelecionado, setNomeArquivoSelecionado] = useState<string | null>(null);
  const [chaveInput, setChaveInput] = useState(0);
  const requestUrl = `/api/proxy/empresas/${encodeURIComponent(empresa.id)}/logo`;
  // Muda a cada upload para forcar o navegador a reobter a imagem (mesma URL).
  const [versao, setVersao] = useState(0);

  async function enviar(arquivo: File) {
    const originalFile = {
      name: arquivo.name,
      type: arquivo.type,
      size: arquivo.size,
    };
    setDiagnostico({ state: "preparing", requestUrl, originalFile });
    setDiagnosticoAberto(true);

    if (arquivo.size > 25 * 1024 * 1024) {
      const error = `Arquivo tem ${(arquivo.size / 1024 / 1024).toFixed(1)} MB; o limite local é 25 MB. Nenhuma requisição foi enviada.`;
      setDiagnostico({ state: "error", requestUrl, originalFile, error });
      toast.error("Imagem muito grande", { description: error });
      return;
    }

    setEnviando(true);
    try {
      let conversionWarning: string | undefined;
      const { blob, nome } = await prepararLogo(arquivo).catch((erro) => {
        conversionWarning = erro instanceof Error ? erro.message : String(erro);
        return { blob: arquivo as Blob, nome: arquivo.name || "logo.png" };
      });
      const sentFile = {
        name: nome,
        type: blob.type || arquivo.type,
        size: blob.size,
      };
      const fd = new FormData();
      fd.append("arquivo", blob, nome);
      setDiagnostico({
        state: "sending",
        requestUrl,
        originalFile,
        sentFile,
        conversionWarning,
      });

      const startedAt = performance.now();
      const response = await fetch(requestUrl, {
        method: "POST",
        credentials: "same-origin",
        body: fd,
      });
      const responseText = await response.text();
      const post = {
        status: response.status,
        statusText: response.statusText,
        contentType: response.headers.get("content-type") ?? undefined,
        requestId:
          response.headers.get("x-request-id") ??
          response.headers.get("x-correlation-id") ??
          response.headers.get("traceparent") ??
          undefined,
        durationMs: Math.round(performance.now() - startedAt),
        body: formatDiagnosticBody(responseText),
      };

      if (!response.ok) {
        const error =
          response.status === 413
            ? "O proxy recusou o tamanho do arquivo (HTTP 413)."
            : `O servidor recusou o upload: HTTP ${response.status} ${response.statusText}.`;
        setDiagnostico({
          state: "error",
          requestUrl,
          originalFile,
          sentFile,
          conversionWarning,
          post,
          error,
        });
        toast.error("Falha ao enviar o logo", {
          description: error,
          duration: 15000,
        });
        return;
      }

      setDiagnostico({
        state: "verifying",
        requestUrl,
        originalFile,
        sentFile,
        conversionWarning,
        post,
      });
      const verificationUrl = `${requestUrl}?v=${Date.now()}`;
      const verificationStartedAt = performance.now();
      const verificationResponse = await fetch(verificationUrl, {
        cache: "no-store",
        credentials: "same-origin",
      });
      const image = await verificationResponse.blob();
      const contentType =
        verificationResponse.headers.get("content-type") ?? undefined;
      const confirmed =
        verificationResponse.ok &&
        Boolean(contentType?.startsWith("image/")) &&
        image.size > 0;
      const verificationBody = confirmed
        ? undefined
        : formatDiagnosticBody(await image.text());
      const verification = {
        url: verificationUrl,
        status: verificationResponse.status,
        statusText: verificationResponse.statusText,
        contentType,
        size: image.size,
        confirmed,
        durationMs: Math.round(performance.now() - verificationStartedAt),
        body: verificationBody,
        error: confirmed
          ? undefined
          : "A resposta do GET não contém uma imagem gravada.",
      };

      setDiagnostico({
        state: confirmed ? "success" : "error",
        requestUrl,
        originalFile,
        sentFile,
        conversionWarning,
        post,
        verification,
        error: confirmed
          ? undefined
          : "POST aceito, mas a leitura imediata não confirmou a gravação.",
      });
      setVersao((v) => v + 1);
      onMudou();
      if (confirmed) {
        toast.success("Logo enviado e confirmado no servidor", {
          description: `${nome} · ${Math.max(1, Math.round(blob.size / 1024))} KB. Veja os detalhes na janela de diagnóstico.`,
        });
      } else {
        toast.error(
          "O servidor aceitou o POST, mas não confirmou o logo gravado",
          {
            description: "Confira o GET e a resposta na janela de diagnóstico.",
            duration: 15000,
          },
        );
      }
    } catch (erro) {
      const error = erro instanceof Error ? erro.message : String(erro);
      setDiagnostico((atual) =>
        atual
          ? {
              ...atual,
              state: "error",
              error: `Falha de rede ou resposta: ${error}`,
            }
          : atual,
      );
      toast.error("Falha ao enviar ou confirmar o logo", {
        description: error,
        duration: 15000,
      });
    } finally {
      setEnviando(false);
    }
  }

  async function remover() {
    setEnviando(true);
    try {
      const resp = await fetch(`/api/proxy/empresas/${empresa.id}/logo`, {
        method: "DELETE",
      });
      if (!resp.ok) throw new Error(`Erro ${resp.status}`);
      onMudou();
      toast.success("Logo removido");
    } catch (e) {
      toast.error("Falha ao remover o logo", {
        description: e instanceof Error ? e.message : undefined,
      });
    } finally {
      setEnviando(false);
    }
  }

  return (
    <div className="flex min-w-max items-center gap-2">
      {empresa.temLogo ? (
        <>
          <img
            src={`/api/proxy/empresas/${empresa.id}/logo?v=${versao}`}
            alt={`Logo de ${empresa.razaoSocial}`}
            className="h-7 w-auto max-w-16 rounded border border-border bg-card object-contain"
          />
          <Button
            variant="ghost"
            size="icon"
            onClick={remover}
            disabled={enviando}
            aria-label="Remover logo"
          >
            <ImageOff className="size-4" />
          </Button>
        </>
      ) : null}
      {/* Input de arquivo escondido, acionado por um botao de tamanho fixo. O
          input nativo ficava espremido na coluna da tabela (33 px em telas de
          1024 px) e praticamente nao dava para clicar nele. */}
      <input
        key={chaveInput}
        ref={inputRef}
        id={fileInputId}
        type="file"
        accept="image/*"
        disabled={enviando}
        className="sr-only"
        tabIndex={-1}
        aria-hidden
        onChange={(event) => {
          const arquivo = event.currentTarget.files?.[0];
          if (!arquivo) return;
          setNomeArquivoSelecionado(arquivo.name);
          setChaveInput((chave) => chave + 1);
          void enviar(arquivo);
        }}
      />
      <Button
        type="button"
        variant="outline"
        size="sm"
        className="shrink-0"
        disabled={enviando}
        onClick={() => inputRef.current?.click()}
        aria-label={`${empresa.temLogo ? "Trocar" : "Enviar"} logo de ${empresa.razaoSocial}`}
        title={nomeArquivoSelecionado ? `Último arquivo: ${nomeArquivoSelecionado}` : undefined}
      >
        {enviando ? <Spinner data-icon="inline-start" /> : <ImageUp data-icon="inline-start" />}
        {enviando ? "Enviando…" : empresa.temLogo ? "Trocar" : "Enviar logo"}
      </Button>
      {diagnostico && !diagnosticoAberto ? (
        <Button
          variant="outline"
          size="sm"
          onClick={() => setDiagnosticoAberto(true)}
        >
          Ver diagnóstico
        </Button>
      ) : null}
      {diagnostico ? (
        <LogoUploadDiagnosticDialog
          open={diagnosticoAberto}
          onOpenChange={setDiagnosticoAberto}
          diagnostic={diagnostico}
        />
      ) : null}
    </div>
  );
}
