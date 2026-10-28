using NFe.Domain;

namespace NFe.Nfse.Nacional;

/// <summary>
/// URLs do Sistema Nacional NFS-e por ambiente. SEFIN = emissao/consulta/eventos
/// do contribuinte; ADN = ambiente de dados nacional (DANFSe oficial).
/// Podem ser sobrescritas por variavel de ambiente para acompanhar mudancas do
/// governo sem recompilar.
/// </summary>
public static class NfseNacionalEndpoints
{
    public const string NamespaceDps = "http://www.sped.fazenda.gov.br/nfse";
    public const string VersaoLayout = "1.00";

    public static string SefinBase(TipoAmbiente ambiente) => ambiente switch
    {
        TipoAmbiente.Producao =>
            Environment.GetEnvironmentVariable("NFSE_SEFIN_URL_PRODUCAO")
            ?? "https://sefin.nfse.gov.br/SefinNacional",
        _ =>
            Environment.GetEnvironmentVariable("NFSE_SEFIN_URL_HOMOLOGACAO")
            ?? "https://sefin.producaorestrita.nfse.gov.br/API/SefinNacional"
    };

    public static string AdnBase(TipoAmbiente ambiente) => ambiente switch
    {
        TipoAmbiente.Producao =>
            Environment.GetEnvironmentVariable("NFSE_ADN_URL_PRODUCAO")
            ?? "https://adn.nfse.gov.br/contribuinte",
        _ =>
            Environment.GetEnvironmentVariable("NFSE_ADN_URL_HOMOLOGACAO")
            ?? "https://adn.producaorestrita.nfse.gov.br/contribuinte"
    };

    public static string UrlEmitir(TipoAmbiente amb) => $"{SefinBase(amb).TrimEnd('/')}/nfse";

    public static string UrlConsultar(TipoAmbiente amb, string chave) =>
        $"{SefinBase(amb).TrimEnd('/')}/nfse/{chave}";

    public static string UrlEventos(TipoAmbiente amb, string chave) =>
        $"{SefinBase(amb).TrimEnd('/')}/nfse/{chave}/eventos";

    public static string UrlDanfse(TipoAmbiente amb, string chave) =>
        $"{AdnBase(amb).TrimEnd('/')}/danfse/{chave}";

    public static string NomeAmbiente(TipoAmbiente amb) =>
        amb == TipoAmbiente.Producao ? "Producao" : "Producao restrita (homologacao)";
}
