using System.Globalization;
using System.Xml.Linq;

namespace NFe.Sefaz.Danfe;

/// <summary>
/// Dados extraidos do XML autorizado (nfeProc) para montar o DANFE.
///
/// A SEFAZ NAO fornece o DANFE — ela devolve apenas o protocolo de autorizacao
/// dentro do nfeProc. O DANFE e a representacao grafica que o emitente gera a
/// partir desse XML, seguindo o layout do Manual de Orientacao do Contribuinte.
/// Esta classe faz o parsing namespace-agnostico (por nome local do elemento).
/// </summary>
public sealed class DanfeDados
{
    public string Chave { get; init; } = "";
    public string Versao { get; init; } = "";
    public string Ambiente { get; init; } = ""; // "1" producao, "2" homologacao
    public bool Homologacao => Ambiente == "2";

    public string NaturezaOperacao { get; init; } = "";
    public string Numero { get; init; } = "";
    public string Serie { get; init; } = "";
    public string Modelo { get; init; } = "";
    public string TipoOperacao { get; init; } = ""; // "0" entrada, "1" saida
    public string DataEmissao { get; init; } = "";
    public string DataSaidaEntrada { get; init; } = "";

    public string Protocolo { get; init; } = "";
    public string DataAutorizacao { get; init; } = "";

    public DanfeEmitente Emitente { get; init; } = new();
    public DanfeParticipante Destinatario { get; init; } = new();
    public DanfeTotais Totais { get; init; } = new();
    public DanfeTransporte Transporte { get; init; } = new();
    public DanfeFatura? Fatura { get; init; }
    public List<DanfeDuplicata> Duplicatas { get; init; } = new();
    public List<DanfeItem> Itens { get; init; } = new();
    public string InformacoesComplementares { get; init; } = "";
    public string ReservadoFisco { get; init; } = "";

    /// <summary>
    /// Logotipo do emitente (PNG/JPEG). NAO vem do XML — e injetado pelo
    /// chamador a partir do cadastro da empresa. Nulo imprime so o texto.
    /// </summary>
    public byte[]? Logo { get; set; }

