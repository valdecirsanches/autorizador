// Parser do XML autorizado (nfeProc) para os dados do DANFE.
//
// Roda 100% no navegador (DOMParser). A SEFAZ NAO fornece o DANFE — ela devolve
// apenas o protocolo de autorizacao dentro do nfeProc. O DANFE e a representacao
// grafica que o emitente gera a partir desse XML, seguindo o layout do Manual de
// Orientacao do Contribuinte. Este modulo extrai os campos necessarios.

export interface DanfeItem {
  numero: string
  codigo: string
  descricao: string
  ncm: string
  cst: string
  cfop: string
  unidade: string
  quantidade: string
  valorUnitario: string
  valorTotal: string
  baseIcms: string
  valorIcms: string
  aliqIcms: string
  valorIpi: string
  aliqIpi: string
}

export interface DanfeEndereco {
  logradouro: string
  numero: string
  complemento: string
  bairro: string
  municipio: string
  uf: string
  cep: string
  fone: string
}

export interface DanfeDados {
  chave: string
  versao: string
  ambiente: string // "1" producao, "2" homologacao
  homologacao: boolean
  // Identificacao
  naturezaOperacao: string
  numero: string
  serie: string
  modelo: string
  tipoOperacao: string // "0" entrada, "1" saida
  dataEmissao: string
  dataSaidaEntrada: string
  // Protocolo
  protocolo: string
  dataAutorizacao: string
  // Emitente
  emitente: {
    nome: string
    fantasia: string
    cnpj: string
    ie: string
    iest: string
    endereco: DanfeEndereco
  }
  // Destinatario
  destinatario: {
    nome: string
    documento: string
    ie: string
    endereco: DanfeEndereco
  }
  // Totais
  totais: {
    baseIcms: string
    valorIcms: string
    baseIcmsSt: string
    valorIcmsSt: string
    valorProdutos: string
    valorFrete: string
    valorSeguro: string
    valorDesconto: string
    valorOutras: string
    valorIpi: string
    valorTotal: string
  }
  // Transporte
  transporte: {
    modalidade: string
    transportadorNome: string
    transportadorDoc: string
    quantidadeVolumes: string
    especie: string
    pesoLiquido: string
    pesoBruto: string
  }
  itens: DanfeItem[]
  informacoesComplementares: string
}

// ---- helpers de leitura namespace-agnostica -------------------------------

function txt(el: Element | null | undefined, tag: string): string {
  if (!el) return ""
  // getElementsByTagName casa pelo nome local quando o XML usa namespace default.
  const found = el.getElementsByTagName(tag)
  return found.length > 0 ? (found[0].textContent ?? "").trim() : ""
}

function first(root: Document | Element, tag: string): Element | null {
  const found = root.getElementsByTagName(tag)
  return found.length > 0 ? found[0] : null
}

function lerEndereco(el: Element | null): DanfeEndereco {
  return {
    logradouro: txt(el, "xLgr"),
    numero: txt(el, "nro"),
    complemento: txt(el, "xCpl"),
    bairro: txt(el, "xBairro"),
    municipio: txt(el, "xMun"),
    uf: txt(el, "UF"),
    cep: txt(el, "CEP"),
    fone: txt(el, "fone"),
  }
}

