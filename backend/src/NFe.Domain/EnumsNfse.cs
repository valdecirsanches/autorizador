namespace NFe.Domain;

/// <summary>
/// Padrao tecnico do provedor de NFS-e utilizado pela empresa. O modulo NFS-e e
/// totalmente independente do fluxo de NF-e: nenhum enum ou entidade da NF-e e
/// reutilizado aqui, exceto <see cref="TipoAmbiente"/> (mesma semantica tpAmb).
/// </summary>
public enum PadraoNfse
{
    /// <summary>Sistema Nacional NFS-e (gov.br) — API REST SEFIN/ADN com mTLS.</summary>
    Nacional = 1,

    /// <summary>Webservice SOAP no padrao ABRASF 2.x do provedor municipal.</summary>
    Abrasf = 2,

    /// <summary>Web Service SOAP RTC da Prefeitura de Osasco.</summary>
    Osasco = 3
}

/// <summary>Ciclo de vida da NFS-e. Valores espelham a semantica da NF-e para o painel.</summary>
public enum StatusNfse
{
    /// <summary>Persistida, ainda sem XML da DPS.</summary>
    Rascunho = 0,

    /// <summary>XML da DPS gerado, ainda sem assinatura.</summary>
    Gerada = 10,

    /// <summary>DPS assinada com o A1 da empresa.</summary>
    Assinada = 20,

    /// <summary>Enviada ao provedor, aguardando retorno.</summary>
    Enviada = 30,

    /// <summary>NFS-e gerada pelo provedor. Possui chave de acesso e XML NFSe.</summary>
    Autorizada = 100,

    /// <summary>Rejeitada pelo provedor (erro de negocio/validacao).</summary>
    Rejeitada = 200,

    /// <summary>Cancelada por evento homologado (e101101 no padrao nacional).</summary>
    Cancelada = 400,

    /// <summary>Falha tecnica no envio. Elegivel a retentativa idempotente.</summary>
    ErroTransmissao = 900
}

/// <summary>Opcao pelo Simples Nacional (opSimpNac) conforme layout da DPS.</summary>
public enum OpcaoSimplesNacional
{
    NaoOptante = 1,
    OptanteMei = 2,
    OptanteMeEpp = 3
}

/// <summary>Regime especial de tributacao (regEspTrib) conforme layout da DPS.</summary>
public enum RegimeEspecialTributacao
{
    Nenhum = 0,
    AtoCooperado = 1,
    Estimativa = 2,
    MicroempresaMunicipal = 3,
    NotarioRegistrador = 4,
    ProfissionalAutonomo = 5,
    SociedadeProfissionais = 6
}

/// <summary>Tributacao do ISSQN (tribISSQN) conforme layout da DPS.</summary>
public enum TributacaoIssqn
{
    OperacaoTributavel = 1,
    Imunidade = 2,
    ExportacaoServico = 3,
    NaoIncidencia = 4
}

/// <summary>Tipo de retencao do ISSQN (tpRetISSQN) conforme layout da DPS.</summary>
public enum TipoRetencaoIssqn
{
    NaoRetido = 1,
    RetidoPeloTomador = 2,
    RetidoPeloIntermediario = 3
}
