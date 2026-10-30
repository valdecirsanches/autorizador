using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NFe.Sefaz.Danfe;

/// <summary>
/// Gera o DANFE (Documento Auxiliar da NF-e) em PDF a partir do XML autorizado
/// (nfeProc), seguindo o layout retrato do Manual de Orientacao do Contribuinte.
/// O DANFE nao e emitido pela SEFAZ: e a representacao grafica que o emitente
/// produz a partir do XML autorizado. Usa QuestPDF (licenca Community).
/// </summary>
public static class GeradorDanfe
{
    private const float Rotulo = 5f;   // fonte dos rotulos (labels) das celulas
    private const float Valor = 7.5f;  // fonte dos valores
    // Margens: laterais maiores para caber com folga na area imprimivel do A4
    // (impressoras comuns perdem ~5mm de cada lado).
    private const float MargemLateral = 26f;
    private const float MargemVertical = 18f;
    private const float Linha = 0.6f;  // espessura das bordas do grid
    private static readonly Color Cinza = Colors.Grey.Lighten3;
    private static readonly Color CinzaRotulo = Colors.Grey.Darken1;

    static GeradorDanfe()
    {
        // Licenca Community: gratuita para faturamento anual ate US$ 1M.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Gera o PDF a partir do XML autorizado (nfeProc). O logo do emitente e
    /// opcional: quando nulo, o cabecalho imprime apenas o texto do emitente.
    /// <paramref name="marcaDagua"/> imprime um carimbo diagonal (ex.: "CANCELADA")
    /// sobre o documento; nulo/vazio gera o DANFE normal.
    /// </summary>
    public static byte[] GerarDeXml(
        string xmlAutorizado, byte[]? logo = null, string? marcaDagua = null,
        OrientacaoDanfe orientacao = OrientacaoDanfe.Retrato)
    {
        var dados = DanfeDados.Parse(xmlAutorizado);
        dados.Logo = logo;
        return Gerar(dados, marcaDagua, orientacao);
    }

    /// <summary>
    /// Gera o PDF a partir dos dados ja extraidos, em A4 retrato (padrao) ou
    /// paisagem.
    /// </summary>
    public static byte[] Gerar(
        DanfeDados d, string? marcaDagua = null,
        OrientacaoDanfe orientacao = OrientacaoDanfe.Retrato)
    {
        ArgumentNullException.ThrowIfNull(d);

        var paisagem = orientacao == OrientacaoDanfe.Paisagem;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(paisagem ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.MarginHorizontal(MargemLateral);
                page.MarginVertical(MargemVertical);
                page.DefaultTextStyle(x => x.FontSize(Valor).FontFamily(Fonts.Arial).LineHeight(1f));

                // Carimbo diagonal (ex.: nota cancelada). Desenhado por cima do
                // conteudo, com vermelho translucido para nao impedir a leitura.
                if (!string.IsNullOrWhiteSpace(marcaDagua))
                    page.Foreground().Element(e => MarcaDagua(e, marcaDagua, paisagem));

                page.Content().Element(e => Conteudo(e, d));

                // Canhoto de recebimento (data e assinatura) no pe da primeira folha.
                page.Footer().ShowOnce().Element(e => Canhoto(e, d));
            });
        }).GeneratePdf();
    }

    /// <summary>Monta a coluna com todos os blocos do DANFE.</summary>
    private static void Conteudo(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            col.Spacing(3);
            col.Item().Element(e => Cabecalho(e, d));
            col.Item().Element(e => NaturezaProtocolo(e, d));
            col.Item().Element(e => InscricoesEmitente(e, d));
            col.Item().Element(e => BlocoDestinatario(e, d));
            if (d.Fatura is not null || d.Duplicatas.Count > 0)
                col.Item().Element(e => BlocoFatura(e, d));
            col.Item().Element(e => BlocoImpostos(e, d));
            col.Item().Element(e => BlocoTransporte(e, d));
            col.Item().Element(e => TabelaProdutos(e, d));
            col.Item().Element(e => DadosAdicionais(e, d));
        });
    }

    // ---- Marca d'agua (carimbo diagonal) -----------------------------------

    // Area de conteudo = A4 (595.28 x 841.89 pt) menos as margens.
    // Usada para centralizar o carimbo com a receita translate/rotate/translate.
    // Em paisagem largura e altura se invertem.
    private const float A4Curto = 595.28f;
    private const float A4Longo = 841.89f;

    private static void MarcaDagua(IContainer container, string texto, bool paisagem)
    {
        var largura = (paisagem ? A4Longo : A4Curto) - 2 * MargemLateral;
        var altura = (paisagem ? A4Curto : A4Longo) - 2 * MargemVertical;

        // Rotaciona em torno do CENTRO da area: translada ate o centro, gira -45,
        // translada de volta, e entao centraliza o texto na area resultante.
        container
            .OffsetX(largura / 2f).OffsetY(altura / 2f)
            .Rotate(-45)
            .OffsetX(-largura / 2f).OffsetY(-altura / 2f)
            .AlignCenter().AlignMiddle()
            .Text(texto)
            .FontFamily(Fonts.Arial).FontSize(96).Bold()
            .FontColor("30C0392B"); // vermelho translucido (alfa 0x30)
    }

    // ---- Canhoto de recebimento (rodape da 1a folha) ------------------------

    private static void Canhoto(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            // Linha de corte acima do canhoto (destacado na parte de baixo da folha).
            col.Item().PaddingTop(4).PaddingBottom(1).Text("- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -")
                .FontSize(5).FontColor(Colors.Grey.Medium);
            col.Item().Border(Linha).Row(row =>
            {
                row.RelativeItem().Padding(3).Column(c =>
                {
                    c.Item().Text(t =>
                    {
                        t.Span("RECEBEMOS DE ").FontSize(5.5f);
                        t.Span(d.Emitente.Nome).FontSize(5.5f).Bold();
                        t.Span(" OS PRODUTOS / SERVICOS CONSTANTES DA NOTA FISCAL ELETRONICA INDICADA AO LADO").FontSize(5.5f);
                    });
                    c.Item().PaddingTop(6).Row(r =>
                    {
                        r.RelativeItem().BorderRight(Linha).PaddingRight(4).Column(cc =>
                        {
                            cc.Item().Text("DATA DE RECEBIMENTO").FontSize(Rotulo).FontColor(CinzaRotulo);
                            cc.Item().MinHeight(12);
                        });
                        r.RelativeItem(3).PaddingLeft(4).Column(cc =>
                        {
                            cc.Item().Text("IDENTIFICACAO E ASSINATURA DO RECEBEDOR").FontSize(Rotulo).FontColor(CinzaRotulo);
                            cc.Item().MinHeight(12);
                        });
                    });
                });
                row.ConstantItem(84).BorderLeft(Linha).AlignMiddle().AlignCenter().Column(c =>
                {
                    c.Item().AlignCenter().Text("NF-e").Bold().FontSize(13);
                    c.Item().AlignCenter().Text($"No. {DanfeDados.FormatarNumero(d.Numero)}").FontSize(6.5f);
                    c.Item().AlignCenter().Text($"SERIE {Serie(d)}").FontSize(6.5f);
                });
            });
        });
    }

    // ---- Cabecalho: emitente + DANFE + chave/barcode -----------------------

    private static void Cabecalho(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            col.Item().Border(Linha).Row(row =>
            {
                // Emitente
                row.RelativeItem(37).Padding(4).Column(c =>
                {
                    c.Item().Text("IDENTIFICACAO DO EMITENTE").FontSize(Rotulo).FontColor(CinzaRotulo);
                    // Logo do emitente (opcional). Sem logo, imprime so o texto.
                    if (d.Logo is { Length: > 0 })
                    {
                        c.Item().PaddingTop(3).Height(34).AlignCenter().AlignMiddle()
                            .Image(d.Logo).FitHeight();
                    }
                    c.Item().PaddingTop(3).AlignCenter().Text(d.Emitente.Nome).Bold().FontSize(9);
                    var end = d.Emitente.Endereco;
                    c.Item().AlignCenter().Text($"{end.Logradouro}, {end.Numero}  {end.Bairro}".Trim()).FontSize(6.5f);
                    c.Item().AlignCenter().Text($"CEP: {DanfeDados.FormatarCep(end.Cep)} - {end.Municipio} - {end.Uf}").FontSize(6.5f);
                    if (!string.IsNullOrWhiteSpace(end.Fone))
                        c.Item().AlignCenter().Text($"TEL: {end.Fone}").FontSize(6.5f);
                });

                // DANFE + tipo de operacao + numero + serie
                row.RelativeItem(26).BorderLeft(Linha).Padding(4).Column(c =>
                {
                    c.Item().AlignCenter().Text("DANFE").Bold().FontSize(12);
                    c.Item().AlignCenter().Text("DOCUMENTO AUXILIAR DA").FontSize(5.5f);
                    c.Item().AlignCenter().Text("NOTA FISCAL ELETRONICA").FontSize(5.5f);
                    c.Item().PaddingTop(4).Row(r =>
                    {
                        r.RelativeItem().AlignMiddle().Text(t =>
                        {
                            t.Line("0 - ENTRADA").FontSize(6);
                            t.Line("1 - SAIDA").FontSize(6);
                        });
                        r.ConstantItem(22).Border(Linha).AlignMiddle().AlignCenter()
                            .Text(d.TipoOperacao).Bold().FontSize(12);
                    });
                    c.Item().PaddingTop(4).AlignCenter().Text($"No. {DanfeDados.FormatarNumero(d.Numero)}").Bold().FontSize(9);
                    c.Item().AlignCenter().Text("FL. 1/1").FontSize(6.5f);
                    c.Item().AlignCenter().Text($"SERIE {Serie(d)}").Bold().FontSize(7.5f);
                });

                // Barcode + chave + consulta
                row.RelativeItem(37).BorderLeft(Linha).Padding(4).Column(c =>
                {
                    // O SVG tem proporcao fixa e escala pela largura. Sem limitar a
                    // largura, numa coluna larga (paisagem) a altura exigida passa de
                    // 36pt e o QuestPDF aborta com "conflicting size constraints".
                    var barras = Code128.ToSvg(d.Chave, height: 36, moduleWidth: 0.92);
                    c.Item().Height(37).AlignCenter().AlignMiddle()
                        .MaxWidth(LarguraSvg(barras))
                        .Svg(barras);
                    c.Item().PaddingTop(3).Text("CHAVE DE ACESSO").FontSize(Rotulo).FontColor(CinzaRotulo);
                    c.Item().AlignCenter().Text(DanfeDados.FormatarChave(d.Chave)).FontSize(7.5f).Bold();
                    c.Item().PaddingTop(4).AlignCenter().Text(
                        "Consulta de autenticidade no portal nacional da NF-e\nwww.nfe.fazenda.gov.br/portal ou no site da Sefaz Autorizadora")
                        .FontSize(5.5f);
                });
            });
        });
    }

    private static void NaturezaProtocolo(IContainer container, DanfeDados d)
    {
        container.Border(Linha).Row(row =>
        {
            row.RelativeItem(6).Element(e => Campo(e, "NATUREZA DA OPERACAO", d.NaturezaOperacao));
            row.RelativeItem(4).BorderLeft(Linha).Element(e => Campo(e,
                "PROTOCOLO DE AUTORIZACAO DE USO",
                $"{d.Protocolo}  {DanfeDados.FormatarDataHora(d.DataAutorizacao)}".Trim()));
        });
    }

    private static void InscricoesEmitente(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            // Tarja de homologacao (sem valor fiscal), quando aplicavel.
            if (d.Homologacao)
                col.Item().PaddingBottom(2).Border(Linha).Background(Colors.Grey.Lighten2)
                    .Padding(3).AlignCenter()
                    .Text("AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL").Bold().FontSize(9);

            col.Item().Border(Linha).Row(row =>
            {
                row.RelativeItem().Element(e => Campo(e, "INSCRICAO ESTADUAL", d.Emitente.Ie));
                row.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "INSCRICAO ESTADUAL DO SUBST. TRIB.", d.Emitente.Iest));
                row.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "CNPJ / CPF", DanfeDados.FormatarDocumento(d.Emitente.Cnpj)));
            });
        });
    }

    private static void BlocoDestinatario(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "DESTINATARIO / REMETENTE");
            var end = d.Destinatario.Endereco;
            col.Item().Border(Linha).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem(6).Element(e => Campo(e, "NOME / RAZAO SOCIAL", d.Destinatario.Nome));
                    r.RelativeItem(3).BorderLeft(Linha).Element(e => Campo(e, "CNPJ / CPF", DanfeDados.FormatarDocumento(d.Destinatario.Documento)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "DATA DA EMISSAO", DanfeDados.FormatarData(d.DataEmissao)));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem(6).Element(e => Campo(e, "ENDERECO", $"{end.Logradouro}, {end.Numero} {end.Complemento}".Trim()));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "BAIRRO / DISTRITO", end.Bairro));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "CEP", DanfeDados.FormatarCep(end.Cep)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "DATA SAIDA / ENTRADA", DanfeDados.FormatarData(d.DataSaidaEntrada)));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem(4).Element(e => Campo(e, "MUNICIPIO", end.Municipio));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "FONE / FAX", end.Fone));
                    r.RelativeItem(1).BorderLeft(Linha).Element(e => Campo(e, "UF", end.Uf));
                    r.RelativeItem(3).BorderLeft(Linha).Element(e => Campo(e, "INSCRICAO ESTADUAL", d.Destinatario.Ie));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "HORA DA SAIDA", DanfeDados.FormatarHora(d.DataSaidaEntrada)));
                });
            });
        });
    }

    private static void BlocoFatura(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "FATURA / DUPLICATAS");
            col.Item().Border(Linha).Column(c =>
            {
                if (d.Fatura is not null)
                    c.Item().Row(r =>
                    {
                        r.RelativeItem(3).Element(e => Campo(e, "NUMERO", d.Fatura.Numero));
                        r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR ORIGINAL", DanfeDados.FormatarMoeda(d.Fatura.ValorOriginal), direita: true));
                        r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR DESCONTO", DanfeDados.FormatarMoeda(d.Fatura.ValorDesconto), direita: true));
                        r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR LIQUIDO", DanfeDados.FormatarMoeda(d.Fatura.ValorLiquido), direita: true));
                    });

                if (d.Duplicatas.Count > 0)
                {
                    var topo = d.Fatura is not null;
                    // Distribui as duplicatas em blocos de 4 por linha, como no DANFE.
                    for (var i = 0; i < d.Duplicatas.Count; i += 4)
                    {
                        var linha = c.Item();
                        if (topo || i > 0) linha = linha.BorderTop(Linha);
                        linha.Row(r =>
                        {
                            for (var j = 0; j < 4; j++)
                            {
                                var idx = i + j;
                                var dup = idx < d.Duplicatas.Count ? d.Duplicatas[idx] : null;
                                var cell = r.RelativeItem();
                                if (j > 0) cell = cell.BorderLeft(Linha);
                                cell.Element(e => Campo(e,
                                    "NUM. / VENC. / VALOR",
                                    dup is null ? "" :
                                        $"{dup.Numero}  {DanfeDados.FormatarData(dup.Vencimento)}  {DanfeDados.FormatarMoeda(dup.Valor)}"));
                            }
                        });
                    }
                }
            });
        });
    }

    private static void BlocoImpostos(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "CALCULO DO IMPOSTO");
            col.Item().Border(Linha).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Element(e => Campo(e, "BASE DE CALCULO DO ICMS", DanfeDados.FormatarMoeda(d.Totais.BaseIcms), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR DO ICMS", DanfeDados.FormatarMoeda(d.Totais.ValorIcms), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "BASE CALC. ICMS SUBST.", DanfeDados.FormatarMoeda(d.Totais.BaseIcmsSt), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR DO ICMS SUBST.", DanfeDados.FormatarMoeda(d.Totais.ValorIcmsSt), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR APROX. DOS TRIBUTOS", DanfeDados.FormatarMoeda(d.Totais.ValorAproxTributos), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR TOTAL DOS PRODUTOS", DanfeDados.FormatarMoeda(d.Totais.ValorProdutos), direita: true));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem().Element(e => Campo(e, "VALOR DO FRETE", DanfeDados.FormatarMoeda(d.Totais.ValorFrete), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR DO SEGURO", DanfeDados.FormatarMoeda(d.Totais.ValorSeguro), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "DESCONTO", DanfeDados.FormatarMoeda(d.Totais.ValorDesconto), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "OUTRAS DESP. ACESS.", DanfeDados.FormatarMoeda(d.Totais.ValorOutras), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR DO IPI", DanfeDados.FormatarMoeda(d.Totais.ValorIpi), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "VALOR TOTAL DA NOTA", DanfeDados.FormatarMoeda(d.Totais.ValorTotal), direita: true, destaque: true));
                });
                if (d.Totais.TemIbsCbs)
                {
                    c.Item().BorderTop(Linha).Row(r =>
                    {
                        r.RelativeItem(2).Element(e => Campo(e, "BASE DE CALCULO IBS/CBS", DanfeDados.FormatarMoeda(d.Totais.BaseIbsCbs), direita: true));
                        r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR DO IBS", DanfeDados.FormatarMoeda(d.Totais.ValorIbs), direita: true));
                        r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "VALOR DA CBS", DanfeDados.FormatarMoeda(d.Totais.ValorCbs), direita: true));
                    });
                }
            });
        });
    }

    private static void BlocoTransporte(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "TRANSPORTADOR / VOLUMES TRANSPORTADOS");
            var t = d.Transporte;
            col.Item().Border(Linha).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem(5).Element(e => Campo(e, "RAZAO SOCIAL", t.TransportadorNome));
                    r.RelativeItem(3).BorderLeft(Linha).Element(e => Campo(e, "FRETE POR CONTA", DanfeDados.DescricaoModalidadeFrete(t.Modalidade)));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "CODIGO ANTT", t.Rntc));
                    r.RelativeItem(2).BorderLeft(Linha).Element(e => Campo(e, "PLACA DO VEICULO", t.Placa));
                    r.RelativeItem(1).BorderLeft(Linha).Element(e => Campo(e, "UF", t.UfVeiculo));
                    r.RelativeItem(3).BorderLeft(Linha).Element(e => Campo(e, "CNPJ / CPF", DanfeDados.FormatarDocumento(t.TransportadorDoc)));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem(6).Element(e => Campo(e, "ENDERECO", t.TransportadorEndereco));
                    r.RelativeItem(4).BorderLeft(Linha).Element(e => Campo(e, "MUNICIPIO", t.TransportadorMunicipio));
                    r.RelativeItem(1).BorderLeft(Linha).Element(e => Campo(e, "UF", t.TransportadorUf));
                    r.RelativeItem(3).BorderLeft(Linha).Element(e => Campo(e, "INSCRICAO ESTADUAL", t.TransportadorIe));
                });
                c.Item().BorderTop(Linha).Row(r =>
                {
                    r.RelativeItem().Element(e => Campo(e, "QUANTIDADE", t.QuantidadeVolumes, direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "ESPECIE", t.Especie));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "MARCA", t.Marca));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "NUMERACAO", t.Numeracao));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "PESO BRUTO", DanfeDados.FormatarQuantidade(t.PesoBruto), direita: true));
                    r.RelativeItem().BorderLeft(Linha).Element(e => Campo(e, "PESO LIQUIDO", DanfeDados.FormatarQuantidade(t.PesoLiquido), direita: true));
                });
            });
        });
    }

    private static void TabelaProdutos(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "DADOS DO PRODUTO / SERVICOS");
            col.Item().Border(Linha).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(34); // codigo
                    cols.RelativeColumn();   // descricao
                    cols.ConstantColumn(38); // ncm
                    cols.ConstantColumn(19); // cst
                    cols.ConstantColumn(22); // cfop
                    cols.ConstantColumn(20); // un
                    cols.ConstantColumn(40); // quant
                    cols.ConstantColumn(40); // vl unit
                    cols.ConstantColumn(42); // vl total
                    cols.ConstantColumn(32); // desconto
                    cols.ConstantColumn(42); // bc icms
                    cols.ConstantColumn(38); // vl icms
                    cols.ConstantColumn(26); // aliq icms
                    cols.ConstantColumn(24); // aliq ipi
                });

                table.Header(h =>
                {
                    Cab(h.Cell(), "CODIGO");
                    Cab(h.Cell(), "DESCRICAO DO PRODUTO / SERVICO", esquerda: true);
                    Cab(h.Cell(), "NCM/SH");
                    Cab(h.Cell(), "CST");
                    Cab(h.Cell(), "CFOP");
                    Cab(h.Cell(), "UND");
                    Cab(h.Cell(), "QUANT");
                    Cab(h.Cell(), "VALOR UNIT");
                    Cab(h.Cell(), "VALOR TOTAL");
                    Cab(h.Cell(), "DESC");
                    Cab(h.Cell(), "BC ICMS");
                    Cab(h.Cell(), "VL ICMS");
                    Cab(h.Cell(), "ALIQ ICMS");
                    Cab(h.Cell(), "ALIQ IPI");
                });

                foreach (var item in d.Itens)
                {
                    Cel(table.Cell(), item.Codigo);
                    Cel(table.Cell(), item.Descricao, esquerda: true);
                    Cel(table.Cell(), item.Ncm);
                    Cel(table.Cell(), item.Cst);
                    Cel(table.Cell(), item.Cfop);
                    Cel(table.Cell(), item.Unidade);
                    Cel(table.Cell(), DanfeDados.FormatarTresCasas(item.Quantidade), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarTresCasas(item.ValorUnitario), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.ValorTotal), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.ValorDesconto), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.BaseIcms), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.ValorIcms), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.AliqIcms), direita: true);
                    Cel(table.Cell(), DanfeDados.FormatarMoeda(item.AliqIpi), direita: true);
                }
            });
        });
    }

    private static void DadosAdicionais(IContainer container, DanfeDados d)
    {
        container.Column(col =>
        {
            Titulo(col, "DADOS ADICIONAIS");
            col.Item().Border(Linha).MinHeight(70).Row(row =>
            {
                row.RelativeItem(6).Padding(4).Column(c =>
                {
                    c.Item().Text("INFORMACOES COMPLEMENTARES").FontSize(Rotulo).FontColor(CinzaRotulo);
                    c.Item().PaddingTop(2).Text(d.InformacoesComplementares).FontSize(6.5f);
                });
                row.RelativeItem(4).BorderLeft(Linha).Padding(4).Column(c =>
                {
                    c.Item().Text("RESERVADO AO FISCO").FontSize(Rotulo).FontColor(CinzaRotulo);
                    c.Item().PaddingTop(2).Text(d.ReservadoFisco).FontSize(6.5f);
                });
            });
        });
    }

    // ---- helpers de layout -------------------------------------------------

    /// <summary>Titulo de secao: faixa fina em negrito, colada ao grid abaixo.</summary>
    private static void Titulo(ColumnDescriptor col, string texto)
        => col.Item().PaddingTop(1).Text(texto).Bold().FontSize(6);

    /// <summary>Celula rotulada: rotulo pequeno no topo e valor em negrito abaixo.</summary>
    private static void Campo(IContainer container, string rotulo, string valor,
        bool destaque = false, bool direita = false)
    {
        container.Padding(2).Column(c =>
        {
            c.Item().Text(rotulo).FontSize(Rotulo).FontColor(CinzaRotulo);
            var linhaValor = direita ? c.Item().AlignRight() : c.Item();
            linhaValor.Text(string.IsNullOrWhiteSpace(valor) ? " " : valor)
                .FontSize(destaque ? 9f : Valor).Bold();
        });
    }

    private static void Cab(IContainer container, string texto, bool esquerda = false)
    {
        var cell = container.Background(Cinza).Border(0.4f).Padding(2);
        if (!esquerda) cell = cell.AlignCenter();
        cell.Text(texto).FontSize(5f).Bold();
    }

    private static void Cel(IContainer container, string texto, bool esquerda = false, bool direita = false)
    {
        var cell = container.Border(0.4f).Padding(2);
        if (direita) cell = cell.AlignRight();
        else if (!esquerda) cell = cell.AlignCenter();
        cell.Text(string.IsNullOrWhiteSpace(texto) ? " " : texto).FontSize(5.8f);
    }

    /// <summary>Largura natural (atributo width) do SVG gerado por <see cref="Code128"/>.</summary>
    private static float LarguraSvg(string svg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(svg, @"<svg[^>]*?\swidth='([0-9.]+)'");
        return m.Success && float.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0
            ? w
            : 300f;
    }

    private static string Serie(DanfeDados d)
        => int.TryParse(d.Serie, out var s) ? s.ToString("000") : d.Serie;
}
