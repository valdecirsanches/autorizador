using System.Globalization;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NFe.Nfse.Danfse;

/// <summary>
/// Dados ja resolvidos para imprimir o DANFSe. Vem da <see cref="NotaServico"/>
/// (campos desnormalizados) + <see cref="Empresa"/> (prestador) + o que for
/// possivel extrair do XML da NFS-e (tomador/endereco), quando disponivel.
/// </summary>
public sealed class DanfseDados
{
    public required NotaServico Nota { get; init; }
    public required Empresa Prestador { get; init; }
    public byte[]? Logo { get; init; }

    // Tomador (extraido do XML quando houver; senao usa o desnormalizado).
    public string? TomadorDocumento { get; init; }
    public string? TomadorNome { get; init; }
    public string? TomadorEndereco { get; init; }
    public string? TomadorMunicipio { get; init; }
    public string? TomadorEmail { get; init; }

    public string? MunicipioPrestacao { get; init; }
    public string? InformacoesComplementares { get; init; }

    /// <summary>
    /// Monta os dados a partir da nota e do prestador, complementando com o XML
    /// da NFS-e (padrao nacional) quando disponivel. Nunca lanca por XML ausente.
    /// </summary>
    public static DanfseDados Montar(NotaServico nota, Empresa prestador, byte[]? logo)
    {
        ArgumentNullException.ThrowIfNull(nota);
        ArgumentNullException.ThrowIfNull(prestador);

        string? doc = nota.TomadorDocumento, nome = nota.TomadorNome, mun = nota.TomadorMunicipio;
        string? end = null, email = null, munPrest = null, infoCompl = null;

        var xml = nota.XmlNfse ?? nota.XmlDpsAssinado ?? nota.XmlDps;
        if (!string.IsNullOrWhiteSpace(xml))
        {
            try
            {
                var raiz = XDocument.Parse(xml).Root;
                // Busca sem namespace para servir tanto ao nacional quanto a ABRASF.
                XElement? Achar(XElement? el, string nome) =>
                    el?.Descendants().FirstOrDefault(x => x.Name.LocalName == nome);
                string? V(XElement? el, string nome) => Achar(el, nome)?.Value?.Trim();

                var toma = Achar(raiz, "toma") ?? Achar(raiz, "Tomador");
                if (toma is not null)
                {
                    doc ??= V(toma, "CNPJ") ?? V(toma, "CPF") ?? V(toma, "Cnpj") ?? V(toma, "Cpf");
                    nome ??= V(toma, "xNome") ?? V(toma, "RazaoSocial");
                    email ??= V(toma, "email") ?? V(toma, "Email");
                    var lgr = V(toma, "xLgr") ?? V(toma, "Endereco");
                    var num = V(toma, "nro") ?? V(toma, "Numero");
                    var bai = V(toma, "xBairro") ?? V(toma, "Bairro");
                    var cep = V(toma, "CEP") ?? V(toma, "Cep");
                    if (lgr is not null)
                        end = string.Join(", ", new[] { lgr, num, bai, cep }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    mun ??= V(toma, "xMun") ?? V(toma, "cMun") ?? V(toma, "CodigoMunicipio");
                }

                munPrest = V(raiz, "xLocPrestacao") ?? V(raiz, "cLocPrestacao") ?? V(raiz, "CodigoMunicipio");
                infoCompl = V(raiz, "xInfComp") ?? V(raiz, "OutrasInformacoes");
            }
            catch
            {
                // XML fora do esperado: imprime com o que a entidade ja tem.
            }
        }

        return new DanfseDados
        {
            Nota = nota,
            Prestador = prestador,
            Logo = logo,
            TomadorDocumento = doc,
            TomadorNome = nome,
            TomadorEndereco = end,
            TomadorMunicipio = mun,
            TomadorEmail = email,
            MunicipioPrestacao = munPrest,
            InformacoesComplementares = infoCompl,
        };
    }
}

/// <summary>
/// Gera o DANFSe (Documento Auxiliar da NFS-e) em PDF. O DANFSe e a representacao
/// grafica que o prestador produz a partir da NFS-e autorizada. Mesmo motor
/// (QuestPDF, licenca Community) e mesmo estilo visual do DANFE da NF-e, mas com
/// codigo totalmente independente para nao tocar o fluxo da NF-e.
/// </summary>
public static class GeradorDanfse
{
    private const float Rotulo = 5.5f;
    private const float Valor = 8f;
    private const float Linha = 0.6f;
    private static readonly Color Cinza = Colors.Grey.Lighten3;
    private static readonly Color CinzaRotulo = Colors.Grey.Darken1;
    private static readonly CultureInfo PtBr = new("pt-BR");

