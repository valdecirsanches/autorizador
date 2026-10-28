import {
  type DanfeDados,
  descricaoModalidadeFrete,
  formatarChave,
  formatarCep,
  formatarData,
  formatarDataHora,
  formatarDocumento,
  formatarMoeda,
  formatarQuantidade,
} from "@/lib/danfe/parse-danfe"
import { Barcode } from "./barcode"

// DANFE modelo 55, retrato. Documento fiscal: preto sobre branco, layout fixo
// conforme o Manual de Orientacao do Contribuinte. Nao usa os tokens de tema do
// painel de proposito — precisa sair identico no papel, em qualquer tema.

function Rotulo({ children }: { children: React.ReactNode }) {
  return <div className="text-[6pt] uppercase leading-none text-black/70">{children}</div>
}

function Valor({ children, className = "" }: { children: React.ReactNode; className?: string }) {
  return <div className={`text-[8pt] leading-tight text-black ${className}`}>{children}</div>
}

// Campo com rotulo em cima e valor embaixo, dentro de uma celula com borda.
function Campo({
  rotulo,
  valor,
  className = "",
  valorClassName = "",
}: {
  rotulo: string
  valor?: React.ReactNode
  className?: string
  valorClassName?: string
}) {
  return (
    <div className={`flex flex-col gap-0.5 border border-black px-1 py-0.5 ${className}`}>
      <Rotulo>{rotulo}</Rotulo>
      <Valor className={valorClassName}>{valor || "\u00A0"}</Valor>
    </div>
  )
}

function TituloSecao({ children }: { children: React.ReactNode }) {
  return (
    <div className="mt-1 text-[7pt] font-bold uppercase leading-none text-black">{children}</div>
  )
}

