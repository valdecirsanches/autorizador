using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NFe.Sefaz.Danfe;

/// <summary>
/// Dados do evento de cancelamento (evento 110111) necessarios para o
/// comprovante. Preenchido pelo chamador a partir do EventoFiscal homologado.
/// </summary>
public sealed record DadosCancelamento(
    string? ProtocoloEvento,
    DateTime? DataRegistro,
    string? CodigoStatus,
    string? Motivo,
    string? Justificativa,
    string? ProtocoloNFe);

/// <summary>
/// Gera o comprovante do EVENTO de cancelamento em PDF — diferente do DANFE.
/// E um documento de guarda que resume o cancelamento homologado: identifica a
/// NF-e, o protocolo do evento, a data/hora do registro na SEFAZ e a
/// justificativa. Os dados da nota sao extraidos do proprio XML autorizado
/// (reaproveitando <see cref="DanfeDados"/>); os do evento vem do retorno da
/// SEFAZ ja persistido. Usa QuestPDF (licenca Community).
/// </summary>
public static class GeradorComprovanteCancelamento
{
    private const float Rotulo = 6.5f;
    private const float Valor = 9f;
    private const float Linha = 0.7f;
    private static readonly Color CinzaRotulo = Colors.Grey.Darken1;

    static GeradorComprovanteCancelamento()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>Gera o comprovante a partir do XML autorizado da nota e dos dados do evento.</summary>
    public static byte[] GerarDeXml(string xmlAutorizado, DadosCancelamento canc, byte[]? logo = null)
    {
        ArgumentNullException.ThrowIfNull(canc);
        var d = DanfeDados.Parse(xmlAutorizado);
        d.Logo = logo;
        return Gerar(d, canc);
    }

