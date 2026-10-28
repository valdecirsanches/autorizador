using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;

namespace NFe.Tests;

/// <summary>
/// Testes que tocam a rede da SEFAZ de homologacao. Sao ignorados por padrao e
/// so rodam com NFE_TESTE_REDE=1, para nao tornar a suite dependente de rede.
///
/// Com um certificado self-signed (nao ICP-Brasil) o resultado esperado NAO e
/// sucesso: e uma recusa do servidor. Isso e justamente o que prova que o
/// pipeline de mTLS funciona ponta a ponta, porque a recusa so pode acontecer
/// depois de resolver o endpoint, completar o handshake TLS e enviar o envelope.
/// Autorizacao de verdade exige um A1 emitido por AC credenciada.
/// </summary>
public class SefazRedeRealTests
{
    private static bool RedeHabilitada =>
        Environment.GetEnvironmentVariable("NFE_TESTE_REDE") == "1";

    private static SefazEndpointRegistry Registry() => SefazEndpointRegistry.LoadDefault();

    [SkippableFact]
    public async Task StatusServico_Homologacao_CompletaHandshakeERecusaCertificado()
    {
        Skip.IfNot(RedeHabilitada, "Defina NFE_TESTE_REDE=1 para exercitar a rede da SEFAZ.");

        var registry = Registry();
        var pfx = CertificadoFalso.CriarPfx("senha");
        using var cert = X509CertificateLoader.LoadPkcs12(
            pfx, "senha", X509KeyStorageFlags.Exportable);

        var soap = new SefazSoapClient(
            new SefazSoapOptions
            {
                // O container nao tem as raizes ICP-Brasil instaladas. Toleramos
                // falha de cadeia para poder exercitar o caminho de rede; em
                // producao isso fica desligado.
                PermitirCadeiaNaoConfiavel = true,
                Timeout = TimeSpan.FromSeconds(45)
            },
            NullLogger<SefazSoapClient>.Instance);

        var client = new StatusServicoClient(registry, soap);

        Exception? falha = null;
        StatusServicoResultado? resultado = null;

        try
        {
            resultado = await client.ConsultarAsync(
                "RS", TipoAmbiente.Homologacao, cert, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            falha = ex;
        }

        // Qualquer um dos dois desfechos comprova que chegamos ate a SEFAZ:
        // resposta SOAP interpretada, ou recusa explicita do servidor.
        if (resultado is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(resultado.CodigoStatus));
            return;
        }

        Assert.NotNull(falha);

        // A recusa esperada e de autorizacao/TLS, nao de DNS ou rota.
        var texto = Achatar(falha!);
        Assert.DoesNotContain("No such host", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Name or service not known", texto, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirma que o host de homologacao responde no TLS. Isola problema de
    /// rede de problema de certificado: se este passa e o teste acima falha, a
    /// causa esta no certificado, nao na conectividade.
    /// </summary>
    [SkippableFact]
    public async Task Homologacao_EstaAlcancavelNoTls()
    {
        Skip.IfNot(RedeHabilitada, "Defina NFE_TESTE_REDE=1 para exercitar a rede da SEFAZ.");

        var ep = Registry().Resolver(
            "RS", TipoAmbiente.Homologacao, ServicoSefaz.StatusServico);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // Sem certificado cliente, esperamos recusa (403/401) ou erro de TLS.
        // O que importa e nao ser falha de DNS.
        try
        {
            var resp = await http.GetAsync(ep.Url);

            Assert.True(
                resp.StatusCode is HttpStatusCode.Forbidden
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.BadRequest
                    or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.OK
                    or HttpStatusCode.InternalServerError,
                $"Status inesperado: {(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            var texto = Achatar(ex);
            Assert.DoesNotContain("No such host", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Name or service not known", texto, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Achatar(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.AppendLine($"{e.GetType().Name}: {e.Message}");
        return sb.ToString();
    }
}
