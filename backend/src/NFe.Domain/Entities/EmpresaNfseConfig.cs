namespace NFe.Domain.Entities;

/// <summary>
/// Configuracao do modulo NFS-e por empresa (1:1 com <see cref="Empresa"/>).
/// Mantida em tabela propria para nao alterar a entidade Empresa usada pela NF-e.
/// </summary>
public class EmpresaNfseConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public PadraoNfse Padrao { get; set; } = PadraoNfse.Nacional;

    /// <summary>Serie da DPS, ate 5 posicoes (layout nacional). Padrao "00001".</summary>
    public string SerieDps { get; set; } = "00001";

    public OpcaoSimplesNacional OpSimpNac { get; set; } = OpcaoSimplesNacional.OptanteMeEpp;

    /// <summary>
    /// Regime de apuracao do Simples Nacional (regApTribSN), exigido quando
    /// opSimpNac = 3 (ME/EPP): 1 = federais e municipal pelo SN; 2 = federais
    /// pelo SN e ISSQN fora (retencao/substituicao); 3 = tudo fora do SN.
    /// </summary>
    public int RegApTribSN { get; set; } = 1;

    public RegimeEspecialTributacao RegEspTrib { get; set; } = RegimeEspecialTributacao.Nenhum;

    /// <summary>
    /// Codigo IBGE (7 digitos) do municipio emissor (cLocEmi). Quando nulo usa o
    /// CodigoMunicipio cadastrado na Empresa.
    /// </summary>
    public string? CodigoMunicipioEmissao { get; set; }

    /// <summary>Versao do aplicativo informada no verAplic da DPS.</summary>
    public string VersaoAplicativo { get; set; } = "AprovadorNFSe 1.0";

    // ---- Campos exclusivos do padrao ABRASF (nulos no padrao nacional) ----

    /// <summary>URL do webservice SOAP do provedor municipal.</summary>
    public string? AbrasfUrl { get; set; }

    /// <summary>Versao do layout ABRASF (ex.: "2.04").</summary>
    public string? AbrasfVersao { get; set; }

    /// <summary>Alguns provedores exigem usuario/senha alem do certificado.</summary>
    public string? AbrasfUsuario { get; set; }
    public string? AbrasfSenha { get; set; }

    /// <summary>Chave de Autenticacao emitida pelo portal de NFS-e de Osasco.</summary>
    public string? OsascoChaveAutenticacao { get; set; }

    public bool Ativa { get; set; } = true;

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
