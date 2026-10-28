/** Espelha os DTOs de contrato do NFe.Api. */

/** Codigo do regime tributario (CRT): 1 Simples, 2 Simples-excesso, 3 Normal, 4 MEI. */
export type RegimeTributario = 1 | 2 | 3 | 4

export interface Empresa {
  id: string
  cnpj: string
  razaoSocial: string
  nomeFantasia: string | null
  inscricaoEstadual: string
  uf: string
  nomeMunicipio: string
  regimeTributario: number
  regimeTributarioDescricao: string
  ambientePadrao: number
  ambientePadraoDescricao: string
  ufAutorizadorOverride: string | null
  ativa: boolean
  temCertificadoAtivo: boolean
  certificadoValidoAte: string | null
  temLogo: boolean
  pastaXmlBackup?: string | null
}

export interface DiagnosticoPastaXml {
  existe: boolean
  pastaEfetiva: string | null
  meses: number
  ultimoMes: string | null
  xmlsNoUltimoMes: number
  mensagem: string
}

export interface Certificado {
  id: string
  empresaId: string
  descricao: string
  subject: string
  emissor: string
  cnpjCertificado: string | null
  validoDe: string
  validoAte: string
  ativo: boolean
  expirado: boolean
  diasParaExpirar: number
  criadoEm: string
}

export interface StatusServico {
  id: number
  empresaId: string | null
  uf: string
  autorizador: string
  ambiente: number
  ambienteDescricao: string
  online: boolean
  codigoStatus: string | null
  motivo: string | null
  tempoRespostaMs: number
  dataHoraSefaz: string | null
  erroTransporte: string | null
  consultadoEm: string
}

export interface EventoVinculado {
  tipoEvento: string
  descricao: string
  numeroProtocolo: string | null
  dataEvento: string | null
}

export interface ConsultaProtocolo {
  chaveAcesso: string
  uf: string
  autorizador: string
  ambiente: number
  ambienteDescricao: string
  codigoStatus: string
  motivo: string
  versaoAplicacao: string | null
  numeroProtocolo: string | null
  autorizada: boolean
  cancelada: boolean
  denegada: boolean
  dataAutorizacao: string | null
  dataHoraSefaz: string | null
  tempoRespostaMs: number
  eventos: EventoVinculado[]
  aviso?: string | null
  xmlOrigem?: "api" | "pasta" | null
  xmlPendente?: "pasta_nao_configurada" | "nao_encontrado" | "erro" | null
}

export interface EventoDocumento {
  id: number
  statusAnterior: number | null
  statusNovo: number
  descricao: string
  detalhe: string | null
  ocorridoEm: string
}

/** Evento fiscal oficial da SEFAZ (cancelamento, CC-e). */
export interface EventoFiscal {
  id: number
  documentoFiscalId: string
  chaveAcesso: string
  tipoEvento: number
  tipoEventoDescricao: string
  sequenciaEvento: number
  descricao: string
  justificativa: string | null
  correcao: string | null
  protocoloEvento: string | null
  codigoStatus: string | null
  motivo: string | null
  homologado: boolean
  dataRegistro: string | null
  criadoEm: string
}

export interface DocumentoFiscal {
  id: string
  empresaId: string
  chaveAcesso: string | null
  modelo: number
  serie: number
  numero: number
  ambiente: number
  ambienteDescricao: string
  status: number
  statusDescricao: string
  autorizado: boolean
  protocolo: string | null
  codigoStatusRetorno: string | null
  motivoRetorno: string | null
  dataAutorizacao: string | null
  destinatarioDocumento: string | null
  destinatarioNome: string | null
  destinatarioUf: string | null
  valorTotal: number
  valorProdutos: number
  naturezaOperacao: string
  tentativasEnvio: number
  ultimoErro: string | null
  temRetornoSefaz: boolean
  temXml: boolean
  criadoEm: string
  atualizadoEm: string
  eventos: EventoDocumento[] | null
}

/** Contador de numeracao de uma serie (por modelo e ambiente). */
export interface SerieNumeracao {
  id: string
  modelo: number
  modeloDescricao: string
  serie: number
  ambiente: number
  ambienteDescricao: string
  ultimoNumero: number
  proximoNumero: number
  atualizadoEm: string
}

/** Registro de uma inutilizacao de faixa de numeracao. */
export interface Inutilizacao {
  id: string
  modelo: number
  modeloDescricao: string
  serie: number
  ambiente: number
  ambienteDescricao: string
  ano: number
  numeroInicial: number
  numeroFinal: number
  justificativa: string
  homologado: boolean
  protocolo: string | null
  codigoStatus: string | null
  motivo: string | null
  dataRegistro: string | null
  criadoEm: string
}

export interface TentativaEmissao {
  id: number
  documentoFiscalId: string | null
  ambiente: number
  serie: number | null
  numero: number | null
  sucesso: boolean
  fase: string
  codigoStatus: string | null
  mensagem: string
  detalhe: string | null
  chaveAcesso: string | null
  destinatarioDocumento: string | null
  destinatarioNome: string | null
  valorTotal: number | null
  criadoEm: string
}

/** 1 = Producao, 2 = Homologacao. */
export type Ambiente = 1 | 2

export const REGIMES: Record<number, string> = {
  1: 'Simples Nacional',
  2: 'Simples Nacional - excesso sublimite',
  3: 'Regime Normal',
  4: 'MEI',
}

/** Modelos de documento fiscal suportados (codigo -> rotulo). */
export const MODELOS: Record<number, string> = {
  55: 'NF-e (modelo 55)',
  65: 'NFC-e (modelo 65)',
}
