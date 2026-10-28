namespace NFe.Domain.Modelos;

/// <summary>
/// Dados de entrada para emissao de uma NF-e. Modelo de aplicacao, independente
/// do XML: o gerador o traduz para o layout 4.00. Configuracao e emitente sao
/// informados pelo app em cada emissao.
/// </summary>
public sealed class NotaFiscalRequest
{
    public ConfiguracaoNFeRequest? Configuracao { get; set; }
    public EmitenteNFeRequest? Emitente { get; set; }

    public int Serie { get; set; }
    public long Numero { get; set; }

    /// <summary>Codigo numerico cNF (8 digitos). Se vazio, o gerador sorteia.</summary>
    public string? CodigoNumerico { get; set; }

    public ModeloDocumento Modelo { get; set; } = ModeloDocumento.NFe;
    public TipoOperacao TipoOperacao { get; set; } = TipoOperacao.Saida;
    public TipoEmissao TipoEmissao { get; set; } = TipoEmissao.Normal;
    public DestinoOperacao DestinoOperacao { get; set; } = DestinoOperacao.Interna;
    public IndicadorPresenca IndicadorPresenca { get; set; } = IndicadorPresenca.NaoSeAplica;

    public string NaturezaOperacao { get; set; } = string.Empty;
    public DateTimeOffset DataEmissao { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DataSaidaEntrada { get; set; }

    /// <summary>
    /// Finalidade da emissao conforme finNFe: 1=Normal, 2=Complementar, 3=Ajuste,
    /// 4=Devolucao de mercadoria. Valores 2, 3 e 4 exigem DocumentosReferenciados.
    /// </summary>
    public int Finalidade { get; set; } = 1;

    /// <summary>
    /// NF-e referenciadas (grupo NFref/refNFe), ate 500. Obrigatorio quando
    /// Finalidade for 2, 3 ou 4. Opcional na nota normal; sem itens, o grupo nao
    /// e emitido.
    /// </summary>
    public List<DocumentoReferenciadoRequest> DocumentosReferenciados { get; set; } = new();

    public bool ConsumidorFinal { get; set; } = true;

    public DestinatarioRequest Destinatario { get; set; } = new();

    public List<ItemNotaRequest> Itens { get; set; } = new();

    public ModalidadeFrete ModalidadeFrete { get; set; } = ModalidadeFrete.SemTransporte;

    /// <summary>Transportadora (grupo transporta). Opcional.</summary>
    public TransportadoraRequest? Transportadora { get; set; }

    /// <summary>Veiculo de transporte (grupo veicTransp). Opcional.</summary>
    public VeiculoTransporteRequest? Veiculo { get; set; }

    /// <summary>Quantidade de volumes transportados (qVol, inteiro de até 15 dígitos).</summary>
    public long? QuantidadeVolumes { get; set; }

    /// <summary>Espécie dos volumes transportados (esp, até 60 caracteres).</summary>
    public string? Especie { get; set; }

    /// <summary>Marca dos volumes transportados (marca, até 60 caracteres).</summary>
    public string? Marca { get; set; }

    /// <summary>Peso líquido total dos volumes em kg (pesoL, até 3 casas decimais).</summary>
    public decimal? PesoLiquido { get; set; }

    /// <summary>Peso bruto total dos volumes em kg (pesoB, até 3 casas decimais).</summary>
    public decimal? PesoBruto { get; set; }

    /// <summary>
    /// Despesas acessorias / outras despesas da nota (acrescimo que nao e frete
    /// nem seguro). Valor de cabecalho: o gerador o rateia proporcionalmente ao
    /// vProd de cada item, lancando em vOutro (nunca altera o preco unitario), com
    /// a sobra de centavos na ultima linha para a soma fechar com o total. Somado a
    /// eventuais ItemNotaRequest.ValorOutro informados por item.
    /// </summary>
    public decimal OutrasDespesas { get; set; }

    /// <summary>
    /// Valor do frete da nota (cabecalho). O layout 4.00 so tem vFrete por item e
    /// no total, entao o gerador rateia este valor proporcionalmente ao vProd de
    /// cada item (sobra de centavos na ultima linha) e o soma a eventuais
    /// ItemNotaRequest.ValorFrete. vFrete do total = soma dos itens.
    /// </summary>
    public decimal ValorFrete { get; set; }

    public List<PagamentoRequest> Pagamentos { get; set; } = new();

    public string? InformacoesComplementares { get; set; }
}

public sealed class TransportadoraRequest
{
    /// <summary>CNPJ (14 digitos). Informe CNPJ ou CPF, nunca os dois.</summary>
    public string? Cnpj { get; set; }

    /// <summary>CPF (11 digitos).</summary>
    public string? Cpf { get; set; }

    /// <summary>Razao social ou nome (xNome, 2 a 60 caracteres).</summary>
    public string? Nome { get; set; }

    /// <summary>Inscricao estadual (IE) ou "ISENTO".</summary>
    public string? InscricaoEstadual { get; set; }

