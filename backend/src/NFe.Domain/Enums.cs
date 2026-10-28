namespace NFe.Domain;

/// <summary>Ambiente de autorizacao conforme tpAmb do layout da NF-e.</summary>
public enum TipoAmbiente
{
    Producao = 1,
    Homologacao = 2
}

/// <summary>Modelo do documento fiscal.</summary>
public enum ModeloDocumento
{
    NFe = 55,
    NFCe = 65
}

/// <summary>Forma de emissao conforme tpEmis.</summary>
public enum TipoEmissao
{
    Normal = 1,
    ContingenciaFsIa = 2,
    ContingenciaScan = 3,
    ContingenciaDpec = 4,
    ContingenciaFsDa = 5,
    ContingenciaSvcAn = 6,
    ContingenciaSvcRs = 7,
    ContingenciaOffline = 9
}

/// <summary>Tipo de operacao conforme tpNF.</summary>
public enum TipoOperacao
{
    Entrada = 0,
    Saida = 1
}

/// <summary>Destino da operacao conforme idDest.</summary>
public enum DestinoOperacao
{
    Interna = 1,
    Interestadual = 2,
    Exterior = 3
}

/// <summary>Indicador de presenca do comprador conforme indPres.</summary>
public enum IndicadorPresenca
{
    NaoSeAplica = 0,
    Presencial = 1,
    Internet = 2,
    Teleatendimento = 3,
    NfceEntregaDomicilio = 4,
    PresencialForaEstabelecimento = 5,
    Outros = 9
}

/// <summary>Indicador de destinatario conforme indIEDest.</summary>
public enum IndicadorIeDestinatario
{
    ContribuinteIcms = 1,
    IsentoInscricao = 2,
    NaoContribuinte = 9
}

/// <summary>Regime tributario do emitente conforme CRT.</summary>
public enum RegimeTributario
{
    SimplesNacional = 1,
    SimplesNacionalExcessoSublimite = 2,
    RegimeNormal = 3,
    MicroempreendedorIndividual = 4
}

/// <summary>Modalidade do frete conforme modFrete.</summary>
public enum ModalidadeFrete
{
    ContratacaoPorContaRemetente = 0,
    ContratacaoPorContaDestinatario = 1,
    ContratacaoPorContaTerceiros = 2,
    TransporteProprioRemetente = 3,
    TransporteProprioDestinatario = 4,
    SemTransporte = 9
}

/// <summary>
/// Estados do ciclo de vida do documento fiscal. Modelado como maquina de
/// estados explicita para que nenhuma transicao ambigua ocorra em producao.
/// </summary>
public enum StatusDocumento
{
    /// <summary>Persistido, ainda sem XML gerado.</summary>
    Rascunho = 0,

    /// <summary>XML gerado e validado contra o XSD, ainda sem assinatura.</summary>
    Gerado = 10,

    /// <summary>XML assinado com certificado A1. Chave de acesso definitiva.</summary>
    Assinado = 20,

    /// <summary>Enviado a SEFAZ, aguardando retorno ou em processamento assincrono.</summary>
    Enviado = 30,

    /// <summary>Lote recebido pela SEFAZ, aguardando consulta de recibo.</summary>
    AguardandoRetorno = 40,

    /// <summary>Autorizado o uso. Possui protocolo e XML de distribuicao.</summary>
    Autorizado = 100,

    /// <summary>Rejeitado pela SEFAZ. O app cliente decide como corrigir e enviar nova tentativa.</summary>
    Rejeitado = 200,

    /// <summary>Denegado por irregularidade fiscal. Numeracao inutilizada, nao pode ser reenviada.</summary>
    Denegado = 300,

    /// <summary>Cancelado por evento de cancelamento homologado.</summary>
    Cancelado = 400,

    /// <summary>Numeracao inutilizada via servico de inutilizacao.</summary>
    Inutilizado = 500,

    /// <summary>Falha tecnica no envio. A aplicação cliente decide se reenvia o mesmo XML.</summary>
    ErroTransmissao = 900
}

/// <summary>Servicos web expostos pelos autorizadores.</summary>
public enum ServicoSefaz
{
    StatusServico,
    Autorizacao,
    RetAutorizacao,
    ConsultaProtocolo,
    Inutilizacao,
    ConsultaCadastro,
    RecepcaoEvento,
    DistribuicaoDfe
}
