"use client";

import useSWR from "swr";
import { useEffect, useId, useRef, useState } from "react";
import {
  Plus,
  Search,
  ImageOff,
  ImageUp,
  ClipboardPaste,
  Trash2,
  FolderOpen,
} from "lucide-react";
import { ConfigPastaXml } from "./config-pasta-xml";
import { toast } from "sonner";
import { fetcher, postJson, del, ApiError } from "@/lib/api";
import type { Empresa, RegimeTributario } from "@/lib/types";
import { prepararLogo } from "@/lib/preparar-logo";
import { rastrear, type NivelRastro } from "@/lib/rastro-logo";
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
  // Soltar um arquivo fora da area do logo faria o navegador SAIR do painel
  // para abrir a imagem. Bloqueia o comportamento padrao enquanto a tela esta
  // aberta; quem trata o arquivo e a area de logo de cada empresa.
  useEffect(() => {
    const bloquear = (e: DragEvent) => {
      if (e.dataTransfer?.types.includes("Files")) e.preventDefault();
    };
    window.addEventListener("dragover", bloquear);
    window.addEventListener("drop", bloquear);
    return () => {
      window.removeEventListener("dragover", bloquear);
      window.removeEventListener("drop", bloquear);
    };
  }, []);

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

/** Explicacao mostrada quando o seletor devolve "cancelado" sem arquivo. */
const DICA_SELETOR =
  "Nenhum arquivo chegou ao painel. Se você escolheu um arquivo, o navegador não conseguiu lê-lo: " +
  "no Ubuntu, Firefox/Chromium instalados como snap só leem arquivos da sua pasta pessoal " +
  "(não de /mnt, pendrive, rede ou pastas ocultas). Copie a imagem para Downloads e tente de novo, " +
  "ou use o botão Colar (Ctrl+V).";

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

  // ---- Rastreio (depuracao): cada etapa vai para o painel "Rastreador" e
  // para o log do servidor do painel. `ultimo` aparece ao lado do botao.
  const nomeEmpresa = empresa.razaoSocial;
  const [ultimo, setUltimo] = useState<{ texto: string; nivel: NivelRastro } | null>(null);
  function passo(etapa: string, detalhe?: string, nivel: NivelRastro = "info") {
    rastrear(nomeEmpresa, etapa, detalhe, nivel);
    if (nivel === "erro") setUltimo({ texto: detalhe ? `${etapa}: ${detalhe}` : etapa, nivel });
  }
  const descreverArquivo = (f: File) =>
    `"${f.name}" · ${f.type || "tipo vazio"} · ${(f.size / 1024).toFixed(1)} KB`;

  // Seletor de arquivo: detecta quando ele fecha sem entregar arquivo.
  const aguardandoArquivo = useRef(false);
  function abrirSeletor() {
    const input = inputRef.current;
    passo(
      "Clique no botão",
      `input ${input ? "presente" : "AUSENTE"}${input?.disabled ? " (desabilitado)" : ""}; enviando=${enviando}`,
    );
    if (!input) {
      passo("Seletor não abriu", "O campo de arquivo não existe na página.", "erro");
      return;
    }
    aguardandoArquivo.current = true;
    const aoVoltar = () => {
      window.removeEventListener("focus", aoVoltar);
      setTimeout(() => {
        if (!aguardandoArquivo.current) return;
        aguardandoArquivo.current = false;
        passo(
          "Seletor fechou sem arquivo",
          DICA_SELETOR,
          "aviso",
        );
      }, 1500);
    };
    window.addEventListener("focus", aoVoltar);
    try {
      input.click();
      passo("Seletor de arquivo solicitado ao navegador");
    } catch (e) {
      passo("Seletor não abriu", e instanceof Error ? e.message : String(e), "erro");
    }
  }

  // Evento nativo 'cancel' do seletor (Chrome 113+, Firefox 91+, Safari 16.4+).
  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    const aoCancelar = () => {
      if (!aguardandoArquivo.current) return;
      aguardandoArquivo.current = false;
      passo("Seletor cancelado", DICA_SELETOR, "aviso");
    };
    el.addEventListener("cancel", aoCancelar);
    return () => el.removeEventListener("cancel", aoCancelar);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chaveInput]);

  // ---- Colar com Ctrl+V: caminho que nao depende do seletor de arquivos do
  // sistema (no Ubuntu, navegador snap nao le arquivos fora da pasta pessoal e
  // o seletor devolve "cancelado"). Funciona em HTTP comum, sem HTTPS.
  const [aguardandoColar, setAguardandoColar] = useState(false);
  useEffect(() => {
    if (!aguardandoColar) return;
    const aoColar = (e: ClipboardEvent) => {
      e.preventDefault();
      setAguardandoColar(false);
      const dados = e.clipboardData;
      const tipos = dados ? [...dados.types].join(", ") : "(sem dados)";
      let arquivo: File | null = null;
      if (dados) {
        arquivo = [...dados.files].find((f) => f.type.startsWith("image/")) ?? null;
        if (!arquivo) {
          for (const item of [...dados.items]) {
            if (item.kind === "file" && item.type.startsWith("image/")) {
              arquivo = item.getAsFile();
              if (arquivo) break;
            }
          }
        }
      }
      if (!arquivo) {
        passo(
          "Colado sem imagem",
          `tipos=[${tipos}]. Copie a IMAGEM (abra o logo e use Ctrl+C, ou clique com o botão direito > Copiar imagem), não o arquivo no gerenciador de arquivos.`,
          "erro",
        );
        return;
      }
      const nomeFinal = arquivo.name && arquivo.name !== "image.png" ? arquivo.name : `logo-colado.${arquivo.type.split("/")[1] ?? "png"}`;
      const colado = new File([arquivo], nomeFinal, { type: arquivo.type });
      passo("Imagem colada", descreverArquivo(colado));
      setNomeArquivoSelecionado(colado.name);
      void enviar(colado);
    };
    const aoTeclar = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setAguardandoColar(false);
        passo("Colar cancelado", "Esc pressionado.", "aviso");
      }
    };
    const expirar = window.setTimeout(() => {
      setAguardandoColar(false);
      passo("Colar expirou", "Nenhum Ctrl+V em 60 s.", "aviso");
    }, 60_000);
    window.addEventListener("paste", aoColar);
    window.addEventListener("keydown", aoTeclar);
    return () => {
      window.clearTimeout(expirar);
      window.removeEventListener("paste", aoColar);
      window.removeEventListener("keydown", aoTeclar);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [aguardandoColar]);

  // Lista de empresas: registra quando o "temLogo" desta empresa muda.
  const temLogoAnterior = useRef(empresa.temLogo);
  useEffect(() => {
    if (temLogoAnterior.current !== empresa.temLogo) {
      passo(
        "Lista de empresas atualizada",
        `temLogo: ${temLogoAnterior.current} → ${empresa.temLogo}`,
        empresa.temLogo ? "ok" : "aviso",
      );
      temLogoAnterior.current = empresa.temLogo;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [empresa.temLogo]);

  // Arrastar e soltar: a celula do logo aceita a imagem solta sobre ela.
  const [arrastando, setArrastando] = useState(false);
  const temArquivo = (e: React.DragEvent) => e.dataTransfer.types.includes("Files");

  function soltar(e: React.DragEvent) {
    if (!temArquivo(e)) return;
    e.preventDefault();
    e.stopPropagation();
    setArrastando(false);
    passo(
      "Arquivo solto na célula",
      `tipos=[${[...e.dataTransfer.types].join(", ")}] arquivos=${e.dataTransfer.files?.length ?? 0}` +
        (e.dataTransfer.files?.[0] ? ` · ${descreverArquivo(e.dataTransfer.files[0])}` : ""),
    );
    if (enviando) {
      passo("Ignorado", "Já existe um envio em andamento.", "aviso");
      return;
    }
    const arquivo = e.dataTransfer.files?.[0];
    if (!arquivo) {
      passo("Nada para enviar", "O navegador não entregou arquivo no 'drop' (pasta, link ou arquivo remoto).", "erro");
      toast.error("Nenhum arquivo recebido", {
        description: "Arraste o arquivo de imagem (não a pasta) ou use o botão Enviar logo.",
      });
      return;
    }
    if (arquivo.type && !arquivo.type.startsWith("image/")) {
      passo("Recusado", `não é imagem: ${descreverArquivo(arquivo)}`, "erro");
      toast.error("O arquivo solto não é uma imagem", {
        description: `${arquivo.name} (${arquivo.type}). Use PNG ou JPEG.`,
      });
      return;
    }
    setNomeArquivoSelecionado(arquivo.name);
    void enviar(arquivo);
  }

  async function enviar(arquivo: File) {
    const originalFile = {
      name: arquivo.name,
      type: arquivo.type,
      size: arquivo.size,
    };
    setDiagnostico({ state: "preparing", requestUrl, originalFile });
    setUltimo(null);
    passo("Preparando imagem", descreverArquivo(arquivo));

    if (arquivo.size > 25 * 1024 * 1024) {
      const error = `Arquivo tem ${(arquivo.size / 1024 / 1024).toFixed(1)} MB; o limite local é 25 MB. Nenhuma requisição foi enviada.`;
      passo("Recusado", error, "erro");
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
      passo(
        "Imagem preparada",
        `enviando "${nome}" · ${sentFile.type || "tipo vazio"} · ${(blob.size / 1024).toFixed(1)} KB` +
          (conversionWarning ? ` · conversão falhou, vai o original: ${conversionWarning}` : ""),
        conversionWarning ? "aviso" : "info",
      );
      const fd = new FormData();
      fd.append("arquivo", blob, nome);
      setDiagnostico({
        state: "sending",
        requestUrl,
        originalFile,
        sentFile,
        conversionWarning,
      });

      passo("POST enviado", requestUrl);
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

      passo(
        "Resposta do POST",
        `HTTP ${response.status} em ${post.durationMs} ms · ${post.contentType ?? "sem content-type"} · ${responseText.slice(0, 300) || "(corpo vazio)"}`,
        response.ok ? "ok" : "erro",
      );

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
          action: { label: "Ver diagnóstico", onClick: () => setDiagnosticoAberto(true) },
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

      passo(
        "Conferência (GET do logo)",
        `HTTP ${verificationResponse.status} · ${contentType ?? "sem content-type"} · ${image.size} bytes`,
        confirmed ? "ok" : "erro",
      );
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
      passo("Recarregando a lista de empresas");
      onMudou();
      passo(confirmed ? "Concluído" : "Concluído com erro", confirmed ? "logo gravado e lido de volta" : "o GET não devolveu a imagem", confirmed ? "ok" : "erro");
      if (confirmed) {
        toast.success("Logo enviado e confirmado no servidor", {
          description: `${nome} · ${Math.max(1, Math.round(blob.size / 1024))} KB.`,
        });
      } else {
        toast.error(
          "O servidor aceitou o POST, mas não confirmou o logo gravado",
          {
            description: "Confira o GET e a resposta na janela de diagnóstico.",
            duration: 15000,
            action: { label: "Ver diagnóstico", onClick: () => setDiagnosticoAberto(true) },
          },
        );
      }
    } catch (erro) {
      const error = erro instanceof Error ? erro.message : String(erro);
      passo("Falha de rede ou exceção", error, "erro");
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
        action: { label: "Ver diagnóstico", onClick: () => setDiagnosticoAberto(true) },
      });
    } finally {
      setEnviando(false);
    }
  }

  async function remover() {
    passo("Remover logo");
    setEnviando(true);
    try {
      const resp = await fetch(`/api/proxy/empresas/${empresa.id}/logo`, {
        method: "DELETE",
      });
      if (!resp.ok) throw new Error(`Erro ${resp.status}`);
      passo("Logo removido", `HTTP ${resp.status}`, "ok");
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
    <div
      className={`-m-1 flex min-w-max items-center gap-2 rounded-md p-1 transition-colors ${
        arrastando ? "bg-primary/10 ring-2 ring-primary" : ""
      }`}
      onDragEnter={(e) => {
        if (!temArquivo(e)) return;
        if (!arrastando) passo("Arrastando sobre a célula", `tipos=[${[...e.dataTransfer.types].join(", ")}]`);
        setArrastando(true);
      }}
      onDragOver={(e) => {
        if (!temArquivo(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = "copy";
        if (!arrastando) setArrastando(true);
      }}
      onDragLeave={(e) => {
        // So desliga ao sair da celula, nao ao passar sobre um filho dela.
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setArrastando(false);
      }}
      onDrop={soltar}
      title="Clique em Enviar logo ou arraste a imagem para cá"
    >
      {arrastando ? (
        <span className="px-2 text-xs font-medium text-primary">Solte a imagem aqui</span>
      ) : null}
      {!arrastando && empresa.temLogo ? (
        <>
          <img
            src={`/api/proxy/empresas/${empresa.id}/logo?v=${versao}`}
            alt={`Logo de ${empresa.razaoSocial}`}
            className="h-7 w-auto max-w-16 rounded border border-border bg-card object-contain"
            onLoad={(e) =>
              versao > 0 &&
              passo(
                "Imagem exibida na célula",
                `${e.currentTarget.naturalWidth}x${e.currentTarget.naturalHeight} px`,
                "ok",
              )
            }
            onError={() =>
              passo("Imagem NÃO carregou na célula", `GET /api/proxy/empresas/${empresa.id}/logo falhou`, "erro")
            }
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
          aguardandoArquivo.current = false;
          const arquivo = event.currentTarget.files?.[0];
          if (!arquivo) {
            passo("Evento 'change' sem arquivo", `files=${event.currentTarget.files?.length ?? "null"}`, "aviso");
            return;
          }
          passo("Arquivo escolhido no seletor", descreverArquivo(arquivo));
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
        onClick={abrirSeletor}
        aria-label={`${empresa.temLogo ? "Trocar" : "Enviar"} logo de ${empresa.razaoSocial}`}
        title={nomeArquivoSelecionado ? `Último arquivo: ${nomeArquivoSelecionado}` : undefined}
      >
        {enviando ? <Spinner data-icon="inline-start" /> : <ImageUp data-icon="inline-start" />}
        {enviando ? "Enviando…" : empresa.temLogo ? "Trocar" : "Enviar logo"}
      </Button>
      <Button
        type="button"
        variant={aguardandoColar ? "default" : "ghost"}
        size="sm"
        className="shrink-0"
        disabled={enviando}
        onClick={() => {
          if (aguardandoColar) {
            setAguardandoColar(false);
            return;
          }
          passo("Aguardando Ctrl+V", "Copie a imagem do logo e pressione Ctrl+V nesta página.");
          setAguardandoColar(true);
        }}
        aria-label={`Colar logo de ${empresa.razaoSocial} com Ctrl+V`}
        title="Colar a imagem copiada (Ctrl+V). Use se o seletor de arquivos não funcionar."
      >
        <ClipboardPaste data-icon="inline-start" />
        {aguardandoColar ? "Pressione Ctrl+V…" : "Colar"}
      </Button>
      {ultimo ? (
        <span
          className={`max-w-56 truncate text-xs ${
            ultimo.nivel === "ok"
              ? "text-emerald-600 dark:text-emerald-400"
              : ultimo.nivel === "erro"
                ? "text-destructive"
                : "text-amber-600 dark:text-amber-400"
          }`}
          title={ultimo.texto}
        >
          {ultimo.texto}
        </span>
      ) : null}
      {diagnostico?.state === "error" && !diagnosticoAberto ? (
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