    /// <summary>Endereco completo (xEnder, ate 60 caracteres).</summary>
    public string? Endereco { get; set; }

    /// <summary>Municipio (xMun, ate 60 caracteres).</summary>
    public string? Municipio { get; set; }

    /// <summary>UF da transportadora.</summary>
    public string? Uf { get; set; }
}

public sealed class VeiculoTransporteRequest
{
    /// <summary>Placa do veiculo (placa, 7 caracteres, sem hifen).</summary>
    public string? Placa { get; set; }

    /// <summary>UF do veiculo.</summary>
    public string? Uf { get; set; }

    /// <summary>Registro Nacional de Transportador de Carga (RNTC, ate 20 caracteres).</summary>
    public string? Rntc { get; set; }
}

public sealed class ConfiguracaoNFeRequest
{
    /// <summary>Ambiente da emissao: 1=producao, 2=homologacao.</summary>
    public TipoAmbiente? Ambiente { get; set; }

    /// <summary>UF alternativa do autorizador, se aplicavel ao estado do emitente.</summary>
    public string? UfAutorizadorOverride { get; set; }

    /// <summary>Usa o autorizador de contingencia (SVC) quando habilitado.</summary>
    public bool Contingencia { get; set; }

    /// <summary>
    /// Orientacao do DANFE em PDF devolvido na resposta: "retrato" (padrao) ou
    /// "paisagem". Nao altera o XML, so a impressao.
    /// </summary>
    public string? OrientacaoDanfe { get; set; }
}

public sealed class EmitenteNFeRequest
{
    public string Cnpj { get; set; } = string.Empty;
    public string RazaoSocial { get; set; } = string.Empty;
    public string? NomeFantasia { get; set; }
    public string InscricaoEstadual { get; set; } = string.Empty;
    public string? InscricaoMunicipal { get; set; }
    public string? CnaeFiscal { get; set; }
    public RegimeTributario? RegimeTributario { get; set; }

