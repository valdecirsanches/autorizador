using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NFe.Domain;

namespace NFe.Nfse.Nacional;

/// <summary>
/// Monta o pedido de registro de evento (pedRegEvento) do Sistema Nacional
/// NFS-e. Implementa o evento e101101 (cancelamento de NFS-e).
/// </summary>
public sealed class GeradorXmlEventoNfse
{
    public const string Versao = "1.01";
    public const string TipoCancelamento = "101101";

    private static readonly XNamespace Ns = NfseNacionalEndpoints.NamespaceDps;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan FusoBrasilia = TimeSpan.FromHours(-3);

    /// <summary>
    /// Motivos aceitos no cMotivo do cancelamento: 1 erro na emissao, 2 servico nao
    /// prestado, 3 erro de assinatura, 4 duplicidade da nota, 5 erro de processamento.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MotivosCancelamento =
        new Dictionary<string, string>
        {
            ["1"] = "Erro na emissao",
            ["2"] = "Servico nao prestado",
            ["3"] = "Erro de assinatura",
            ["4"] = "Duplicidade da nota",
            ["5"] = "Erro de processamento"
        };

    public (string Xml, string Id) GerarCancelamento(
        string chaveAcesso,
        string cnpjAutor,
        TipoAmbiente ambiente,
        string versaoAplicativo,
        string codigoMotivo,
        string justificativa,
        int sequencia = 1)
    {
        var chave = Digitos(chaveAcesso);
        if (chave.Length != 50)
            throw new ArgumentException("Chave de acesso da NFS-e deve ter 50 digitos.", nameof(chaveAcesso));

        var cnpj = Digitos(cnpjAutor);
        if (cnpj.Length != 14)
            throw new ArgumentException("CNPJ do autor deve ter 14 digitos.", nameof(cnpjAutor));

        if (!MotivosCancelamento.ContainsKey(codigoMotivo))
            throw new ArgumentException(
                "codigoMotivo invalido. Use 1..5 (" +
                string.Join(", ", MotivosCancelamento.Select(k => $"{k.Key}={k.Value}")) + ").",
                nameof(codigoMotivo));

        var just = Regex.Replace(justificativa ?? "", @"\s+", " ").Trim();
        if (just.Length < 15 || just.Length > 255)
            throw new ArgumentException("Justificativa deve ter entre 15 e 255 caracteres.", nameof(justificativa));

        if (sequencia is < 1 or > 999)
            throw new ArgumentException("Sequencia do evento deve estar entre 1 e 999.", nameof(sequencia));

        var nPed = sequencia.ToString(Inv).PadLeft(3, '0');
        var id = "PRE" + chave + TipoCancelamento + nPed;

        var inf = new XElement(Ns + "infPedReg",
            new XAttribute("Id", id),
            new XElement(Ns + "tpAmb", ((int)ambiente).ToString(Inv)),
            new XElement(Ns + "verAplic", versaoAplicativo.Length > 20 ? versaoAplicativo[..20] : versaoAplicativo),
            new XElement(Ns + "dhEvento",
                DateTimeOffset.UtcNow.ToOffset(FusoBrasilia).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv)),
            new XElement(Ns + "CNPJAutor", cnpj),
            new XElement(Ns + "chNFSe", chave),
            new XElement(Ns + "nPedRegEvento", nPed),
            new XElement(Ns + "e101101",
                new XElement(Ns + "xDesc", "Cancelamento de NFS-e"),
                new XElement(Ns + "cMotivo", codigoMotivo),
                new XElement(Ns + "xMotivo", just)));

        var raiz = new XElement(Ns + "pedRegEvento", new XAttribute("versao", Versao), inf);

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), raiz);
        var sb = new StringBuilder();
        using (var w = new StringWriter(sb, Inv))
            doc.Save(w, SaveOptions.DisableFormatting);

        return (sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\""), id);
    }

    private static string Digitos(string? s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\D", "");
}
