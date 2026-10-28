namespace NFe.Domain.Modelos;

/// <summary>
/// Dados de entrada para emissao de uma NFS-e. Modelo de aplicacao, independente
/// do XML: o gerador o traduz para a DPS (padrao nacional) ou para o RPS (ABRASF).
/// O prestador vem da Empresa + EmpresaNfseConfig e nao e repetido aqui.
/// </summary>
public sealed class NfseRequest
{
    /// <summary>
    /// Numero da DPS definido pelo sistema emissor. Quando informado, a API
    /// respeita esse numero e valida que ainda nao foi usado na serie/ambiente.
    /// Quando omitido, a API reserva o proximo numero do contador interno.
    /// </summary>
    public long? NumeroDps { get; set; }

    /// <summary>
    /// Data/hora de emissao (dhEmi). Se omitida, usa o instante atual no fuso
    /// de Brasilia.
    /// </summary>
    public DateTimeOffset? DataEmissao { get; set; }

    /// <summary>Data de competencia (dCompet). Se omitida, usa a data de emissao.</summary>
    public DateTime? DataCompetencia { get; set; }

    public TomadorNfseRequest? Tomador { get; set; }

    public ServicoNfseRequest Servico { get; set; } = new();

    public ValoresNfseRequest Valores { get; set; } = new();

    /// <summary>Informacoes complementares livres, ate 2000 caracteres.</summary>
    public string? InformacoesComplementares { get; set; }

    /// <summary>
    /// Configuracao de emissao enviada pelo APP. OBRIGATORIA: a emissao respeita
    /// somente o que vem neste JSON (ambiente, padrao, serie, regime tributario,
    /// municipio e inscricao municipal). Nada disso e lido do cadastro do servidor
    /// e nada e gravado. Do servidor vem apenas o certificado A1 e a identificacao
    /// do prestador (CNPJ, razao social e endereco da Empresa).
    /// </summary>
    public ConfiguracaoNfseRequest? Configuracao { get; set; }
}

/// <summary>
/// Configuracao de emissao NFS-e informada pelo app em cada requisicao.
/// Campos obrigatorios: ambiente, padrao, serie, opSimpNac, regEspTrib,
/// codigoMunicipioEmissao e inscricaoMunicipal (e regApTribSN quando
/// opSimpNac = 3). Credenciais do provedor sao enviadas aqui; se omitidas,
/// o servidor usa a credencial gravada, quando houver.
/// </summary>
public sealed class ConfiguracaoNfseRequest
{
    /// <summary>Provedor NFS-e: 1=Nacional, 2=ABRASF municipal, 3=Prefeitura de Osasco.</summary>
    public PadraoNfse? Padrao { get; set; }

    /// <summary>Ambiente da emissao: 1=producao, 2=homologacao.</summary>
    public TipoAmbiente? Ambiente { get; set; }

    /// <summary>cLocEmi: codigo IBGE (7 digitos) do municipio emissor.</summary>
    public string? CodigoMunicipioEmissao { get; set; }

    /// <summary>Serie da DPS (ate 5 posicoes).</summary>
    public string? Serie { get; set; }

    /// <summary>verAplic informado na DPS.</summary>
    public string? VersaoAplicativo { get; set; }

    /// <summary>Inscricao municipal do prestador (IM).</summary>
    public string? InscricaoMunicipal { get; set; }

    /// <summary>opSimpNac: 1 = nao optante, 2 = MEI, 3 = ME/EPP.</summary>
    public OpcaoSimplesNacional? OpSimpNac { get; set; }

    /// <summary>regApTribSN (1..3), exigido quando opSimpNac = ME/EPP.</summary>
    public int? RegApTribSN { get; set; }

    /// <summary>regEspTrib: regime especial de tributacao.</summary>
    public RegimeEspecialTributacao? RegEspTrib { get; set; }

    /// <summary>Osasco: Chave de Autenticacao do portal municipal (segredo, nunca e logada).</summary>
    public string? OsascoChaveAutenticacao { get; set; }

    /// <summary>ABRASF: URL do webservice SOAP municipal.</summary>
    public string? AbrasfUrl { get; set; }

    /// <summary>ABRASF: versao do layout (ex.: "2.04").</summary>
    public string? AbrasfVersao { get; set; }

    public string? AbrasfUsuario { get; set; }

    /// <summary>ABRASF: senha do provedor (segredo, nunca e logada).</summary>
    public string? AbrasfSenha { get; set; }
}

public sealed class TomadorNfseRequest
{
    /// <summary>CNPJ (14) ou CPF (11), somente digitos. Nulo/vazio = tomador nao identificado.</summary>
    public string? Documento { get; set; }

    /// <summary>NIF do tomador estrangeiro (quando nao ha CNPJ/CPF).</summary>
    public string? Nif { get; set; }

    public string Nome { get; set; } = string.Empty;
    public string? InscricaoMunicipal { get; set; }
    public string? Email { get; set; }
    public string? Telefone { get; set; }