    public string Logradouro { get; set; } = string.Empty;
    public string Numero { get; set; } = string.Empty;
    public string? Complemento { get; set; }
    public string Bairro { get; set; } = string.Empty;
    public string CodigoMunicipio { get; set; } = string.Empty;
    public string NomeMunicipio { get; set; } = string.Empty;
    public string Uf { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public string CodigoPais { get; set; } = "1058";
    public string NomePais { get; set; } = "BRASIL";
    public string? Telefone { get; set; }
}

public sealed class DestinatarioRequest
{
    /// <summary>CNPJ (14) ou CPF (11), somente digitos.</summary>
    public string Documento { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;

    public IndicadorIeDestinatario IndicadorIe { get; set; } = IndicadorIeDestinatario.NaoContribuinte;
    public string? InscricaoEstadual { get; set; }
    public string? Email { get; set; }

    public string Logradouro { get; set; } = string.Empty;
    public string Numero { get; set; } = string.Empty;
    public string? Complemento { get; set; }
    public string Bairro { get; set; } = string.Empty;
    public string CodigoMunicipio { get; set; } = string.Empty;
    public string NomeMunicipio { get; set; } = string.Empty;
    public string Uf { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public string? Telefone { get; set; }

    public string CodigoPais { get; set; } = "1058";
    public string NomePais { get; set; } = "BRASIL";
}

public sealed class ItemNotaRequest
{
    /// <summary>Codigo do produto no emitente (cProd).</summary>
    public string Codigo { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;

    /// <summary>Codigo GTIN/EAN, ou "SEM GTIN".</summary>
    public string Gtin { get; set; } = "SEM GTIN";

    /// <summary>Nomenclatura Comum do Mercosul, 8 digitos.</summary>
    public string Ncm { get; set; } = string.Empty;

    /// <summary>
    /// Codigo de beneficio fiscal na UF (cBenef), opcional. 8 a 10 caracteres
    /// (ex.: "SEM CBENEF" onde a UF aceita). Sem valor, o grupo nao e gerado.
    /// </summary>
    public string? CBenef { get; set; }

    /// <summary>Codigo Fiscal de Operacoes e Prestacoes, 4 digitos.</summary>
    public string Cfop { get; set; } = string.Empty;

    public string UnidadeComercial { get; set; } = "UN";

    private decimal _quantidade;
    private decimal _valorUnitario;

    /// <summary>Quantidade comercial, normalizada para 3 casas decimais.</summary>
    public decimal Quantidade
    {
        get => _quantidade;
        set => _quantidade = Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>Preco unitario, normalizado para 3 casas decimais.</summary>
    public decimal ValorUnitario
    {
        get => _valorUnitario;
        set => _valorUnitario = Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>Valor bruto do item (vProd). Se 0, o gerador calcula qtd*unit.</summary>
    public decimal ValorBruto { get; set; }

    public decimal ValorDesconto { get; set; }
    public decimal ValorFrete { get; set; }

    /// <summary>
    /// Outras despesas acessorias especificas deste item (vOutro). Somado a
    /// eventual parcela rateada de NotaFiscalRequest.OutrasDespesas.
    /// </summary>
    public decimal ValorOutro { get; set; }

    /// <summary>Origem da mercadoria (0=Nacional).</summary>
    public int OrigemMercadoria { get; set; }

    /// <summary>
    /// Codigo de situacao tributaria do ICMS (CST) para regime normal, ou codigo
    /// de situacao da operacao no Simples (CSOSN). O gerador escolhe o grupo pelo
    /// regime da empresa.
    /// </summary>
    public string CstIcms { get; set; } = "00";

    public decimal AliquotaIcms { get; set; }
    public decimal BaseCalculoIcms { get; set; }
    public decimal ValorIcms { get; set; }

    /// <summary>
    /// Percentual de reducao da base de calculo do ICMS (pRedBC), usado no CST 20.
    /// Se omitido, e derivado de baseCalculoIcms x valorBruto do item.
    /// </summary>
    public decimal? PercentualReducaoBcIcms { get; set; }

    /// <summary>
    /// CST 60 (ICMS cobrado anteriormente por ST). Todos opcionais: vBCSTRet,
    /// pST (aliquota suportada pelo consumidor, percentual), vICMSSubstituto e
    /// vICMSSTRet. So saem no XML quando informados.
    /// </summary>
    public decimal? BaseCalculoIcmsStRetido { get; set; }
    public decimal? AliquotaSuportadaConsumidor { get; set; }
    public decimal? ValorIcmsSubstituto { get; set; }
    public decimal? ValorIcmsStRetido { get; set; }

    /// <summary>CST do PIS/COFINS (ex.: "07" operacao isenta).</summary>
    public string CstPis { get; set; } = "07";
    public string CstCofins { get; set; } = "07";

    /// <summary>
    /// Base, aliquota (percentual) e valor do PIS. Usados quando o CST gera grupo
    /// tributado ou "outras operacoes" (01, 02, 49, 50-56, 60-67, 70-75, 98, 99).
    /// Para CST nao tributado (04-09) sao ignorados.
    /// </summary>
    public decimal BaseCalculoPis { get; set; }
    public decimal AliquotaPis { get; set; }
    public decimal ValorPis { get; set; }

    /// <summary>Base, aliquota (percentual) e valor da COFINS. Mesma regra do PIS.</summary>
    public decimal BaseCalculoCofins { get; set; }
    public decimal AliquotaCofins { get; set; }
    public decimal ValorCofins { get; set; }

    /// <summary>
    /// IBS/CBS da reforma tributaria (grupo IBSCBS). Opcional: quando o app nao
    /// envia, o item sai sem o grupo, exatamente como antes.
    /// </summary>
    public IbsCbsItemRequest? IbsCbs { get; set; }
}

/// <summary>
/// Tributacao IBS/CBS de um item. Aliquotas em percentual (ex.: 0.1 = 0,1%).
/// Os valores (ValorIbsUf, ValorIbsMunicipal, ValorCbs) sao opcionais: se nao
/// informados, o gerador calcula BaseCalculo x aliquota, arredondando a 2 casas.
/// </summary>
public sealed class IbsCbsItemRequest
{
    /// <summary>CST do IBS/CBS (3 digitos, ex.: "000").</summary>
    public string Cst { get; set; } = "000";

    /// <summary>Classificacao tributaria cClassTrib (6 digitos, ex.: "000001").</summary>
    public string ClassificacaoTributaria { get; set; } = string.Empty;

    /// <summary>Base de calculo comum do IBS e da CBS (vBC). Se 0, usa vProd - vDesc do item.</summary>
    public decimal BaseCalculo { get; set; }

    public decimal AliquotaIbsUf { get; set; }
    public decimal AliquotaIbsMunicipal { get; set; }
    public decimal AliquotaCbs { get; set; }

    /// <summary>
    /// Aliquota reduzida (grupo gRed, pRedAliq/pAliqEfet). Percentuais.
    /// Reducao = percentual de reducao da aliquota (0 a 100). AliquotaEfetiva = aliquota
    /// apos a reducao. Informe um dos dois: o outro e derivado de Aliquota* e, quando
    /// nenhum e informado, o item sai sem gRed. Os valores sao calculados sobre a
    /// aliquota efetiva.
    /// </summary>
    public decimal? ReducaoAliquotaIbsUf { get; set; }
    public decimal? AliquotaEfetivaIbsUf { get; set; }
    public decimal? ReducaoAliquotaIbsMunicipal { get; set; }
    public decimal? AliquotaEfetivaIbsMunicipal { get; set; }
    public decimal? ReducaoAliquotaCbs { get; set; }
    public decimal? AliquotaEfetivaCbs { get; set; }

    public decimal? ValorIbsUf { get; set; }
    public decimal? ValorIbsMunicipal { get; set; }
    public decimal? ValorCbs { get; set; }
}

public sealed class PagamentoRequest
{
    /// <summary>Meio de pagamento conforme tPag (01=Dinheiro, 03=Cartao credito...).</summary>
    public string Meio { get; set; } = "01";
    public decimal Valor { get; set; }
}
