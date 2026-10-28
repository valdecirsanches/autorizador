"use client";


import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

export type LogoUploadDiagnostic = {
  state: "preparing" | "sending" | "verifying" | "success" | "error";
  requestUrl: string;
  originalFile?: { name: string; type: string; size: number };
  sentFile?: { name: string; type: string; size: number };
  conversionWarning?: string;
  error?: string;
  post?: {
    status?: number;
    statusText?: string;
    contentType?: string;
    requestId?: string;
    durationMs?: number;
    body?: string;
  };
  verification?: {
    url: string;
    status?: number;
    statusText?: string;
    contentType?: string;
    size?: number;
    confirmed: boolean;
    durationMs?: number;
    body?: string;
    error?: string;
  };
};

const stateLabels: Record<LogoUploadDiagnostic["state"], string> = {
  preparing: "Preparando imagem",
  sending: "Enviando requisição",
  verifying: "Confirmando gravação",
  success: "Gravação confirmada",
  error: "Falha no envio ou confirmação",
};

function formatBytes(bytes?: number) {
  if (bytes === undefined) return "—";
  if (bytes < 1024) return `${bytes} bytes`;
  return `${(bytes / 1024).toFixed(1)} KB`;
}

function ResponseBlock({
  title,
  status,
  statusText,
  contentType,
  requestId,
  url,
  durationMs,
  body,
  error,
  size,
}: {
  title: string;
  status?: number;
  statusText?: string;
  contentType?: string;
  requestId?: string;
  url?: string;
  durationMs?: number;
  body?: string;
  error?: string;
  size?: number;
}) {
  return (
    <section className="flex flex-col gap-2 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-medium">{title}</h3>
        {status !== undefined ? (
          <Badge
            variant={
              status >= 200 && status < 300 ? "secondary" : "destructive"
            }
          >
            HTTP {status} {statusText}
          </Badge>
        ) : null}
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        {url ? (
          <>
            <dt className="text-muted-foreground">URL</dt>
            <dd className="break-all font-mono">{url}</dd>
          </>
        ) : null}
        {contentType ? (
          <>
            <dt className="text-muted-foreground">Content-Type</dt>
            <dd className="break-all">{contentType}</dd>
          </>
        ) : null}
        {requestId ? (
          <>
            <dt className="text-muted-foreground">ID da requisição</dt>
            <dd className="break-all font-mono">{requestId}</dd>
          </>
        ) : null}
        {durationMs !== undefined ? (
          <>
            <dt className="text-muted-foreground">Duração</dt>
            <dd>{durationMs} ms</dd>
          </>
        ) : null}
        {size !== undefined ? (
          <>
            <dt className="text-muted-foreground">Bytes recebidos</dt>
            <dd>{formatBytes(size)}</dd>
          </>
        ) : null}
      </dl>
      {error ? <p className="text-sm text-destructive">{error}</p> : null}
      {body ? (
        <pre className="max-h-56 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 font-mono text-xs">
          {body}
        </pre>
      ) : status !== undefined ? (
        <p className="text-sm text-muted-foreground">
          A resposta não trouxe corpo.
        </p>
      ) : null}
    </section>
  );
}

export function LogoUploadDiagnosticDialog({
  open,
  onOpenChange,
  diagnostic,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  diagnostic: LogoUploadDiagnostic;
}) {
  const file = diagnostic.sentFile ?? diagnostic.originalFile;
  const command = [
    `fetch(${JSON.stringify(diagnostic.requestUrl)}, {`,
    '  method: "POST",',
    '  credentials: "same-origin",',
    "  body: FormData {",
    file
      ? `    arquivo: File(${JSON.stringify(file.name)}, { type: ${JSON.stringify(file.type || "application/octet-stream")}, size: ${formatBytes(file.size)} }),`
      : "    arquivo: <selecione uma imagem>",
    "  },",
    "})",
  ].join("\n");

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90svh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <div className="flex flex-wrap items-center gap-2">
            <DialogTitle>Diagnóstico do envio do logo</DialogTitle>
            <Badge
              variant={
                diagnostic.state === "error"
                  ? "destructive"
                  : diagnostic.state === "success"
                    ? "secondary"
                    : "outline"
              }
            >
              {stateLabels[diagnostic.state]}
            </Badge>
          </div>
          <DialogDescription>
            A seleção foi recebida pelo navegador. O comando enviado, a resposta do servidor e a confirmação da gravação aparecem aqui. Nenhum cookie ou token é exibido.
          </DialogDescription>
        </DialogHeader>

        <section className="flex flex-col gap-2 rounded-md border p-3">
          <h3 className="font-medium">
            Comando enviado
          </h3>
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
            <dt className="text-muted-foreground">Método</dt>
            <dd>POST</dd>
            <dt className="text-muted-foreground">URL</dt>
            <dd className="break-all font-mono">{diagnostic.requestUrl}</dd>
            <dt className="text-muted-foreground">Campo multipart</dt>
            <dd>arquivo</dd>
            {diagnostic.originalFile ? (
              <>
                <dt className="text-muted-foreground">Arquivo original</dt>
                <dd className="break-all">
                  {diagnostic.originalFile.name} ·{" "}
                  {diagnostic.originalFile.type || "tipo não informado"} ·{" "}
                  {formatBytes(diagnostic.originalFile.size)}
                </dd>
              </>
            ) : null}
            {diagnostic.sentFile ? (
              <>
                <dt className="text-muted-foreground">Arquivo enviado</dt>
                <dd className="break-all">
                  {diagnostic.sentFile.name} ·{" "}
                  {diagnostic.sentFile.type || "tipo não informado"} ·{" "}
                  {formatBytes(diagnostic.sentFile.size)}
                </dd>
              </>
            ) : null}
            <dt className="text-muted-foreground">Content-Type</dt>
            <dd>multipart/form-data; boundary gerado pelo navegador</dd>
            <dt className="text-muted-foreground">Autenticação</dt>
            <dd>Cookie same-origin (valor oculto)</dd>
          </dl>
          <pre className="max-h-40 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-3 font-mono text-xs">
            {command}
          </pre>
          {diagnostic.conversionWarning ? (
            <p className="text-sm text-muted-foreground">
              A conversão local falhou; foi enviado o arquivo original:{" "}
              {diagnostic.conversionWarning}
            </p>
          ) : null}
          {diagnostic.error ? (
            <p className="text-sm text-destructive">{diagnostic.error}</p>
          ) : null}
        </section>

        {diagnostic.post ? (
          <ResponseBlock
            title="Resposta do POST"
            status={diagnostic.post.status}
            statusText={diagnostic.post.statusText}
            contentType={diagnostic.post.contentType}
            requestId={diagnostic.post.requestId}
            durationMs={diagnostic.post.durationMs}
            body={diagnostic.post.body}
          />
        ) : null}

        {diagnostic.verification ? (
          <ResponseBlock
            title="Resposta do GET de confirmação"
            url={diagnostic.verification.url}
            status={diagnostic.verification.status}
            statusText={diagnostic.verification.statusText}
            contentType={diagnostic.verification.contentType}
            durationMs={diagnostic.verification.durationMs}
            size={diagnostic.verification.size}
            body={diagnostic.verification.body}
            error={
              diagnostic.verification.error ??
              (diagnostic.verification.confirmed
                ? undefined
                : "A leitura não confirmou uma imagem gravada.")
            }
          />
        ) : null}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Fechar
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