    static GeradorDanfse()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Gera o PDF. <paramref name="marcaDagua"/> imprime um carimbo diagonal
    /// (ex.: "CANCELADA"); nulo gera o documento normal.
    /// </summary>
    public static byte[] Gerar(DanfseDados d, string? marcaDagua = null)
    {
        ArgumentNullException.ThrowIfNull(d);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(x => x.FontSize(Valor).FontFamily(Fonts.Arial).LineHeight(1.1f));

                if (!string.IsNullOrWhiteSpace(marcaDagua))
                    page.Foreground().Element(e => MarcaDagua(e, marcaDagua));

                page.Content().Column(col =>
                {
                    col.Spacing(4);
                    col.Item().Element(e => Cabecalho(e, d));
                    col.Item().Element(e => Identificacao(e, d));
                    col.Item().Element(e => Bloco(e, "PRESTADOR DE SERVICOS", c => Prestador(c, d)));
                    col.Item().Element(e => Bloco(e, "TOMADOR DE SERVICOS", c => Tomador(c, d)));
                    col.Item().Element(e => Bloco(e, "DISCRIMINACAO DOS SERVICOS", c => Servico(c, d)));
                    col.Item().Element(e => Bloco(e, "TRIBUTOS E VALORES", c => Valores(c, d)));
                    if (!string.IsNullOrWhiteSpace(d.InformacoesComplementares))
                        col.Item().Element(e => Bloco(e, "INFORMACOES COMPLEMENTARES",
                            c => c.Padding(4).Text(d.InformacoesComplementares!).FontSize(7)));
                    col.Item().Element(e => Rodape(e, d));
                });
            });
        }).GeneratePdf();
    }

    // ---- Marca d'agua --------------------------------------------------------

    private const float AreaLargura = 595.28f - 40f;
    private const float AreaAltura = 841.89f - 40f;

    private static void MarcaDagua(IContainer container, string texto)
    {
        container
            .OffsetX(AreaLargura / 2f).OffsetY(AreaAltura / 2f)
            .Rotate(-45)
            .OffsetX(-AreaLargura / 2f).OffsetY(-AreaAltura / 2f)
            .AlignCenter().AlignMiddle()
            .Text(texto)
            .FontFamily(Fonts.Arial).FontSize(96).Bold()
            .FontColor("30C0392B");
    }

    // ---- Blocos -------------------------------------------------------------

    private static void Cabecalho(IContainer container, DanfseDados d)
    {
        container.Border(Linha).Row(row =>
        {
            if (d.Logo is { Length: > 0 })
                row.ConstantItem(90).Padding(4).AlignMiddle().Image(d.Logo).FitArea();

            row.RelativeItem().Padding(6).Column(c =>
            {
                c.Item().Text("DANFSe").Bold().FontSize(16);
                c.Item().Text("Documento Auxiliar da Nota Fiscal de Servico Eletronica").FontSize(7);
                c.Item().PaddingTop(2).Text(d.Nota.Padrao == PadraoNfse.Nacional
                    ? "Sistema Nacional NFS-e"
                    : "Padrao ABRASF (provedor municipal)").FontSize(7).FontColor(CinzaRotulo);
            });

            row.ConstantItem(170).BorderLeft(Linha).Padding(6).Column(c =>
            {
                Campo(c, "NUMERO DA NFS-e", d.Nota.NumeroNfse ?? "-", bold: true, tam: 12);
                Campo(c, "SERIE / N. DPS", $"{d.Nota.Serie} / {d.Nota.NumeroDps}");
                Campo(c, "DATA DE EMISSAO", d.Nota.DataEmissao.ToString("dd/MM/yyyy HH:mm", PtBr));
                Campo(c, "COMPETENCIA", d.Nota.DataCompetencia.ToString("MM/yyyy", PtBr));
            });
        });
    }

    private static void Identificacao(IContainer container, DanfseDados d)
    {
        container.Border(Linha).Background(Cinza).Padding(5).Column(c =>
        {
            c.Item().Text("CHAVE DE ACESSO").FontSize(Rotulo).FontColor(CinzaRotulo);
            c.Item().Text(FormatarChave(d.Nota.ChaveAcesso)).FontFamily(Fonts.Consolas).FontSize(9).Bold();
            c.Item().PaddingTop(3).Row(r =>
            {
                r.RelativeItem().Text(t =>
                {
                    t.Span("AMBIENTE: ").FontSize(Rotulo).FontColor(CinzaRotulo);
                    t.Span(d.Nota.Ambiente == TipoAmbiente.Producao ? "PRODUCAO" : "HOMOLOGACAO - SEM VALOR FISCAL").FontSize(7).Bold();
                });
                if (!string.IsNullOrWhiteSpace(d.Nota.CodigoVerificacao))
                    r.RelativeItem().AlignRight().Text(t =>
                    {
                        t.Span("COD. VERIFICACAO: ").FontSize(Rotulo).FontColor(CinzaRotulo);
                        t.Span(d.Nota.CodigoVerificacao!).FontSize(7).Bold();
                    });
                if (d.Nota.DataAutorizacao is { } aut)
                    r.RelativeItem().AlignRight().Text(t =>
                    {
                        t.Span("AUTORIZADA EM: ").FontSize(Rotulo).FontColor(CinzaRotulo);
                        t.Span(aut.ToString("dd/MM/yyyy HH:mm:ss", PtBr)).FontSize(7).Bold();
                    });
            });
        });
    }

    private static void Bloco(IContainer container, string titulo, Action<IContainer> corpo)
    {
        container.Border(Linha).Column(c =>
        {
            c.Item().Background(Cinza).PaddingHorizontal(4).PaddingVertical(2)
                .Text(titulo).FontSize(6.5f).Bold();
            c.Item().Element(corpo);
        });
    }

    private static void Prestador(IContainer container, DanfseDados d)
    {
        var p = d.Prestador;
        container.Padding(4).Row(row =>
        {
            row.RelativeItem(3).Column(c =>
            {
                Campo(c, "RAZAO SOCIAL", p.RazaoSocial, bold: true);
                if (!string.IsNullOrWhiteSpace(p.NomeFantasia))
                    Campo(c, "NOME FANTASIA", p.NomeFantasia!);
                Campo(c, "ENDERECO", string.Join(", ",
                    new[] { p.Logradouro, p.Numero, p.Complemento, p.Bairro }
                        .Where(s => !string.IsNullOrWhiteSpace(s))));
                Campo(c, "MUNICIPIO / UF / CEP", $"{p.NomeMunicipio} / {p.Uf} / {FormatarCep(p.Cep)}");
            });
            row.RelativeItem(2).Column(c =>
            {
                Campo(c, "CNPJ", FormatarCnpjCpf(p.Cnpj));
                Campo(c, "INSCRICAO MUNICIPAL", p.InscricaoMunicipal ?? "-");
                Campo(c, "INSCRICAO ESTADUAL", p.InscricaoEstadual);
                if (!string.IsNullOrWhiteSpace(p.Telefone))
                    Campo(c, "TELEFONE", p.Telefone!);
            });
        });
    }

    private static void Tomador(IContainer container, DanfseDados d)
    {
        container.Padding(4).Row(row =>
        {
            row.RelativeItem(3).Column(c =>
            {
                Campo(c, "NOME / RAZAO SOCIAL", d.TomadorNome ?? "TOMADOR NAO IDENTIFICADO", bold: true);
                if (!string.IsNullOrWhiteSpace(d.TomadorEndereco))
                    Campo(c, "ENDERECO", d.TomadorEndereco!);
                if (!string.IsNullOrWhiteSpace(d.TomadorMunicipio))
                    Campo(c, "MUNICIPIO", d.TomadorMunicipio!);
            });
            row.RelativeItem(2).Column(c =>
            {
                Campo(c, "CNPJ / CPF", string.IsNullOrWhiteSpace(d.TomadorDocumento) ? "-" : FormatarCnpjCpf(d.TomadorDocumento!));
                if (!string.IsNullOrWhiteSpace(d.TomadorEmail))
                    Campo(c, "E-MAIL", d.TomadorEmail!);
            });
        });
    }

    private static void Servico(IContainer container, DanfseDados d)
    {
        container.Padding(4).Column(c =>
        {
            c.Item().Row(r =>
            {
                r.RelativeItem().Element(x => CampoInline(x, "COD. TRIBUTACAO NACIONAL", d.Nota.CodigoTributacaoNacional));
                if (!string.IsNullOrWhiteSpace(d.MunicipioPrestacao))
                    r.RelativeItem().Element(x => CampoInline(x, "LOCAL DA PRESTACAO", d.MunicipioPrestacao!));
            });
            c.Item().PaddingTop(4).Text("DESCRICAO").FontSize(Rotulo).FontColor(CinzaRotulo);
            c.Item().MinHeight(60).Text(d.Nota.DescricaoServico).FontSize(8);
        });
    }

    private static void Valores(IContainer container, DanfseDados d)
    {
        var n = d.Nota;
        var liquido = n.ValorLiquido ?? (n.ValorServico - n.ValorDescontos - (n.IssRetido ? n.ValorIss : 0m));

        container.Padding(4).Column(c =>
        {
            c.Item().Row(r =>
            {
                r.RelativeItem().Element(x => CampoInline(x, "VALOR DOS SERVICOS", Moeda(n.ValorServico)));
                r.RelativeItem().Element(x => CampoInline(x, "DESCONTOS", Moeda(n.ValorDescontos)));
                r.RelativeItem().Element(x => CampoInline(x, "ALIQUOTA ISS", $"{n.AliquotaIss.ToString("0.00", PtBr)} %"));
                r.RelativeItem().Element(x => CampoInline(x, "VALOR ISS", Moeda(n.ValorIss)));
                r.RelativeItem().Element(x => CampoInline(x, "ISS RETIDO", n.IssRetido ? "SIM" : "NAO"));
            });
            c.Item().PaddingTop(4).Background(Cinza).Padding(4).Row(r =>
            {
                r.RelativeItem().AlignRight().Text("VALOR LIQUIDO DA NFS-e").FontSize(7).Bold();
                r.ConstantItem(120).AlignRight().Text(Moeda(liquido)).FontSize(11).Bold();
            });
        });
    }

    private static void Rodape(IContainer container, DanfseDados d)
    {
        container.PaddingTop(4).Column(c =>
        {
            c.Item().Text(t =>
            {
                t.Span("Status: ").FontSize(6.5f).FontColor(CinzaRotulo);
                t.Span(d.Nota.Status.ToString().ToUpperInvariant()).FontSize(6.5f).Bold();
                if (!string.IsNullOrWhiteSpace(d.Nota.MotivoRetorno))
                    t.Span($"  -  {d.Nota.CodigoRetorno} {d.Nota.MotivoRetorno}").FontSize(6.5f);
            });
            c.Item().Text(d.Nota.Padrao == PadraoNfse.Nacional
                ? "Consulte a autenticidade em https://www.nfse.gov.br/consultapublica informando a chave de acesso."
                : "Consulte a autenticidade no portal da prefeitura do municipio de prestacao.")
                .FontSize(6.5f).FontColor(CinzaRotulo);
        });
    }

    // ---- Helpers ------------------------------------------------------------

    private static void Campo(ColumnDescriptor c, string rotulo, string valor, bool bold = false, float? tam = null)
    {
        c.Item().Text(rotulo).FontSize(Rotulo).FontColor(CinzaRotulo);
        var t = c.Item().PaddingBottom(2).Text(valor).FontSize(tam ?? Valor);
        if (bold) t.Bold();
    }

    private static void CampoInline(IContainer container, string rotulo, string valor)
    {
        container.Column(c =>
        {
            c.Item().Text(rotulo).FontSize(Rotulo).FontColor(CinzaRotulo);
            c.Item().Text(valor).FontSize(Valor).Bold();
        });
    }

    private static string Moeda(decimal v) => v.ToString("C2", PtBr);

    private static string FormatarChave(string? chave)
    {
        if (string.IsNullOrWhiteSpace(chave)) return "-";
        var s = chave.Trim();
        // Agrupa de 5 em 5 para leitura.
        return string.Join(" ", Enumerable.Range(0, (s.Length + 4) / 5)
            .Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));
    }

    private static string FormatarCnpjCpf(string doc)
    {
        var d = new string(doc.Where(char.IsDigit).ToArray());
        return d.Length switch
        {
            14 => $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}",
            11 => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}",
            _ => doc,
        };
    }

    private static string FormatarCep(string cep)
    {
        var d = new string(cep.Where(char.IsDigit).ToArray());
        return d.Length == 8 ? $"{d[..5]}-{d[5..]}" : cep;
    }
}
