using System.Security.Cryptography.X509Certificates;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;

namespace NFe.Nfse.Contratos;

/// <summary>
/// Dados ja resolvidos para gerar a DPS/RPS: request do app + prestador (Empresa)
/// + configuracao NFS-e + numeracao reservada. E o que os geradores de XML consomem.
/// </summary>
public sealed record DpsDados(
    NfseRequest Request,
    Empresa Prestador,
    EmpresaNfseConfig Config,
    TipoAmbiente Ambiente,
    string Serie,
    long NumeroDps,
    DateTimeOffset DataEmissao,
    DateTime DataCompetencia);

/// <summary>Resultado de uma emissao no provedor.</summary>
public sealed record ResultadoEmissaoNfse(
    bool Sucesso,
    string? ChaveAcesso,
    string? NumeroNfse,
    string? CodigoVerificacao,
    string? XmlNfse,
    string? Codigo,
    string? Motivo,
    string RetornoBruto,
    DateTime? DataAutorizacao,
    long DuracaoMs);

/// <summary>Resultado de uma consulta de NFS-e no provedor.</summary>
public sealed record ResultadoConsultaNfse(
    bool Encontrada,
    bool Cancelada,
    string? ChaveAcesso,
    string? NumeroNfse,
    string? XmlNfse,
    string? Codigo,
    string? Motivo,
    string RetornoBruto);

/// <summary>Resultado de um evento (cancelamento) no provedor.</summary>
public sealed record ResultadoEventoNfse(
    bool Homologado,
    string? Protocolo,
    string? Codigo,
    string? Motivo,
    string? XmlEnviado,
    string RetornoBruto,
    DateTime? DataRegistro);

/// <summary>
/// Abstracao do provedor de NFS-e. O padrao nacional e a implementacao principal;
/// provedores municipais ABRASF implementam a mesma interface.
/// </summary>
public interface INfseProvedor
{
    PadraoNfse Padrao { get; }

    /// <summary>
    /// Gera o XML da DPS/RPS (ainda sem assinatura). Separado do envio para que o
    /// servico de aplicacao possa persistir/arquivar cada estagio.
    /// </summary>
    (string Xml, string Id) GerarXml(DpsDados dados);

    /// <summary>Assina o XML gerado com o A1 da empresa.</summary>
    string Assinar(string xml, string id, X509Certificate2 certificado);

    Task<ResultadoEmissaoNfse> EmitirAsync(
        string xmlAssinado,
        EmpresaNfseConfig config,
        TipoAmbiente ambiente,
        X509Certificate2 certificado,
        CancellationToken ct = default);

    Task<ResultadoConsultaNfse> ConsultarAsync(
        string chaveAcesso,
        EmpresaNfseConfig config,
        TipoAmbiente ambiente,
        X509Certificate2 certificado,
        CancellationToken ct = default);

    Task<ResultadoEventoNfse> CancelarAsync(
        NotaServico nota,
        string codigoMotivo,
        string justificativa,
        int sequencia,
        EmpresaNfseConfig config,
        X509Certificate2 certificado,
        CancellationToken ct = default);
}

/// <summary>Falha de transporte ou protocolo ao falar com o provedor de NFS-e.</summary>
public sealed class NfseProvedorException : Exception
{
    public int? StatusHttp { get; }
    public string? CorpoRecebido { get; }

    public NfseProvedorException(
        string message, int? statusHttp = null, string? corpoRecebido = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusHttp = statusHttp;
        CorpoRecebido = corpoRecebido;
    }
}
