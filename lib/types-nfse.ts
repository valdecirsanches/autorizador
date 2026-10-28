/**
 * Tipos do modulo NFS-e (espelham os DTOs de NfseEndpoints.cs). Arquivo
 * separado de lib/types.ts para nao tocar nos contratos da NF-e.
 */

import type { Ambiente } from "./types"

export type PadraoNfse = 1 | 2 | 3 // 1 = Nacional, 2 = ABRASF, 3 = Osasco

export const PADROES_NFSE: Record<PadraoNfse, string> = {
  1: "Padrão Nacional (SEFIN)",
  2: "ABRASF (webservice municipal)",
  3: "Osasco (eGoverneISS)",
}

export const OPCOES_SIMPLES: Record<number, string> = {
  1: "Não optante",
  2: "Optante — MEI",
  3: "Optante — ME/EPP",
}

export const REG_AP_TRIB_SN: Record<number, string> = {
  1: "Tributos federais e municipal pelo SN",
  2: "Tributos federais pelo SN, ISSQN fora",
  3: "Tributos federais e municipal fora do SN",
}

export const REGIMES_ESPECIAIS: Record<number, string> = {
  0: "Nenhum",
  1: "Ato cooperado",
  2: "Estimativa",
  3: "Microempresa municipal",
  4: "Notário ou registrador",
  5: "Profissional autônomo",
  6: "Sociedade de profissionais",
}

export interface NfseConfig {
  empresaId: string
  padrao: PadraoNfse
  padraoDescricao: string
  serieDps: string
  opSimpNac: number
  regApTribSN: number
  regEspTrib: number
  codigoMunicipioEmissao: string | null
  versaoAplicativo: string
  abrasfUrl: string | null
  abrasfVersao: string | null
  abrasfUsuario: string | null
  abrasfTemSenha: boolean
  osascoTemChaveAutenticacao: boolean
  ativa: boolean
  atualizadoEm: string
}

export interface SerieDps {
  serie: string
  ambiente: Ambiente
  ultimoNumero: number
  proximoNumero: number
  atualizadoEm: string
}

export interface EventoNfse {
  id: number
  tipoEvento: string
  sequencia: number
  codigoMotivo: string | null
  justificativa: string | null
  protocolo: string | null
  codigoStatus: string | null
  motivo: string | null
  homologado: boolean
  dataRegistro: string | null
  criadoEm: string
}

export interface NotaServico {
  id: string
  empresaId: string
  padrao: PadraoNfse
  ambiente: Ambiente
  serie: string
  numeroDps: number
  idDps: string | null
  chaveAcesso: string | null
  numeroNfse: string | null
  codigoVerificacao: string | null
  status: number
  statusDescricao: string
  dataEmissao: string
  dataCompetencia: string
  tomadorDocumento: string | null
  tomadorNome: string | null
  codigoTributacaoNacional: string
  descricaoServico: string
  valorServico: number
  valorDescontos: number
  aliquotaIss: number
  valorIss: number
  issRetido: boolean
  valorLiquido: number | null
  codigoRetorno: string | null
  motivoRetorno: string | null
  dataAutorizacao: string | null
  chaveIdempotencia: string | null
  tentativasEnvio: number
  ultimoErro: string | null
  criadoEm: string
  atualizadoEm: string
  eventos: EventoNfse[] | null
}

export interface PaginaNfse<T> {
  total: number
  pagina: number
  tamanho: number
  itens: T[]
}

export interface TentativaNfse {
  id: number
  empresaId: string
  notaServicoId: string | null
  ambiente: number
  serie: string | null
  numeroDps: number | null
  chaveIdempotencia: string | null
  sucesso: boolean
  fase: string
  codigoStatus: string | null
  mensagem: string
  detalhe: string | null
  chaveAcesso: string | null
  tomadorDocumento: string | null
  tomadorNome: string | null
  valorServico: number | null
  criadoEm: string
}

export interface ConsultaNfse {
  encontrada: boolean
  cancelada: boolean
  chaveAcesso: string | null
  numeroNfse: string | null
  codigo: string | null
  motivo: string | null
  xmlBase64: string | null
  local: NotaServico | null
}

export interface MotivoCancelamentoNfse {
  codigo: string
  descricao: string
}

/** Formata a chave de 50 digitos em grupos de 10 para leitura. */
export function formatChaveNfse(chave: string | null | undefined): string {
  if (!chave) return "—"
  return chave.replace(/(\d{10})(?=\d)/g, "$1 ")
}