    // Endereco (opcional para CPF, recomendado para CNPJ).
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? Bairro { get; set; }

    /// <summary>Codigo IBGE do municipio, 7 digitos.</summary>
    public string? CodigoMunicipio { get; set; }

    /// <summary>Nome do municipio do tomador (exigido por Osasco no EnderecoDTO).</summary>
    public string? Cidade { get; set; }

    /// <summary>UF do tomador. Se omitida, e derivada do codigo IBGE.</summary>
    public string? Uf { get; set; }

    public string? Cep { get; set; }
}

public sealed class ServicoNfseRequest
{
    /// <summary>
    /// Codigo IBGE (7 digitos) do municipio onde o servico foi prestado
    /// (cLocPrestacao). Se omitido, usa o municipio do prestador.
    /// </summary>
    public string? CodigoMunicipioPrestacao { get; set; }

    /// <summary>Endereco completo do local onde o servico foi prestado (RTC Osasco).</summary>
    public string? EnderecoPrestacaoServico { get; set; }
    public string? NumeroPrestacaoServico { get; set; }
    public string? ComplementoPrestacaoServico { get; set; }
    public string? BairroPrestacaoServico { get; set; }
    public string? CidadePrestacaoServico { get; set; }
    public string? EstadoPrestacaoServico { get; set; }
    public string? CepPrestacaoServico { get; set; }
    public string? CodigoCidadeIBGEPrestacaoServico { get; set; }
    public string? PaisPrestacaoServico { get; set; }

    /// <summary>
    /// Codigo de tributacao nacional (cTribNac), 6 digitos: item (2) + subitem (2)
    /// + desdobro (2) da LC 116, por exemplo "010701".
    /// </summary>
    public string CodigoTributacaoNacional { get; set; } = string.Empty;

    /// <summary>Codigo de tributacao municipal (cTribMun), ate 3 digitos, quando exigido.</summary>
    public string? CodigoTributacaoMunicipal { get; set; }

    /// <summary>Codigo NBS (9 digitos), opcional.</summary>
    public string? CodigoNbs { get; set; }

    /// <summary>Classificacao tributaria RTC usada pelo provedor de Osasco.</summary>
    public string? ClassTrib { get; set; }

    /// <summary>CST IBS/CBS utilizado pelo provedor de Osasco.</summary>
    public string? CodigoCst { get; set; }

    /// <summary>Identificador municipal do local de incidencia RTC.</summary>
    public int? IdLocalIncidencia { get; set; }

    /// <summary>Indicador de operacao RTC aceito pelo provedor municipal.</summary>
    public string? IndOperacao { get; set; }

    /// <summary>Descricao do servico (xDescServ), ate 2000 caracteres.</summary>
    public string Descricao { get; set; } = string.Empty;

    /// <summary>
    /// ABRASF: codigo do item da lista de servicos (ex.: "1.07") e CNAE, quando
    /// o provedor municipal exigir.
    /// </summary>
    public string? ItemListaServico { get; set; }
    public string? Cnae { get; set; }
}

public sealed class ValoresNfseRequest
{
    /// <summary>Valor do servico prestado (vServ).</summary>
    public decimal ValorServico { get; set; }

    /// <summary>Desconto incondicionado (vDescIncond).</summary>
    public decimal DescontoIncondicionado { get; set; }

    /// <summary>Desconto condicionado (vDescCond).</summary>
    public decimal DescontoCondicionado { get; set; }

    /// <summary>Deducoes/reducoes da base de calculo, quando aplicavel.</summary>
    public decimal Deducoes { get; set; }

    public TributacaoIssqn TributacaoIssqn { get; set; } = TributacaoIssqn.OperacaoTributavel;
    public TipoRetencaoIssqn RetencaoIssqn { get; set; } = TipoRetencaoIssqn.NaoRetido;

    /// <summary>
    /// Aliquota do ISSQN em percentual (ex.: 2.00 = 2%). Para optantes do Simples
    /// Nacional o padrao nacional pode exigir a aliquota efetiva.
    /// </summary>
    public decimal AliquotaIss { get; set; }

    /// <summary>
    /// Valor do ISSQN. Se 0 e a operacao e tributavel, o gerador calcula
    /// (base x aliquota).
    /// </summary>
    public decimal ValorIss { get; set; }

    // Retencoes federais (opcionais).
    public decimal ValorPis { get; set; }
    public decimal ValorCofins { get; set; }
    public decimal ValorInss { get; set; }
    public decimal ValorIr { get; set; }
    public decimal ValorCsll { get; set; }

    /// <summary>
    /// Percentual aproximado do total de tributos (pTotTrib) para atender a Lei
    /// 12.741/2012. Se nulo, informa indTotTrib = 0 (nao informado).
    /// </summary>
    public decimal? PercentualTotalTributos { get; set; }
}