export function DanfeDocumento({ dados, logoUrl }: { dados: DanfeDados; logoUrl?: string }) {
  const end = dados.emitente.endereco
  const destEnd = dados.destinatario.endereco
  const entradaSaida = dados.tipoOperacao === "0" ? "0" : "1"

  return (
    <div className="danfe mx-auto flex w-[210mm] flex-col bg-white p-[6mm] font-mono text-black">
      {/* ------------------------------------------------------------------ */}
      {/* Canhoto / recibo de entrega                                         */}
      {/* ------------------------------------------------------------------ */}
      <div className="flex border border-black">
        <div className="flex flex-1 flex-col justify-between px-1 py-0.5">
          <div className="text-[7pt] leading-tight text-black">
            RECEBEMOS DE {dados.emitente.nome} OS PRODUTOS E/OU SERVIÇOS CONSTANTES DA NOTA FISCAL
            ELETRÔNICA INDICADA AO LADO
          </div>
          <div className="flex gap-1">
            <div className="flex flex-1 flex-col border border-black px-1 py-0.5">
              <Rotulo>Data de recebimento</Rotulo>
              <Valor>{"\u00A0"}</Valor>
            </div>
            <div className="flex flex-[2] flex-col border border-black px-1 py-0.5">
              <Rotulo>Identificação e assinatura do recebedor</Rotulo>
              <Valor>{"\u00A0"}</Valor>
            </div>
          </div>
        </div>
        <div className="flex w-[42mm] flex-col items-center justify-center border-l border-black px-1 py-0.5 text-center">
          <div className="text-[9pt] font-bold leading-none text-black">NF-e</div>
          <div className="mt-1 text-[7pt] leading-tight text-black">Nº {dados.numero}</div>
          <div className="text-[7pt] leading-tight text-black">SÉRIE {dados.serie}</div>
        </div>
      </div>

      <div className="my-0.5 border-t border-dashed border-black/60" />

      {/* ------------------------------------------------------------------ */}
      {/* Cabecalho principal                                                 */}
      {/* ------------------------------------------------------------------ */}
      <div className="flex border border-black">
        {/* Emitente */}
        <div className="flex w-[70mm] flex-col justify-center border-r border-black px-2 py-1 text-center">
          {logoUrl && (
            // Logo do emitente (same-origin via proxy). Some silenciosamente se o
            // emitente nao tiver logo (404): onError esconde o <img> para nao
            // aparecer o icone de imagem quebrada no DANFE.
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={logoUrl || "/placeholder.svg"}
              alt=""
              className="mx-auto mb-1 max-h-[16mm] w-auto object-contain"
              onError={(e) => {
                ;(e.currentTarget as HTMLImageElement).style.display = "none"
              }}
            />
          )}
          <div className="text-[10pt] font-bold leading-tight text-black">
            {dados.emitente.nome}
          </div>
          {dados.emitente.fantasia && (
            <div className="text-[7pt] leading-tight text-black">{dados.emitente.fantasia}</div>
          )}
          <div className="mt-1 text-[7pt] leading-tight text-black">
            {end.logradouro}, {end.numero}
            {end.complemento ? ` - ${end.complemento}` : ""}
          </div>
          <div className="text-[7pt] leading-tight text-black">
            {end.bairro} - {end.municipio}/{end.uf}
          </div>
          <div className="text-[7pt] leading-tight text-black">
            CEP: {formatarCep(end.cep)}
            {end.fone ? ` - Fone: ${end.fone}` : ""}
          </div>
        </div>

        {/* Bloco DANFE */}
        <div className="flex w-[40mm] flex-col items-center justify-center border-r border-black px-1 py-1 text-center">
          <div className="text-[13pt] font-bold leading-none text-black">DANFE</div>
          <div className="mt-0.5 text-[6pt] leading-tight text-black">
            Documento Auxiliar da Nota Fiscal Eletrônica
          </div>
          <div className="mt-1 flex items-center gap-1 text-[7pt] leading-tight text-black">
            <span>0 - ENTRADA</span>
            <span>1 - SAÍDA</span>
          </div>
          <div className="mt-0.5 flex size-5 items-center justify-center border border-black text-[9pt] font-bold text-black">
            {entradaSaida}
          </div>
          <div className="mt-1 text-[7pt] font-bold leading-tight text-black">Nº {dados.numero}</div>
          <div className="text-[7pt] leading-tight text-black">SÉRIE {dados.serie}</div>
          <div className="text-[7pt] leading-tight text-black">FOLHA 1/1</div>
        </div>

        {/* Codigo de barras + chave + protocolo */}
        <div className="flex flex-1 flex-col">
          <div className="flex items-center justify-center border-b border-black px-2 py-1">
            {dados.chave.length === 44 ? (
              <Barcode value={dados.chave} height={45} />
            ) : (
              <span className="text-[7pt] text-black">Chave indisponível</span>
            )}
          </div>
          <div className="flex flex-col border-b border-black px-1 py-0.5">
            <Rotulo>Chave de acesso</Rotulo>
            <Valor className="text-[8pt] tracking-tight">{formatarChave(dados.chave)}</Valor>
          </div>
          <div className="flex flex-1 flex-col justify-center px-1 py-0.5 text-center">
            <div className="text-[6.5pt] leading-tight text-black">
              Consulta de autenticidade no portal nacional da NF-e
            </div>
            <div className="text-[6.5pt] leading-tight text-black">
              www.nfe.fazenda.gov.br/portal ou no site da Sefaz autorizadora
            </div>
          </div>
        </div>
      </div>

      {/* Natureza + protocolo */}
      <div className="flex">
        <Campo
          className="flex-[2]"
          rotulo="Natureza da operação"
          valor={dados.naturezaOperacao}
        />
        <Campo
          className="flex-1"
          rotulo="Protocolo de autorização de uso"
          valor={
            dados.protocolo
              ? `${dados.protocolo} - ${formatarDataHora(dados.dataAutorizacao)}`
              : ""
          }
        />
      </div>

      {/* Inscricoes */}
      <div className="flex">
        <Campo className="flex-1" rotulo="Inscrição estadual" valor={dados.emitente.ie} />
        <Campo
          className="flex-1"
          rotulo="Inscr. estadual do subst. trib."
          valor={dados.emitente.iest}
        />
        <Campo
          className="flex-1"
          rotulo="CNPJ / CPF"
          valor={formatarDocumento(dados.emitente.cnpj)}
        />
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Destinatario                                                        */}
      {/* ------------------------------------------------------------------ */}
      <TituloSecao>Destinatário / Remetente</TituloSecao>
      <div className="flex">
        <Campo className="flex-[3]" rotulo="Nome / Razão social" valor={dados.destinatario.nome} />
        <Campo
          className="flex-1"
          rotulo="CNPJ / CPF"
          valor={formatarDocumento(dados.destinatario.documento)}
        />
        <Campo className="flex-1" rotulo="Data de emissão" valor={formatarData(dados.dataEmissao)} />
      </div>
      <div className="flex">
        <Campo
          className="flex-[3]"
          rotulo="Endereço"
          valor={`${destEnd.logradouro}${destEnd.numero ? `, ${destEnd.numero}` : ""}${destEnd.complemento ? ` - ${destEnd.complemento}` : ""}`}
        />
        <Campo className="flex-1" rotulo="Bairro / Distrito" valor={destEnd.bairro} />
        <Campo className="flex-1" rotulo="CEP" valor={formatarCep(destEnd.cep)} />
      </div>
      <div className="flex">
        <Campo className="flex-[2]" rotulo="Município" valor={destEnd.municipio} />
        <Campo className="w-[16mm]" rotulo="UF" valor={destEnd.uf} />
        <Campo className="flex-1" rotulo="Inscrição estadual" valor={dados.destinatario.ie} />
        <Campo
          className="flex-1"
          rotulo="Data de saída / entrada"
          valor={formatarData(dados.dataSaidaEntrada)}
        />
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Calculo do imposto                                                  */}
      {/* ------------------------------------------------------------------ */}
      <TituloSecao>Cálculo do imposto</TituloSecao>
      <div className="flex">
        <Campo
          className="flex-1"
          rotulo="Base de cálc. do ICMS"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.baseIcms)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor do ICMS"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorIcms)}
        />
        <Campo
          className="flex-1"
          rotulo="Base de cálc. ICMS ST"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.baseIcmsSt)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor do ICMS ST"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorIcmsSt)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor total dos produtos"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorProdutos)}
        />
      </div>
      <div className="flex">
        <Campo
          className="flex-1"
          rotulo="Valor do frete"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorFrete)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor do seguro"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorSeguro)}
        />
        <Campo
          className="flex-1"
          rotulo="Desconto"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorDesconto)}
        />
        <Campo
          className="flex-1"
          rotulo="Outras despesas"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorOutras)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor do IPI"
          valorClassName="text-right"
          valor={formatarMoeda(dados.totais.valorIpi)}
        />
        <Campo
          className="flex-1"
          rotulo="Valor total da nota"
          valorClassName="text-right font-bold text-[9pt]"
          valor={formatarMoeda(dados.totais.valorTotal)}
        />
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Transportador                                                       */}
      {/* ------------------------------------------------------------------ */}
      <TituloSecao>Transportador / Volumes transportados</TituloSecao>
      <div className="flex">
        <Campo
          className="flex-[2]"
          rotulo="Nome / Razão social"
          valor={dados.transporte.transportadorNome}
        />
        <Campo
          className="flex-[2]"
          rotulo="Modalidade do frete"
          valor={descricaoModalidadeFrete(dados.transporte.modalidade)}
        />
        <Campo
          className="flex-1"
          rotulo="CNPJ / CPF"
          valor={formatarDocumento(dados.transporte.transportadorDoc)}
        />
      </div>
      <div className="flex">
        <Campo className="flex-1" rotulo="Quantidade" valor={dados.transporte.quantidadeVolumes} />
        <Campo className="flex-1" rotulo="Espécie" valor={dados.transporte.especie} />
        <Campo
          className="flex-1"
          rotulo="Peso líquido"
          valorClassName="text-right"
          valor={dados.transporte.pesoLiquido}
        />
        <Campo
          className="flex-1"
          rotulo="Peso bruto"
          valorClassName="text-right"
          valor={dados.transporte.pesoBruto}
        />
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Dados dos produtos / servicos                                       */}
      {/* ------------------------------------------------------------------ */}
      <TituloSecao>Dados do produto / serviço</TituloSecao>
      <table className="w-full border-collapse border border-black text-[6.5pt] text-black">
        <thead>
          <tr className="[&>th]:border [&>th]:border-black [&>th]:px-0.5 [&>th]:py-0.5 [&>th]:font-bold">
            <th className="text-left">Cód.</th>
            <th className="text-left">Descrição do produto / serviço</th>
            <th>NCM/SH</th>
            <th>CST</th>
            <th>CFOP</th>
            <th>Un.</th>
            <th className="text-right">Qtd.</th>
            <th className="text-right">Vlr. unit.</th>
            <th className="text-right">Vlr. total</th>
            <th className="text-right">BC ICMS</th>
            <th className="text-right">Vlr. ICMS</th>
            <th className="text-right">Alíq. ICMS</th>
          </tr>
        </thead>
        <tbody>
          {dados.itens.map((item) => (
            <tr
              key={item.numero}
              className="[&>td]:border [&>td]:border-black [&>td]:px-0.5 [&>td]:py-0.5 align-top"
            >
              <td>{item.codigo}</td>
              <td className="text-left">{item.descricao}</td>
              <td className="text-center">{item.ncm}</td>
              <td className="text-center">{item.cst}</td>
              <td className="text-center">{item.cfop}</td>
              <td className="text-center">{item.unidade}</td>
              <td className="text-right">{formatarQuantidade(item.quantidade)}</td>
              <td className="text-right">{formatarMoeda(item.valorUnitario)}</td>
              <td className="text-right">{formatarMoeda(item.valorTotal)}</td>
              <td className="text-right">{formatarMoeda(item.baseIcms)}</td>
              <td className="text-right">{formatarMoeda(item.valorIcms)}</td>
              <td className="text-right">
                {item.aliqIcms ? `${formatarMoeda(item.aliqIcms)}%` : ""}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {/* ------------------------------------------------------------------ */}
      {/* Dados adicionais                                                    */}
      {/* ------------------------------------------------------------------ */}
      <TituloSecao>Dados adicionais</TituloSecao>
      <div className="min-h-[18mm] border border-black px-1 py-0.5">
        <Rotulo>Informações complementares</Rotulo>
        <div className="whitespace-pre-wrap text-[7pt] leading-tight text-black">
          {dados.informacoesComplementares}
        </div>
      </div>

      {dados.homologacao && (
        <div className="mt-2 text-center text-[10pt] font-bold uppercase text-black">
          NF-e emitida em ambiente de homologação - sem valor fiscal
        </div>
      )}
    </div>
  )
}
