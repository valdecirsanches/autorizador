using NFe.Domain;

namespace NFe.Sefaz.Soap;

/// <summary>
/// Namespaces e nomes de operacao de cada web service da NF-e 4.00.
/// O envelope SOAP precisa declarar exatamente o namespace do WSDL do servico,
/// caso contrario o autorizador responde com falha de schema.
/// </summary>
public static class NamespacesSefaz
{
    /// <summary>Namespace do schema dos documentos fiscais (portalfiscal).</summary>
    public const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";

    /// <summary>Namespace do envelope SOAP 1.2.</summary>
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>Namespace do envelope SOAP 1.1.</summary>
    public const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";

    private const string WsdlBase = "http://www.portalfiscal.inf.br/nfe/wsdl";

    /// <summary>Namespace do WSDL, usado no elemento nfeDadosMsg.</summary>
    public static string Wsdl(ServicoSefaz servico) => servico switch
    {
        ServicoSefaz.StatusServico => $"{WsdlBase}/NFeStatusServico4",
        ServicoSefaz.Autorizacao => $"{WsdlBase}/NFeAutorizacao4",
        ServicoSefaz.RetAutorizacao => $"{WsdlBase}/NFeRetAutorizacao4",
        ServicoSefaz.ConsultaProtocolo => $"{WsdlBase}/NFeConsultaProtocolo4",
        ServicoSefaz.Inutilizacao => $"{WsdlBase}/NFeInutilizacao4",
        ServicoSefaz.ConsultaCadastro => $"{WsdlBase}/CadConsultaCadastro4",
        ServicoSefaz.RecepcaoEvento => $"{WsdlBase}/NFeRecepcaoEvento4",
        ServicoSefaz.DistribuicaoDfe => $"{WsdlBase}/NFeDistribuicaoDFe",
        _ => throw new ArgumentOutOfRangeException(nameof(servico), servico, "Servico sem namespace WSDL mapeado.")
    };

    /// <summary>
    /// Nome do elemento raiz esperado na resposta, dentro de nfeResultMsg.
    /// Usado para localizar o conteudo util sem depender da posicao.
    /// </summary>
    public static string ElementoRetorno(ServicoSefaz servico) => servico switch
    {
        ServicoSefaz.StatusServico => "retConsStatServ",
        ServicoSefaz.Autorizacao => "retEnviNFe",
        ServicoSefaz.RetAutorizacao => "retConsReciNFe",
        ServicoSefaz.ConsultaProtocolo => "retConsSitNFe",
        ServicoSefaz.Inutilizacao => "retInutNFe",
        ServicoSefaz.ConsultaCadastro => "retConsCad",
        ServicoSefaz.RecepcaoEvento => "retEnvEvento",
        ServicoSefaz.DistribuicaoDfe => "retDistDFeInt",
        _ => throw new ArgumentOutOfRangeException(nameof(servico), servico, "Servico sem elemento de retorno mapeado.")
    };

    /// <summary>Versao do layout enviada no atributo versao das mensagens.</summary>
    public const string VersaoLayout = "4.00";

    /// <summary>Versao de dados aceita pelo servico de consulta de cadastro.</summary>
    public const string VersaoConsultaCadastro = "2.00";
}
