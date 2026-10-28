using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;

namespace NFe.Infrastructure.ServicosNfse;

/// <summary>
/// Grava em disco a trilha de XML/JSON da NFS-e, em pasta propria por empresa:
/// <c>{NFE_XML_DIR}/{cnpj}/nfse/{ambiente}/{chave|id}-{sufixo}.xml</c>.
/// Fica em subpasta <c>nfse/</c> para nunca se misturar com os arquivos da NF-e
/// que o <c>ArquivadorXml</c> grava na mesma raiz. Best-effort: falha de disco
/// e apenas logada, o banco continua sendo a fonte de verdade.
/// </summary>
public sealed class ArquivadorXmlNfse
{
    private readonly string _baseDir;
    private readonly ILogger<ArquivadorXmlNfse>? _log;

    public ArquivadorXmlNfse(string baseDir, ILogger<ArquivadorXmlNfse>? log = null)
    {
        _baseDir = baseDir;
        _log = log;
    }

    public string BaseDir => _baseDir;

    /// <summary>Usa a mesma variavel da NF-e (NFE_XML_DIR); subpasta nfse/ separa os modulos.</summary>
    public static ArquivadorXmlNfse DoAmbiente(ILogger<ArquivadorXmlNfse>? log = null)
    {
        var dir = Environment.GetEnvironmentVariable("NFE_XML_DIR");
        return new ArquivadorXmlNfse(
            string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(Directory.GetCurrentDirectory(), "xmls")
                : dir,
            log);
    }

    public void SalvarDpsAssinada(Empresa empresa, NotaServico nota)
        => Salvar(empresa, nota, nota.XmlDpsAssinado, "dps", "xml");

    public void SalvarNfse(Empresa empresa, NotaServico nota)
        => Salvar(empresa, nota, nota.XmlNfse, "nfse", "xml");

    /// <summary>Retorno bruto do provedor (JSON no nacional, SOAP no ABRASF). Salvo em todo desfecho.</summary>
    public void SalvarRetorno(Empresa empresa, NotaServico nota)
        => Salvar(empresa, nota, nota.RetornoBruto,
            "ret", nota.Padrao == PadraoNfse.Nacional ? "json" : "xml");

    public void SalvarEvento(Empresa empresa, NotaServico nota, EventoNotaServico ev)
    {
        Salvar(empresa, nota, ev.XmlEnviado, $"evento-{ev.TipoEvento}-{ev.Sequencia:D3}", "xml");
        Salvar(empresa, nota, ev.XmlRetorno, $"evento-{ev.TipoEvento}-{ev.Sequencia:D3}-ret",
            nota.Padrao == PadraoNfse.Nacional ? "json" : "xml");
    }

    private void Salvar(Empresa empresa, NotaServico nota, string? conteudo, string sufixo, string ext)
    {
        if (string.IsNullOrWhiteSpace(conteudo)) return;
        try
        {
            var ambiente = nota.Ambiente == TipoAmbiente.Producao ? "producao" : "homologacao";
            var pasta = Path.Combine(_baseDir, SanitizarCnpj(empresa.Cnpj), "nfse", ambiente);
            Directory.CreateDirectory(pasta);

            var nomeBase = string.IsNullOrWhiteSpace(nota.ChaveAcesso)
                ? nota.Id.ToString()
                : nota.ChaveAcesso;

            File.WriteAllText(Path.Combine(pasta, $"{nomeBase}-{sufixo}.{ext}"), conteudo);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Nao foi possivel arquivar {Sufixo} da NFS-e {Id} em disco.", sufixo, nota.Id);
        }
    }

    private static string SanitizarCnpj(string cnpj)
    {
        var d = new string((cnpj ?? "").Where(char.IsDigit).ToArray());
        return d.Length == 0 ? "sem-cnpj" : d;
    }
}