export function parseDanfe(xml: string): DanfeDados {
  const doc = new DOMParser().parseFromString(xml, "application/xml")

  const parseError = doc.getElementsByTagName("parsererror")
  if (parseError.length > 0) {
    throw new Error("XML invalido ou ilegivel.")
  }

  const infNFe = first(doc, "infNFe")
  if (!infNFe) {
    throw new Error("Este XML nao contem uma NF-e (infNFe ausente).")
  }

  const ide = first(infNFe, "ide")
  const emit = first(infNFe, "emit")
  const dest = first(infNFe, "dest")
  const total = first(infNFe, "ICMSTot")
  const transp = first(infNFe, "transp")
  const infProt = first(doc, "infProt")
  const infAdic = first(infNFe, "infAdic")

  const chaveRaw = infNFe.getAttribute("Id") ?? ""
  const chave = chaveRaw.replace(/^NFe/, "")

  const ambiente = txt(ide, "tpAmb")

  // Itens
  const dets = Array.from(infNFe.getElementsByTagName("det"))
  const itens: DanfeItem[] = dets.map((det) => {
    const prod = first(det, "prod")
    const imposto = first(det, "imposto")
    // ICMS pode vir em varios grupos (ICMS00, ICMS20, ICMSSN101...). Buscamos os
    // campos por nome local independentemente do grupo.
    const icms = imposto
    const ipi = imposto
    return {
      numero: det.getAttribute("nItem") ?? "",
      codigo: txt(prod, "cProd"),
      descricao: txt(prod, "xProd"),
      ncm: txt(prod, "NCM"),
      cst: txt(icms, "CST") || txt(icms, "CSOSN"),
      cfop: txt(prod, "CFOP"),
      unidade: txt(prod, "uCom"),
      quantidade: txt(prod, "qCom"),
      valorUnitario: txt(prod, "vUnCom"),
      valorTotal: txt(prod, "vProd"),
      baseIcms: txt(icms, "vBC"),
      valorIcms: txt(icms, "vICMS"),
      aliqIcms: txt(icms, "pICMS"),
      valorIpi: txt(ipi, "vIPI"),
      aliqIpi: txt(ipi, "pIPI"),
    }
  })

  const transporta = transp ? first(transp, "transporta") : null
  const vol = transp ? first(transp, "vol") : null

  return {
    chave,
    versao: infNFe.getAttribute("versao") ?? "",
    ambiente,
    homologacao: ambiente === "2",
    naturezaOperacao: txt(ide, "natOp"),
    numero: txt(ide, "nNF"),
    serie: txt(ide, "serie"),
    modelo: txt(ide, "mod"),
    tipoOperacao: txt(ide, "tpNF"),
    dataEmissao: txt(ide, "dhEmi"),
    dataSaidaEntrada: txt(ide, "dhSaiEnt"),
    protocolo: txt(infProt, "nProt"),
    dataAutorizacao: txt(infProt, "dhRecbto"),
    emitente: {
      nome: txt(emit, "xNome"),
      fantasia: txt(emit, "xFant"),
      cnpj: txt(emit, "CNPJ") || txt(emit, "CPF"),
      ie: txt(emit, "IE"),
      iest: txt(emit, "IEST"),
      endereco: lerEndereco(emit ? first(emit, "enderEmit") : null),
    },
    destinatario: {
      nome: txt(dest, "xNome"),
      documento: txt(dest, "CNPJ") || txt(dest, "CPF") || txt(dest, "idEstrangeiro"),
      ie: txt(dest, "IE"),
      endereco: lerEndereco(dest ? first(dest, "enderDest") : null),
    },
    totais: {
      baseIcms: txt(total, "vBC"),
      valorIcms: txt(total, "vICMS"),
      baseIcmsSt: txt(total, "vBCST"),
      valorIcmsSt: txt(total, "vST"),
      valorProdutos: txt(total, "vProd"),
      valorFrete: txt(total, "vFrete"),
      valorSeguro: txt(total, "vSeg"),
      valorDesconto: txt(total, "vDesc"),
      valorOutras: txt(total, "vOutro"),
      valorIpi: txt(total, "vIPI"),
      valorTotal: txt(total, "vNF"),
    },
    transporte: {
      modalidade: txt(transp, "modFrete"),
      transportadorNome: txt(transporta, "xNome"),
      transportadorDoc: txt(transporta, "CNPJ") || txt(transporta, "CPF"),
      quantidadeVolumes: txt(vol, "qVol"),
      especie: txt(vol, "esp"),
      pesoLiquido: txt(vol, "pesoL"),
      pesoBruto: txt(vol, "pesoB"),
    },
    itens,
    informacoesComplementares: txt(infAdic, "infCpl"),
  }
}

// ---- formatadores para exibicao -------------------------------------------

export function formatarChave(chave: string): string {
  return chave.replace(/(\d{4})(?=\d)/g, "$1 ").trim()
}

export function formatarDocumento(doc: string): string {
  const d = doc.replace(/\D/g, "")
  if (d.length === 14) {
    return d.replace(/(\d{2})(\d{3})(\d{3})(\d{4})(\d{2})/, "$1.$2.$3/$4-$5")
  }
  if (d.length === 11) {
    return d.replace(/(\d{3})(\d{3})(\d{3})(\d{2})/, "$1.$2.$3-$4")
  }
  return doc
}

export function formatarCep(cep: string): string {
  const d = cep.replace(/\D/g, "")
  return d.length === 8 ? d.replace(/(\d{5})(\d{3})/, "$1-$2") : cep
}

export function formatarMoeda(valor: string): string {
  const n = Number.parseFloat(valor)
  if (Number.isNaN(n)) return valor || "0,00"
  return n.toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

export function formatarQuantidade(valor: string): string {
  const n = Number.parseFloat(valor)
  if (Number.isNaN(n)) return valor || ""
  return n.toLocaleString("pt-BR", { minimumFractionDigits: 0, maximumFractionDigits: 4 })
}

export function formatarDataHora(iso: string): string {
  if (!iso) return ""
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" })
}

export function formatarData(iso: string): string {
  if (!iso) return ""
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleDateString("pt-BR")
}

export function descricaoModalidadeFrete(mod: string): string {
  const mapa: Record<string, string> = {
    "0": "0 - Contratação por conta do Remetente (CIF)",
    "1": "1 - Contratação por conta do Destinatário (FOB)",
    "2": "2 - Contratação por conta de Terceiros",
    "3": "3 - Transporte Próprio por conta do Remetente",
    "4": "4 - Transporte Próprio por conta do Destinatário",
    "9": "9 - Sem Ocorrência de Transporte",
  }
  return mapa[mod] ?? mod
}