    /// <summary>Gera o comprovante a partir dos dados ja extraidos.</summary>
    public static byte[] Gerar(DanfeDados d, DadosCancelamento canc)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(canc);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(Valor).FontFamily(Fonts.Arial).LineHeight(1.1f));

                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Element(e => Cabecalho(e, d));
                    col.Item().Element(e => Titulo(e, d));
                    col.Item().Element(e => BlocoNota(e, d));
                    col.Item().Element(e => BlocoEvento(e, canc));
                    col.Item().Element(e => BlocoJustificativa(e, canc));
                    col.Item().Element(Rodape);
                });
            });
        }).GeneratePdf();
    }

    // ---- Cabecalho: logo/emitente ------------------------------------------

    private static void Cabecalho(IContainer container, DanfeDados d)
    {
        container.Border(Linha).Padding(8).Row(row =>
        {
            if (d.Logo is { Length: > 0 })
                row.ConstantItem(70).Height(44).AlignMiddle().Image(d.Logo).FitArea();

            row.RelativeItem().PaddingLeft(d.Logo is { Length: > 0 } ? 8 : 0).Column(c =>
            {
                c.Item().Text(d.Emitente.Nome).Bold().FontSize(12);
                var end = d.Emitente.Endereco;
                c.Item().Text($"{end.Logradouro}, {end.Numero}  {end.Bairro}".Trim()).FontSize(7.5f);
                c.Item().Text($"{end.Municipio} - {end.Uf}  CEP {DanfeDados.FormatarCep(end.Cep)}").FontSize(7.5f);
                c.Item().Text($"CNPJ {DanfeDados.FormatarDocumento(d.Emitente.Cnpj)}   IE {d.Emitente.Ie}").FontSize(7.5f);
            });
        });
    }

    private static void Titulo(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            if (d.Homologacao)
                col.Item().PaddingBottom(4).Border(Linha).Background(Colors.Grey.Lighten2)
                    .Padding(4).AlignCenter()
                    .Text("AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL").Bold().FontSize(10);

            col.Item().Background(Colors.Grey.Lighten3).Border(Linha).Padding(8).AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Text("COMPROVANTE DE CANCELAMENTO").Bold().FontSize(14);
                c.Item().AlignCenter().Text("Nota Fiscal Eletronica - Evento 110111").FontSize(8).FontColor(CinzaRotulo);
            });
        });
    }

    // ---- Bloco: identificacao da NF-e cancelada ----------------------------

    private static void BlocoNota(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Secao(col, "NOTA FISCAL CANCELADA");
            col.Item().Border(Linha).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem(2).Element(e => Campo(e, "NUMERO", DanfeDados.FormatarNumero(d.Numero)));
                    r.RelativeItem(1).BorderLeft(Linha).Element(e => Campo(e, "SERIE", Serie(d)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "DATA DE EMISSAO", DanfeDados.FormatarDataHora(d.DataEmissao)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR TOTAL", "R$ " + DanfeDados.FormatarMoeda(d.Totais.ValorTotal), direita: true));
                });
                c.Item().BorderTop(Linha).Element(e => Campo(e, "CHAVE DE ACESSO", DanfeDados.FormatarChave(d.Chave)));
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem(3).Element(e => Campo(e, "DESTINATARIO", d.Destinatario.Nome));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "CNPJ / CPF", DanfeDados.FormatarDocumento(d.Destinatario.Documento)));
                });
            });
        });
    }

    // ---- Bloco: dados do evento de cancelamento ----------------------------

    private static void BlocoEvento(IContainer container, DadosCancelamento canc)
    {
        container.Column(col =>
        {
            Secao(col, "REGISTRO DO CANCELAMENTO NA SEFAZ");
            col.Item().Border(Linha).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem(3).Element(e => Campo(e, "PROTOCOLO DO CANCELAMENTO", Ou(canc.ProtocoloEvento)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "DATA / HORA DO REGISTRO", FormatarDataHora(canc.DataRegistro)));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem(1).Element(e => Campo(e, "STATUS", Ou(canc.CodigoStatus)));
                    r.RelativeItem(4).BorderLeft(Linha).Element(e => Campo(e, "MOTIVO", Ou(canc.Motivo)));
                });
                c.Item().BorderTop(Linha).Element(e => Campo(e, "PROTOCOLO DE AUTORIZACAO DA NF-e", Ou(canc.ProtocoloNFe)));
            });
        });
    }

    private static void BlocoJustificativa(IContainer container, DadosCancelamento canc)
    {
        container.Column(col =>
        {
            Secao(col, "JUSTIFICATIVA DO CANCELAMENTO");
            col.Item().Border(Linha).Padding(8).MinHeight(48)
                .Text(Ou(canc.Justificativa)).FontSize(9.5f);
        });
    }

    private static void Rodape(IContainer container)
    {
        container.PaddingTop(6).Column(c =>
        {
            c.Item().Text(
                "Este comprovante resume o evento de cancelamento homologado pela SEFAZ. " +
                "O documento fiscal de guarda e o XML do evento (procEventoNFe), que deve " +
                "ser armazenado junto ao XML autorizado da nota pelo prazo legal.")
                .FontSize(7.5f).FontColor(CinzaRotulo);
            c.Item().PaddingTop(4).AlignCenter().Text(
                "Consulta de autenticidade em www.nfe.fazenda.gov.br/portal ou no site da SEFAZ autorizadora")
                .FontSize(7).FontColor(CinzaRotulo);
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static void Secao(ColumnDescriptor col, string texto)
        => col.Item().Text(texto).Bold().FontSize(7.5f).FontColor(CinzaRotulo);

    private static void Campo(IContainer container, string rotulo, string valor, bool direita = false)
    {
        container.Padding(6).Column(c =>
        {
            c.Item().Text(rotulo).FontSize(Rotulo).FontColor(CinzaRotulo);
            var linha = direita ? c.Item().AlignRight() : c.Item();
            linha.Text(string.IsNullOrWhiteSpace(valor) ? " " : valor).FontSize(Valor).Bold();
        });
    }

    private static string Ou(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v.Trim();

    private static string Serie(DanfeDados d)
        => int.TryParse(d.Serie, out var s) ? s.ToString("000") : d.Serie;

    private static string FormatarDataHora(DateTime? dt)
        => dt is null ? "-" : dt.Value.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR"));
}