    /// <summary>Extrai os dados do DANFE de um XML nfeProc (ou NFe) autorizado.</summary>
    public static DanfeDados Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("XML vazio.", nameof(xml));

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new InvalidOperationException("XML invalido ou ilegivel.", ex); }

        var infNFe = First(doc.Root, "infNFe")
            ?? throw new InvalidOperationException("Este XML nao contem uma NF-e (infNFe ausente).");

        var ide = First(infNFe, "ide");
        var emit = First(infNFe, "emit");
        var dest = First(infNFe, "dest");
        var total = First(infNFe, "ICMSTot");
        var totalIbsCbs = First(infNFe, "IBSCBSTot");
        var transp = First(infNFe, "transp");
        var infProt = First(doc.Root, "infProt");
        var infAdic = First(infNFe, "infAdic");

        var chaveRaw = (string?)infNFe.Attribute("Id") ?? "";
        var chave = chaveRaw.StartsWith("NFe", StringComparison.OrdinalIgnoreCase)
            ? chaveRaw[3..] : chaveRaw;

        var itens = new List<DanfeItem>();
        foreach (var det in Elements(infNFe, "det"))
        {
            var prod = First(det, "prod");
            var imposto = First(det, "imposto");
            itens.Add(new DanfeItem
            {
                Numero = (string?)det.Attribute("nItem") ?? "",
                Codigo = Txt(prod, "cProd"),
                Descricao = Txt(prod, "xProd"),
                Ncm = Txt(prod, "NCM"),
                Cst = FirstNonEmpty(Txt(imposto, "CST"), Txt(imposto, "CSOSN")),
                Cfop = Txt(prod, "CFOP"),
                Unidade = Txt(prod, "uCom"),
                Quantidade = Txt(prod, "qCom"),
                ValorUnitario = Txt(prod, "vUnCom"),
                ValorTotal = Txt(prod, "vProd"),
                ValorDesconto = Txt(prod, "vDesc"),
                BaseIcms = Txt(imposto, "vBC"),
                ValorIcms = Txt(imposto, "vICMS"),
                AliqIcms = Txt(imposto, "pICMS"),
                ValorIpi = Txt(imposto, "vIPI"),
                AliqIpi = Txt(imposto, "pIPI"),
            });
        }

        var transporta = transp is null ? null : First(transp, "transporta");
        var veic = transp is null ? null : First(transp, "veicTransp");
        var vol = transp is null ? null : First(transp, "vol");

        // Cobranca (fatura + duplicatas). Presente so em NF-e com pagamento a prazo.
        var cobr = First(infNFe, "cobr");
        var fat = cobr is null ? null : First(cobr, "fat");
        DanfeFatura? fatura = fat is null ? null : new DanfeFatura
        {
            Numero = Txt(fat, "nFat"),
            ValorOriginal = Txt(fat, "vOrig"),
            ValorDesconto = Txt(fat, "vDesc"),
            ValorLiquido = Txt(fat, "vLiq"),
        };
        var duplicatas = new List<DanfeDuplicata>();
        if (cobr is not null)
            foreach (var dup in Elements(cobr, "dup"))
                duplicatas.Add(new DanfeDuplicata
                {
                    Numero = Txt(dup, "nDup"),
                    Vencimento = Txt(dup, "dVenc"),
                    Valor = Txt(dup, "vDup"),
                });

        return new DanfeDados
        {
            Chave = chave,
            Versao = (string?)infNFe.Attribute("versao") ?? "",
            Ambiente = Txt(ide, "tpAmb"),
            NaturezaOperacao = Txt(ide, "natOp"),
            Numero = Txt(ide, "nNF"),
            Serie = Txt(ide, "serie"),
            Modelo = Txt(ide, "mod"),
            TipoOperacao = Txt(ide, "tpNF"),
            DataEmissao = Txt(ide, "dhEmi"),
            DataSaidaEntrada = Txt(ide, "dhSaiEnt"),
            Protocolo = Txt(infProt, "nProt"),
            DataAutorizacao = Txt(infProt, "dhRecbto"),
            Emitente = new DanfeEmitente
            {
                Nome = Txt(emit, "xNome"),
                Fantasia = Txt(emit, "xFant"),
                Cnpj = FirstNonEmpty(Txt(emit, "CNPJ"), Txt(emit, "CPF")),
                Ie = Txt(emit, "IE"),
                Iest = Txt(emit, "IEST"),
                Endereco = LerEndereco(emit is null ? null : First(emit, "enderEmit")),
            },
            Destinatario = new DanfeParticipante
            {
                Nome = Txt(dest, "xNome"),
                Documento = FirstNonEmpty(Txt(dest, "CNPJ"), Txt(dest, "CPF"), Txt(dest, "idEstrangeiro")),
                Ie = Txt(dest, "IE"),
                Endereco = LerEndereco(dest is null ? null : First(dest, "enderDest")),
            },
            Totais = new DanfeTotais
            {
                BaseIcms = Txt(total, "vBC"),
                ValorIcms = Txt(total, "vICMS"),
                BaseIcmsSt = Txt(total, "vBCST"),
                ValorIcmsSt = Txt(total, "vST"),
                ValorProdutos = Txt(total, "vProd"),
                ValorFrete = Txt(total, "vFrete"),
                ValorSeguro = Txt(total, "vSeg"),
                ValorDesconto = Txt(total, "vDesc"),
                ValorOutras = Txt(total, "vOutro"),
                ValorIpi = Txt(total, "vIPI"),
                ValorAproxTributos = Txt(total, "vTotTrib"),
                ValorTotal = Txt(total, "vNF"),
                TemIbsCbs = totalIbsCbs is not null,
                BaseIbsCbs = Txt(totalIbsCbs, "vBCIBSCBS"),
                ValorIbs = Txt(totalIbsCbs, "vIBS"),
                ValorCbs = Txt(totalIbsCbs, "vCBS"),
            },
            Transporte = new DanfeTransporte
            {
                Modalidade = Txt(transp, "modFrete"),
                TransportadorNome = Txt(transporta, "xNome"),
                TransportadorDoc = FirstNonEmpty(Txt(transporta, "CNPJ"), Txt(transporta, "CPF")),
                TransportadorIe = Txt(transporta, "IE"),
                TransportadorEndereco = Txt(transporta, "xEnder"),
                TransportadorMunicipio = Txt(transporta, "xMun"),
                TransportadorUf = Txt(transporta, "UF"),
                Placa = Txt(veic, "placa"),
                UfVeiculo = Txt(veic, "UF"),
                Rntc = Txt(veic, "RNTC"),
                QuantidadeVolumes = Txt(vol, "qVol"),
                Especie = Txt(vol, "esp"),
                Marca = Txt(vol, "marca"),
                Numeracao = Txt(vol, "nVol"),
                PesoLiquido = Txt(vol, "pesoL"),
                PesoBruto = Txt(vol, "pesoB"),
            },
            Fatura = fatura,
            Duplicatas = duplicatas,
            Itens = itens,
            InformacoesComplementares = Txt(infAdic, "infCpl"),
            ReservadoFisco = Txt(infAdic, "infAdFisco"),
        };
    }

    // ---- helpers de leitura namespace-agnostica ---------------------------

    private static XElement? First(XElement? root, string localName) =>
        root?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static string Txt(XElement? el, string localName)
    {
        if (el is null) return "";
        var found = el.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
        return (found?.Value ?? "").Trim();
    }

    private static string FirstNonEmpty(params string[] valores)
    {
        foreach (var v in valores)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    private static DanfeEndereco LerEndereco(XElement? el) => new()
    {
        Logradouro = Txt(el, "xLgr"),
        Numero = Txt(el, "nro"),
        Complemento = Txt(el, "xCpl"),
        Bairro = Txt(el, "xBairro"),
        Municipio = Txt(el, "xMun"),
        Uf = Txt(el, "UF"),
        Cep = Txt(el, "CEP"),
        Fone = Txt(el, "fone"),
    };

    // ---- formatadores para exibicao ---------------------------------------

    public static string FormatarChave(string chave) =>
        string.Join(" ", Chunk(new string(chave.Where(char.IsDigit).ToArray()), 4));

    public static string FormatarDocumento(string doc)
    {
        var d = new string(doc.Where(char.IsDigit).ToArray());
        return d.Length switch
        {
            14 => $"{d[..2]}.{d.Substring(2, 3)}.{d.Substring(5, 3)}/{d.Substring(8, 4)}-{d.Substring(12, 2)}",
            11 => $"{d[..3]}.{d.Substring(3, 3)}.{d.Substring(6, 3)}-{d.Substring(9, 2)}",
            _ => doc,
        };
    }

    public static string FormatarCep(string cep)
    {
        var d = new string(cep.Where(char.IsDigit).ToArray());
        return d.Length == 8 ? $"{d[..5]}-{d.Substring(5, 3)}" : cep;
    }

    public static string FormatarMoeda(string valor)
    {
        if (decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return n.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        return string.IsNullOrEmpty(valor) ? "0,00" : valor;
    }

    public static string FormatarQuantidade(string valor)
    {
        if (decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return n.ToString("0.####", CultureInfo.GetCultureInfo("pt-BR"));
        return valor ?? "";
    }

    public static string FormatarTresCasas(string valor)
    {
        if (decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return n.ToString("N3", CultureInfo.GetCultureInfo("pt-BR"));
        return string.IsNullOrEmpty(valor) ? "0,000" : valor;
    }

    public static string FormatarDataHora(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTimeOffset.TryParse(iso, out var d)
            ? d.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")) : iso;
    }

    public static string FormatarData(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTimeOffset.TryParse(iso, out var d)
            ? d.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) : iso;
    }

    public static string FormatarHora(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTimeOffset.TryParse(iso, out var d)
            ? d.ToString("HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR")) : "";
    }

    /// <summary>Numero da NF formatado como no DANFE: "000.059.128".</summary>
    public static string FormatarNumero(string numero)
        => long.TryParse(numero, out var n)
            ? n.ToString("000\\.000\\.000", CultureInfo.InvariantCulture)
            : numero;

    public static string DescricaoModalidadeFrete(string mod) => mod switch
    {
        "0" => "0 - Remetente (CIF)",
        "1" => "1 - Destinatario (FOB)",
        "2" => "2 - Terceiros",
        "3" => "3 - Proprio (Remetente)",
        "4" => "4 - Proprio (Destinatario)",
        "9" => "9 - Sem transporte",
        _ => mod,
    };

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}

public sealed class DanfeEndereco
{
    public string Logradouro { get; init; } = "";
    public string Numero { get; init; } = "";
    public string Complemento { get; init; } = "";
    public string Bairro { get; init; } = "";
    public string Municipio { get; init; } = "";
    public string Uf { get; init; } = "";
    public string Cep { get; init; } = "";
    public string Fone { get; init; } = "";
}

public sealed class DanfeEmitente
{
    public string Nome { get; init; } = "";
    public string Fantasia { get; init; } = "";
    public string Cnpj { get; init; } = "";
    public string Ie { get; init; } = "";
    public string Iest { get; init; } = "";
    public DanfeEndereco Endereco { get; init; } = new();
}

public sealed class DanfeParticipante
{
    public string Nome { get; init; } = "";
    public string Documento { get; init; } = "";
    public string Ie { get; init; } = "";
    public DanfeEndereco Endereco { get; init; } = new();
}

public sealed class DanfeTotais
{
    public string BaseIcms { get; init; } = "";
    public string ValorIcms { get; init; } = "";
    public string BaseIcmsSt { get; init; } = "";
    public string ValorIcmsSt { get; init; } = "";
    public string ValorProdutos { get; init; } = "";
    public string ValorFrete { get; init; } = "";
    public string ValorSeguro { get; init; } = "";
    public string ValorDesconto { get; init; } = "";
    public string ValorOutras { get; init; } = "";
    public string ValorIpi { get; init; } = "";
    public string ValorAproxTributos { get; init; } = "";
    public string ValorTotal { get; init; } = "";
    public bool TemIbsCbs { get; init; }
    public string BaseIbsCbs { get; init; } = "";
    public string ValorIbs { get; init; } = "";
    public string ValorCbs { get; init; } = "";
}

public sealed class DanfeTransporte
{
    public string Modalidade { get; init; } = "";
    public string TransportadorNome { get; init; } = "";
    public string TransportadorDoc { get; init; } = "";
    public string TransportadorIe { get; init; } = "";
    public string TransportadorEndereco { get; init; } = "";
    public string TransportadorMunicipio { get; init; } = "";
    public string TransportadorUf { get; init; } = "";
    public string Placa { get; init; } = "";
    public string UfVeiculo { get; init; } = "";
    public string Rntc { get; init; } = "";
    public string QuantidadeVolumes { get; init; } = "";
    public string Especie { get; init; } = "";
    public string Marca { get; init; } = "";
    public string Numeracao { get; init; } = "";
    public string PesoLiquido { get; init; } = "";
    public string PesoBruto { get; init; } = "";
}

public sealed class DanfeFatura
{
    public string Numero { get; init; } = "";
    public string ValorOriginal { get; init; } = "";
    public string ValorDesconto { get; init; } = "";
    public string ValorLiquido { get; init; } = "";
}

public sealed class DanfeDuplicata
{
    public string Numero { get; init; } = "";
    public string Vencimento { get; init; } = "";
    public string Valor { get; init; } = "";
}

public sealed class DanfeItem
{
    public string Numero { get; init; } = "";
    public string Codigo { get; init; } = "";
    public string Descricao { get; init; } = "";
    public string Ncm { get; init; } = "";
    public string Cst { get; init; } = "";
    public string Cfop { get; init; } = "";
    public string Unidade { get; init; } = "";
    public string Quantidade { get; init; } = "";
    public string ValorUnitario { get; init; } = "";
    public string ValorTotal { get; init; } = "";
    public string ValorDesconto { get; init; } = "";
    public string BaseIcms { get; init; } = "";
    public string ValorIcms { get; init; } = "";
    public string AliqIcms { get; init; } = "";
    public string ValorIpi { get; init; } = "";
    public string AliqIpi { get; init; } = "";
}
