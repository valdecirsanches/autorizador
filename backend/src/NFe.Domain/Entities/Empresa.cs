namespace NFe.Domain.Entities;

/// <summary>
/// Raiz de agregacao do isolamento multiempresa. Toda operacao fiscal e todo
/// documento pertencem obrigatoriamente a uma empresa, e todas as consultas
/// devem ser filtradas por EmpresaId.
/// </summary>
public class Empresa
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>CNPJ somente digitos, 14 posicoes. Unico no sistema.</summary>
    public string Cnpj { get; set; } = string.Empty;

    public string RazaoSocial { get; set; } = string.Empty;
    public string? NomeFantasia { get; set; }

    /// <summary>Inscricao estadual somente digitos, ou ISENTO.</summary>
    public string InscricaoEstadual { get; set; } = string.Empty;
    public string? InscricaoMunicipal { get; set; }

    /// <summary>CNAE fiscal principal, 7 digitos.</summary>
    public string? CnaeFiscal { get; set; }

    public RegimeTributario RegimeTributario { get; set; } = RegimeTributario.RegimeNormal;

    // Endereco do emitente, exigido no grupo enderEmit do XML.
    public string Logradouro { get; set; } = string.Empty;
    public string Numero { get; set; } = string.Empty;
    public string? Complemento { get; set; }
    public string Bairro { get; set; } = string.Empty;

    /// <summary>Codigo IBGE do municipio, 7 digitos.</summary>
    public string CodigoMunicipio { get; set; } = string.Empty;
    public string NomeMunicipio { get; set; } = string.Empty;

    /// <summary>Sigla da UF. Determina o autorizador utilizado.</summary>
    public string Uf { get; set; } = string.Empty;

    /// <summary>CEP somente digitos, 8 posicoes.</summary>
    public string Cep { get; set; } = string.Empty;

    public string CodigoPais { get; set; } = "1058";
    public string NomePais { get; set; } = "BRASIL";
    public string? Telefone { get; set; }

    /// <summary>
    /// Ambiente padrao da empresa. Comeca em homologacao por seguranca: a
    /// promocao para producao deve ser um ato explicito do operador.
    /// </summary>
    public TipoAmbiente AmbientePadrao { get; set; } = TipoAmbiente.Homologacao;

    /// <summary>
    /// UF do autorizador quando difere da UF do emitente, por exemplo quando o
    /// estado delega para SVRS ou SVAN. Nulo significa usar a propria UF.
    /// </summary>
    public string? UfAutorizadorOverride { get; set; }

    /// <summary>
    /// Pasta (na maquina da API) com os XMLs ja emitidos, organizada em
    /// AAAAMM/NFe e AAAAMM/Evento. Usada para completar notas sem XML.
    /// </summary>
    public string? PastaXmlBackup { get; set; }

    public bool Ativa { get; set; } = true;

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<Certificado> Certificados { get; set; } = new List<Certificado>();
    public ICollection<SerieNumeracao> Series { get; set; } = new List<SerieNumeracao>();
}
