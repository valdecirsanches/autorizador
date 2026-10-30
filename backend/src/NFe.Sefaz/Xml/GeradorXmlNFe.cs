using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;

namespace NFe.Sefaz.Xml;

/// <summary>Resultado da geracao: XML e a chave de acesso ja calculada.</summary>
public sealed record NFeGerada(string Xml, string ChaveAcesso, string CodigoNumerico, int DigitoVerificador);

/// <summary>
/// Gera o XML da NF-e (elemento NFe/infNFe) no layout 4.00 a partir do emitente
/// (Empresa) e de um NotaFiscalRequest. A ordem dos elementos segue o XSD, pois
/// o schema da NF-e usa xs:sequence e rejeita qualquer campo fora de ordem.
///
/// Cobre o caminho mais comum (ICMS00 para regime normal, ICMSSN102 para Simples,
/// PIS/COFINS nao tributados). Grupos tributarios adicionais sao pontos de
/// extensao explicitos em <see cref="GrupoIcms"/> e <see cref="GrupoPisCofins"/>.
/// </summary>
public sealed class GeradorXmlNFe
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/nfe";
    private const string VersaoLayout = "4.00";

    private const string TextoHomologacao =
        "NF-E EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";

    public NFeGerada Gerar(Empresa emit, NotaFiscalRequest req, TipoAmbiente ambiente)
    {
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentNullException.ThrowIfNull(req);

        if (req.Itens.Count == 0)
            throw new InvalidOperationException("A nota precisa de ao menos um item.");

        ValidarFinalidade(req);
        ValidarVolumes(req);
        ValidarFrete(req);
        ValidarTransporte(req);

        var cNF = string.IsNullOrWhiteSpace(req.CodigoNumerico)
            ? SortearCodigoNumerico()
            : req.CodigoNumerico.PadLeft(8, '0');

        // O cNF nao pode ser igual ao numero da nota (regra de validacao 'BF').
        if (cNF == req.Numero.ToString("D8"))
            cNF = SortearCodigoNumerico();

        var dataEmissao = req.DataEmissao;
        var chave = ChaveAcesso.Compor(
            emit.Uf, dataEmissao.Year, dataEmissao.Month, emit.Cnpj,
            req.Modelo, req.Serie, req.Numero, req.TipoEmissao, cNF);

        var dv = chave[43] - '0';
        var cUF = CodigoUf.Codigo(emit.Uf);

        var infNFe = new XElement(Ns + "infNFe",
            new XAttribute("versao", VersaoLayout),
            new XAttribute("Id", ChaveAcesso.ParaId(chave)),
            GerarIde(req, cUF, cNF, dv, ambiente, emit),
            GerarEmit(emit),
            GerarDest(req.Destinatario, ambiente),
            GerarItens(req, emit.RegimeTributario),
            GerarTotal(req),
            new XElement(Ns + "transp",
                // modFrete e um unico digito (TModFrete: 0,1,2,3,4,9). Nao pode
                // ser preenchido com zero a esquerda ("09" quebra a enumeracao).
                new XElement(Ns + "modFrete", ((int)req.ModalidadeFrete).ToString(CultureInfo.InvariantCulture)),
                GerarTransportadora(req.Transportadora),
                GerarVeiculo(req.Veiculo),
                GerarVolume(req)),
            GerarCobranca(req),
            GerarPagamento(req),
            GerarInfoAdicional(req, ambiente));

        var nfe = new XElement(Ns + "NFe", infNFe);
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), nfe);

        return new NFeGerada(SerializarSemBom(doc), chave, cNF, dv);
    }

    /// <summary>
    /// Regenera o bloco &lt;emit&gt; de um XML de NF-e JA GERADO usando o cadastro
    /// ATUAL da empresa (CRT, IE, endereco, razao social). Usado no reenvio de uma
    /// nota nao autorizada apos o usuario corrigir o cadastro — por exemplo, a
    /// rejeicao 481 (CRT diverge do cadastro na SEFAZ).
    ///
    /// Remove a assinatura anterior (o XML precisa ser reassinado) e devolve o XML
    /// NAO assinado. A chave de acesso NAO muda: CRT/IE/endereco nao entram na
    /// composicao da chave, entao a referencia da nova assinatura continua valida.
    ///
    /// Rejeita a operacao quando a CATEGORIA de regime muda (Simples &lt;-&gt; Normal),
    /// pois nesse caso os grupos de ICMS dos itens tambem precisariam ser refeitos
    /// e o correto e emitir a nota do zero.
    /// </summary>
    public string RegerarEmitente(string xmlAtual, Empresa emit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlAtual);
        ArgumentNullException.ThrowIfNull(emit);

        var doc = XDocument.Parse(xmlAtual);
        var infNFe = doc.Descendants(Ns + "infNFe").FirstOrDefault()
            ?? throw new InvalidOperationException("XML sem elemento infNFe; nao pode ser regenerado.");

        // Coerencia de regime: Simples (ICMSSN*) x Normal (ICMS00/40/...).
        var usaSimplesNoXml = infNFe.Descendants()
            .Any(e => e.Name.LocalName.StartsWith("ICMSSN", StringComparison.Ordinal));
        var novoEhSimples = emit.RegimeTributario is RegimeTributario.SimplesNacional
            or RegimeTributario.SimplesNacionalExcessoSublimite
            or RegimeTributario.MicroempreendedorIndividual;
        if (usaSimplesNoXml != novoEhSimples)
            throw new InvalidOperationException(
                "A mudanca de categoria de regime (Simples <-> Normal) exige refazer os " +
                "grupos de tributacao dos itens. Emita a nota novamente em vez de reenviar.");

        var emitAntigo = infNFe.Element(Ns + "emit")
            ?? throw new InvalidOperationException("XML sem elemento emit; nao pode ser regenerado.");
        emitAntigo.ReplaceWith(GerarEmit(emit));

        // Remove a assinatura anterior — o documento sera reassinado apos regerar.
        XNamespace dsig = "http://www.w3.org/2000/09/xmldsig#";
        foreach (var sig in doc.Descendants(dsig + "Signature").ToList())
            sig.Remove();

        return SerializarSemBom(doc);
    }

    private static XElement GerarIde(
        NotaFiscalRequest req, int cUF, string cNF, int dv, TipoAmbiente ambiente, Empresa emit)
    {
        var ide = new XElement(Ns + "ide",
            new XElement(Ns + "cUF", cUF),
            new XElement(Ns + "cNF", cNF),
            new XElement(Ns + "natOp", req.NaturezaOperacao),
            new XElement(Ns + "mod", (int)req.Modelo),
            new XElement(Ns + "serie", req.Serie),
            new XElement(Ns + "nNF", req.Numero),
            new XElement(Ns + "dhEmi", FormatarDataHora(req.DataEmissao, emit.Uf)),
            new XElement(Ns + "tpNF", (int)req.TipoOperacao),
            new XElement(Ns + "idDest", (int)req.DestinoOperacao),
            new XElement(Ns + "cMunFG", emit.CodigoMunicipio),
            new XElement(Ns + "tpImp", 1),
            new XElement(Ns + "tpEmis", (int)req.TipoEmissao),
            new XElement(Ns + "cDV", dv),
            new XElement(Ns + "tpAmb", (int)ambiente),
            new XElement(Ns + "finNFe", req.Finalidade),
            new XElement(Ns + "indFinal", req.ConsumidorFinal ? 1 : 0),
            new XElement(Ns + "indPres", (int)req.IndicadorPresenca),
            new XElement(Ns + "procEmi", 0),
            new XElement(Ns + "verProc", "NFeAutorizador/1.0"));

        // dhSaiEnt e opcional; no XSD 4.00 vem logo apos dhEmi e antes de tpNF.
        if (req.DataSaidaEntrada is { } saida)
        {
            ide.Element(Ns + "dhEmi")!
               .AddAfterSelf(new XElement(Ns + "dhSaiEnt", FormatarDataHora(saida, emit.Uf)));
        }

        // NFref e o ultimo grupo do ide no XSD 4.00 (apos verProc, dhCont e xJust).
        foreach (var chaveReferenciada in ChavesReferenciadas(req))
            ide.Add(new XElement(Ns + "NFref", new XElement(Ns + "refNFe", chaveReferenciada)));

        return ide;
    }

    private static List<string> ChavesReferenciadas(NotaFiscalRequest req) =>
        req.DocumentosReferenciados
            .Select(d => new string((d?.ChaveNFe ?? string.Empty).Where(char.IsDigit).ToArray()))
            .ToList();

    private static void ValidarFinalidade(NotaFiscalRequest req)
    {
        if (req.Finalidade is < 1 or > 4)
            throw new ArgumentException(
                "finalidade deve ser 1 (normal), 2 (complementar), 3 (ajuste) ou 4 (devolucao).",
                nameof(req));

        var chaves = ChavesReferenciadas(req);

        if (req.Finalidade != 1 && chaves.Count == 0)
            throw new ArgumentException(
                "finalidade " + req.Finalidade + " exige ao menos uma NF-e em documentosReferenciados " +
                "(chave da nota original).",
                nameof(req));

        if (chaves.Count > 500)
            throw new ArgumentException("documentosReferenciados aceita no maximo 500 notas.", nameof(req));

        foreach (var chave in chaves)
            if (!ChaveAcesso.Valida(chave))
                throw new ArgumentException(
                    "documentosReferenciados: chaveNFe invalida (informe 44 digitos com digito verificador correto).",
                    nameof(req));

        if (chaves.Distinct().Count() != chaves.Count)
            throw new ArgumentException("documentosReferenciados: chaveNFe repetida.", nameof(req));
    }

    private static XElement GerarEmit(Empresa emit)
    {
        var ender = new XElement(Ns + "enderEmit",
            new XElement(Ns + "xLgr", emit.Logradouro),
            new XElement(Ns + "nro", emit.Numero),
            Opcional("xCpl", emit.Complemento),
            new XElement(Ns + "xBairro", emit.Bairro),
            new XElement(Ns + "cMun", emit.CodigoMunicipio),
            new XElement(Ns + "xMun", emit.NomeMunicipio),
            new XElement(Ns + "UF", emit.Uf),
            new XElement(Ns + "CEP", emit.Cep),
            new XElement(Ns + "cPais", emit.CodigoPais),
            new XElement(Ns + "xPais", emit.NomePais),
            Fone(emit.Telefone));

        return new XElement(Ns + "emit",
            new XElement(Ns + "CNPJ", emit.Cnpj),
            new XElement(Ns + "xNome", emit.RazaoSocial),
            Opcional("xFant", emit.NomeFantasia),
            ender,
            new XElement(Ns + "IE", SoDigitos(emit.InscricaoEstadual)),
            new XElement(Ns + "CRT", (int)emit.RegimeTributario));
    }

    private static XElement GerarDest(DestinatarioRequest dest, TipoAmbiente ambiente)
    {
        var doc = new string(dest.Documento.Where(char.IsDigit).ToArray());
        var elementoDoc = doc.Length == 14
            ? new XElement(Ns + "CNPJ", doc)
            : new XElement(Ns + "CPF", doc);

        // Em homologacao a razao social do destinatario e padronizada por norma.
        var nome = ambiente == TipoAmbiente.Homologacao ? TextoHomologacao : dest.Nome;

        var ender = new XElement(Ns + "enderDest",
            new XElement(Ns + "xLgr", dest.Logradouro),
            new XElement(Ns + "nro", dest.Numero),
            Opcional("xCpl", dest.Complemento),
            new XElement(Ns + "xBairro", dest.Bairro),
            new XElement(Ns + "cMun", dest.CodigoMunicipio),
            new XElement(Ns + "xMun", dest.NomeMunicipio),
            new XElement(Ns + "UF", dest.Uf),
            new XElement(Ns + "CEP", dest.Cep),
            new XElement(Ns + "cPais", dest.CodigoPais),
            new XElement(Ns + "xPais", dest.NomePais),
            Fone(dest.Telefone));

        var elementos = new List<object> { elementoDoc, new XElement(Ns + "xNome", nome), ender };

        // indIEDest e obrigatorio; IE so aparece para contribuinte.
        elementos.Add(new XElement(Ns + "indIEDest", (int)dest.IndicadorIe));
        if (dest.IndicadorIe == IndicadorIeDestinatario.ContribuinteIcms &&
            !string.IsNullOrWhiteSpace(dest.InscricaoEstadual))
            elementos.Add(new XElement(Ns + "IE", SoDigitos(dest.InscricaoEstadual)));

        var email = Opcional("email", dest.Email);
        if (email is not null) elementos.Add(email);

        return new XElement(Ns + "dest", elementos);
    }

    private static IEnumerable<XElement> GerarItens(NotaFiscalRequest req, RegimeTributario regime)
    {
        // vOutro (outras despesas) ja rateado por item; a soma fecha com o total.
        var outroPorItem = RatearOutrasDespesas(req);
        var fretePorItem = RatearFrete(req);

        for (var idx = 0; idx < req.Itens.Count; idx++)
        {
            var item = req.Itens[idx];
            var vProd = item.ValorBruto > 0
                ? item.ValorBruto
                : item.Quantidade * item.ValorUnitario;

            var prod = new XElement(Ns + "prod",
                new XElement(Ns + "cProd", item.Codigo),
                new XElement(Ns + "cEAN", item.Gtin),
                new XElement(Ns + "xProd", item.Descricao),
                new XElement(Ns + "NCM", item.Ncm));

            // cBenef fica entre NCM (e CEST/CNPJFab) e CFOP na sequencia do layout 4.00.
            if (!string.IsNullOrWhiteSpace(item.CBenef))
                prod.Add(new XElement(Ns + "cBenef", item.CBenef.Trim()));

            prod.Add(
                new XElement(Ns + "CFOP", item.Cfop),
                new XElement(Ns + "uCom", item.UnidadeComercial),
                new XElement(Ns + "qCom", Num(item.Quantidade, 3)),
                new XElement(Ns + "vUnCom", Num(item.ValorUnitario, 3)),
                new XElement(Ns + "vProd", Num(vProd, 2)),
                new XElement(Ns + "cEANTrib", item.Gtin),
                new XElement(Ns + "uTrib", item.UnidadeComercial),
                new XElement(Ns + "qTrib", Num(item.Quantidade, 3)),
                new XElement(Ns + "vUnTrib", Num(item.ValorUnitario, 3)));

            if (fretePorItem[idx] > 0)
                prod.Add(new XElement(Ns + "vFrete", Num(fretePorItem[idx], 2)));
            if (item.ValorDesconto > 0)
                prod.Add(new XElement(Ns + "vDesc", Num(item.ValorDesconto, 2)));
            // vOutro por item: preco unitario permanece intacto; o acrescimo
            // aparece como "Outras despesas" e nao dispara a rejeicao 610.
            if (outroPorItem[idx] > 0)
                prod.Add(new XElement(Ns + "vOutro", Num(outroPorItem[idx], 2)));

            // indTot=1: o valor do item compoe o total da nota.
            prod.Add(new XElement(Ns + "indTot", 1));

            var imposto = new XElement(Ns + "imposto",
                new XElement(Ns + "ICMS", GrupoIcms(item, regime)),
                new XElement(Ns + "PIS", GrupoPisCofins(item.CstPis, "PIS",
                    item.BaseCalculoPis, item.AliquotaPis, item.ValorPis)),
                new XElement(Ns + "COFINS", GrupoPisCofins(item.CstCofins, "COFINS",
                    item.BaseCalculoCofins, item.AliquotaCofins, item.ValorCofins)));

            // IBS/CBS vem depois de PIS/COFINS na sequencia do imposto. So entra
            // quando o app envia os dados; sem eles a nota sai como antes.
            if (item.IbsCbs is not null)
                imposto.Add(GrupoIbsCbs(item, idx + 1));

            yield return new XElement(Ns + "det",
                new XAttribute("nItem", idx + 1),
                prod,
                imposto);
        }
    }

    /// <summary>
    /// Distribui as despesas acessorias em vOutro por item. Cada posicao recebe o
    /// ValorOutro explicito do item mais a parcela rateada de
    /// NotaFiscalRequest.OutrasDespesas, proporcional ao vProd. A sobra de
    /// centavos do arredondamento cai na ultima linha para que a soma dos itens
    /// bata exatamente com o vOutro do total (evita a rejeicao 610).
    /// </summary>
    private static decimal[] RatearOutrasDespesas(NotaFiscalRequest req) =>
        RatearValor(req, req.Itens.Select(i => i.ValorOutro).ToArray(), req.OutrasDespesas);

    /// <summary>
    /// vFrete por item: ValorFrete explicito do item mais a parcela rateada de
    /// NotaFiscalRequest.ValorFrete (mesmo criterio das outras despesas), de modo
    /// que a soma dos itens seja exatamente o vFrete do total.
    /// </summary>
    private static decimal[] RatearFrete(NotaFiscalRequest req) =>
        RatearValor(req, req.Itens.Select(i => i.ValorFrete).ToArray(), req.ValorFrete);

    private static decimal[] RatearValor(NotaFiscalRequest req, decimal[] porItem, decimal extra)
    {
        var n = req.Itens.Count;
        var resultado = (decimal[])porItem.Clone();

        if (extra <= 0 || n == 0)
            return resultado;

        var vProds = new decimal[n];
        decimal totalProd = 0;
        for (var i = 0; i < n; i++)
        {
            var it = req.Itens[i];
            vProds[i] = it.ValorBruto > 0 ? it.ValorBruto : it.Quantidade * it.ValorUnitario;
            totalProd += vProds[i];
        }

        decimal acumulado = 0;
        for (var i = 0; i < n; i++)
        {
            decimal parcela;
            if (i == n - 1)
            {
                parcela = extra - acumulado; // sobra de centavos na ultima linha
            }
            else
            {
                parcela = totalProd > 0
                    ? decimal.Round(extra * vProds[i] / totalProd, 2, MidpointRounding.AwayFromZero)
                    : decimal.Round(extra / n, 2, MidpointRounding.AwayFromZero);
                acumulado += parcela;
            }

            resultado[i] += parcela;
        }

        return resultado;
    }

    /// <summary>
    /// Ponto de extensao para os grupos de ICMS. Regime normal usa ICMS + CST;
    /// Simples usa ICMSSN + CSOSN. Implementados os casos mais comuns (00 e 102).
    /// </summary>
    private static XElement GrupoIcms(ItemNotaRequest item, RegimeTributario regime)
    {
        var origem = item.OrigemMercadoria.ToString();
        var simples = regime is RegimeTributario.SimplesNacional
            or RegimeTributario.SimplesNacionalExcessoSublimite
            or RegimeTributario.MicroempreendedorIndividual;
        var cstInformado = NormalizarCstIcms(item.CstIcms);

        if (simples)
        {
            // CSOSN 102: sem permissao de credito, sem tributacao no Simples.
            var csosn = cstInformado is "102" or "103" or "300" or "400" or "500" ? cstInformado : "102";
            return new XElement(Ns + "ICMSSN102",
                new XElement(Ns + "orig", origem),
                new XElement(Ns + "CSOSN", csosn));
        }

        // Regime normal. ICMS00: tributacao integral.
        if (cstInformado == "00")
        {
            return new XElement(Ns + "ICMS00",
                new XElement(Ns + "orig", origem),
                new XElement(Ns + "CST", "00"),
                new XElement(Ns + "modBC", 3),
                new XElement(Ns + "vBC", Num(item.BaseCalculoIcms, 2)),
                new XElement(Ns + "pICMS", Num(item.AliquotaIcms, 4)),
                new XElement(Ns + "vICMS", Num(item.ValorIcms, 2)));
        }

        // ICMS20: tributacao com reducao da base de calculo.
        if (cstInformado == "20")
        {
            return new XElement(Ns + "ICMS20",
                new XElement(Ns + "orig", origem),
                new XElement(Ns + "CST", "20"),
                new XElement(Ns + "modBC", 3),
                new XElement(Ns + "pRedBC", Num(PercentualReducaoBc(item), 4)),
                new XElement(Ns + "vBC", Num(item.BaseCalculoIcms, 2)),
                new XElement(Ns + "pICMS", Num(item.AliquotaIcms, 4)),
                new XElement(Ns + "vICMS", Num(item.ValorIcms, 2)));
        }

        // CST 40/41/50 (isenta/nao tributada/suspensao): grupo ICMS40.
        if (cstInformado is "40" or "41" or "50")
        {
            return new XElement(Ns + "ICMS40",
                new XElement(Ns + "orig", origem),
                new XElement(Ns + "CST", cstInformado));
        }

        // ICMS60: cobrado anteriormente por ST. Ordem do XSD: vBCSTRet, pST, vICMSSubstituto, vICMSSTRet.
        if (cstInformado == "60")
        {
            var icms60 = new XElement(Ns + "ICMS60",
                new XElement(Ns + "orig", origem),
                new XElement(Ns + "CST", "60"));
            if (item.BaseCalculoIcmsStRetido is { } vBcStRet)
                icms60.Add(new XElement(Ns + "vBCSTRet", Num(vBcStRet, 2)));
            if (item.AliquotaSuportadaConsumidor is { } pSt)
                icms60.Add(new XElement(Ns + "pST", Num(pSt, 4)));
            if (item.ValorIcmsSubstituto is { } vSubst)
                icms60.Add(new XElement(Ns + "vICMSSubstituto", Num(vSubst, 2)));
            if (item.ValorIcmsStRetido is { } vStRet)
                icms60.Add(new XElement(Ns + "vICMSSTRet", Num(vStRet, 2)));
            return icms60;
        }

        throw new ArgumentException(
            $"cstIcms '{item.CstIcms}' (item {item.Codigo}) ainda nao e suportado no regime normal. " +
            "Suportados: 00, 20, 40, 41, 50 e 60.");
    }

    /// <summary>
    /// Aceita o CST com ou sem formatacao (ex.: "20.0" ou "020"), devolvendo os
    /// dois digitos do CST (regime normal) ou os tres do CSOSN (Simples).
    /// </summary>
    private static string NormalizarCstIcms(string? cst)
    {
        var texto = (cst ?? string.Empty).Trim();
        var ponto = texto.IndexOf('.');
        if (ponto >= 0) texto = texto[..ponto];
        var digitos = SoDigitos(texto);
        return digitos.Length == 1 ? "0" + digitos : digitos;
    }

    private static decimal PercentualReducaoBc(ItemNotaRequest item)
    {
        if (item.PercentualReducaoBcIcms is { } informado)
        {
            // 100% e valido (base zerada, ex.: pao com cBenef de isencao); o XSD aceita ate 100.00.
            if (informado is <= 0 or > 100)
                throw new ArgumentException(
                    $"percentualReducaoBcIcms do item {item.Codigo} deve ser maior que 0 e no maximo 100.");
            return informado;
        }

        var bruto = item.ValorBruto > 0 ? item.ValorBruto : item.Quantidade * item.ValorUnitario;
        if (bruto <= 0 || item.BaseCalculoIcms <= 0 || item.BaseCalculoIcms >= bruto)
            throw new ArgumentException(
                $"CST 20 no item {item.Codigo}: baseCalculoIcms ({item.BaseCalculoIcms}) deve ser menor que o " +
                $"valor do item ({bruto}) para derivar a reducao. Corrija a base ou informe percentualReducaoBcIcms.");

        return Math.Round((1 - item.BaseCalculoIcms / bruto) * 100, 4);
    }

    /// <summary>
    /// Grupo PIS/COFINS conforme o CST: 04-09 nao tributado (NT); 01/02 por
    /// aliquota (Aliq); 49, 50-56, 60-67, 70-75, 98 e 99 outras operacoes (Outr).
    /// CST 03 (por quantidade) ainda nao e suportado.
    /// </summary>
    private static XElement GrupoPisCofins(string cst, string tributo, decimal baseCalculo, decimal aliquota, decimal valor)
    {
        if (cst is "04" or "05" or "06" or "07" or "08" or "09")
        {
            return new XElement(Ns + (tributo + "NT"),
                new XElement(Ns + "CST", cst));
        }

        var grupo = cst switch
        {
            "01" or "02" => tributo + "Aliq",
            "49" or "99" or "98" => tributo + "Outr",
            _ when int.TryParse(cst, out var n) && (n is >= 50 and <= 56 or >= 60 and <= 67 or >= 70 and <= 75)
                => tributo + "Outr",
            _ => null
        };

        if (grupo is null)
        {
            throw new ArgumentException(cst == "03"
                ? $"CST 03 de {tributo} (aliquota por unidade de medida) ainda nao e suportado."
                : $"CST '{cst}' de {tributo} nao existe na tabela oficial (validos: 01-09, 49, 50-56, 60-67, 70-75, 98, 99).");
        }

        return new XElement(Ns + grupo,
            new XElement(Ns + "CST", cst),
            new XElement(Ns + "vBC", Num(baseCalculo, 2)),
            new XElement(Ns + "p" + tributo, Num(aliquota, 4)),
            new XElement(Ns + "v" + tributo, Num(valor, 2)));
    }

    private static XElement GerarTotal(NotaFiscalRequest req)
    {
        decimal vProd = 0, vBC = 0, vICMS = 0, vDesc = 0, vFrete = 0, vPIS = 0, vCOFINS = 0;
        foreach (var i in req.Itens)
        {
            vProd += i.ValorBruto > 0 ? i.ValorBruto : i.Quantidade * i.ValorUnitario;
            vBC += i.BaseCalculoIcms;
            vICMS += i.ValorIcms;
            vDesc += i.ValorDesconto;
            vPIS += i.ValorPis;
            vCOFINS += i.ValorCofins;
        }

        // vFrete do total = soma exata do vFrete rateado por item.
        foreach (var parcela in RatearFrete(req))
            vFrete += parcela;

        // vOutro do total = soma exata das parcelas rateadas por item (mesmo
        // calculo usado em GerarItens), garantindo o fechamento sem a 610.
        decimal vOutro = 0;
        foreach (var parcela in RatearOutrasDespesas(req))
            vOutro += parcela;

        var vNF = CalcularVNF(req);

        var total = new XElement(Ns + "total",
            new XElement(Ns + "ICMSTot",
                new XElement(Ns + "vBC", Num(vBC, 2)),
                new XElement(Ns + "vICMS", Num(vICMS, 2)),
                new XElement(Ns + "vICMSDeson", Num(0, 2)),
                new XElement(Ns + "vFCP", Num(0, 2)),
                new XElement(Ns + "vBCST", Num(0, 2)),
                new XElement(Ns + "vST", Num(0, 2)),
                new XElement(Ns + "vFCPST", Num(0, 2)),
                new XElement(Ns + "vFCPSTRet", Num(0, 2)),
                new XElement(Ns + "vProd", Num(vProd, 2)),
                new XElement(Ns + "vFrete", Num(vFrete, 2)),
                new XElement(Ns + "vSeg", Num(0, 2)),
                new XElement(Ns + "vDesc", Num(vDesc, 2)),
                new XElement(Ns + "vII", Num(0, 2)),
                new XElement(Ns + "vIPI", Num(0, 2)),
                new XElement(Ns + "vIPIDevol", Num(0, 2)),
                new XElement(Ns + "vPIS", Num(vPIS, 2)),
                new XElement(Ns + "vCOFINS", Num(vCOFINS, 2)),
                new XElement(Ns + "vOutro", Num(vOutro, 2)),
                new XElement(Ns + "vNF", Num(vNF, 2))));

        var ibsCbsTot = GerarTotalIbsCbs(req);
        if (ibsCbsTot is not null)
            total.Add(ibsCbsTot);

        return total;
    }

    private readonly record struct ValoresIbsCbs(decimal Base, decimal IbsUf, decimal IbsMun, decimal Cbs)
    {
        public decimal Ibs => IbsUf + IbsMun;
    }

    private static decimal Arredondar2(decimal v) =>
        decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Base e valores do IBS/CBS de um item. Usa os valores informados pelo app e,
    /// na ausencia deles, calcula base x aliquota / 100.
    /// </summary>
    private static ValoresIbsCbs CalcularIbsCbs(ItemNotaRequest item)
    {
        var g = item.IbsCbs!;
        var vProd = item.ValorBruto > 0 ? item.ValorBruto : item.Quantidade * item.ValorUnitario;
        var baseCalculo = g.BaseCalculo > 0 ? g.BaseCalculo : vProd - item.ValorDesconto;
        baseCalculo = Arredondar2(baseCalculo);

        var uf = ResolverAliquota(g.AliquotaIbsUf, g.ReducaoAliquotaIbsUf, g.AliquotaEfetivaIbsUf);
        var mun = ResolverAliquotaMunicipal(g, uf);
        var cbs = ResolverAliquota(g.AliquotaCbs, g.ReducaoAliquotaCbs, g.AliquotaEfetivaCbs);

        return new ValoresIbsCbs(
            baseCalculo,
            Arredondar2(g.ValorIbsUf ?? baseCalculo * uf.Efetiva / 100m),
            Arredondar2(g.ValorIbsMunicipal ?? baseCalculo * mun.Efetiva / 100m),
            Arredondar2(g.ValorCbs ?? baseCalculo * cbs.Efetiva / 100m));
    }

    private readonly record struct AliquotaResolvida(decimal Efetiva, decimal? Reducao);

    /// <summary>
    /// Resolve aliquota efetiva e percentual de reducao a partir do que o app enviou.
    /// Reducao so e devolvida quando maior que zero (so entao o gRed e emitido).
    /// </summary>
    private static AliquotaResolvida ResolverAliquota(decimal aliquota, decimal? reducao, decimal? efetiva)
    {
        if (reducao is null && efetiva is null)
            return new AliquotaResolvida(aliquota, null);

        decimal ef, red;
        if (efetiva is not null)
        {
            ef = efetiva.Value;
            red = reducao ?? (aliquota > 0 ? decimal.Round((1m - ef / aliquota) * 100m, 4) : 0m);
        }
        else
        {
            red = reducao!.Value;
            ef = decimal.Round(aliquota * (1m - red / 100m), 4);
        }

        return red > 0 ? new AliquotaResolvida(ef, red) : new AliquotaResolvida(ef, null);
    }

    /// <summary>
    /// O IBS e um tributo unico repartido entre UF e municipio, e a SEFAZ (rejeicao 1074)
    /// exige gRed nos dois quando ha reducao. Se o app informou so a reducao da UF, o
    /// municipio herda o mesmo percentual; a aliquota efetiva e recalculada sobre a
    /// aliquota municipal.
    /// </summary>
    private static AliquotaResolvida ResolverAliquotaMunicipal(IbsCbsItemRequest g, AliquotaResolvida uf)
    {
        if (g.ReducaoAliquotaIbsMunicipal is null && g.AliquotaEfetivaIbsMunicipal is null && uf.Reducao is not null)
            return ResolverAliquota(g.AliquotaIbsMunicipal, uf.Reducao, null);

        return ResolverAliquota(g.AliquotaIbsMunicipal, g.ReducaoAliquotaIbsMunicipal, g.AliquotaEfetivaIbsMunicipal);
    }

    private static XElement? GrupoReducao(AliquotaResolvida a) =>
        a.Reducao is null
            ? null
            : new XElement(Ns + "gRed",
                new XElement(Ns + "pRedAliq", Num(a.Reducao.Value, 4)),
                new XElement(Ns + "pAliqEfet", Num(a.Efetiva, 4)));

    private static void ValidarReducao(int nItem, string nome, decimal? reducao, decimal? efetiva)
    {
        if (reducao is < 0 or > 100)
            throw new InvalidOperationException($"Item {nItem}: reducao da aliquota {nome} deve estar entre 0 e 100.");
        if (efetiva is < 0)
            throw new InvalidOperationException($"Item {nItem}: aliquota efetiva {nome} nao pode ser negativa.");
    }

    private static XElement GrupoIbsCbs(ItemNotaRequest item, int nItem)
    {
        var g = item.IbsCbs!;
        var cst = SoDigitos(g.Cst);
        var classe = SoDigitos(g.ClassificacaoTributaria);

        if (cst.Length != 3)
            throw new InvalidOperationException($"Item {nItem}: CST do IBS/CBS deve ter 3 digitos.");
        if (classe.Length != 6)
            throw new InvalidOperationException(
                $"Item {nItem}: classificacao tributaria (cClassTrib) do IBS/CBS deve ter 6 digitos.");
        if (g.BaseCalculo < 0 || g.AliquotaIbsUf < 0 || g.AliquotaIbsMunicipal < 0 || g.AliquotaCbs < 0)
            throw new InvalidOperationException($"Item {nItem}: base e aliquotas do IBS/CBS nao podem ser negativas.");

        ValidarReducao(nItem, "do IBS UF", g.ReducaoAliquotaIbsUf, g.AliquotaEfetivaIbsUf);
        ValidarReducao(nItem, "do IBS municipal", g.ReducaoAliquotaIbsMunicipal, g.AliquotaEfetivaIbsMunicipal);
        ValidarReducao(nItem, "da CBS", g.ReducaoAliquotaCbs, g.AliquotaEfetivaCbs);

        var v = CalcularIbsCbs(item);
        var redUf = ResolverAliquota(g.AliquotaIbsUf, g.ReducaoAliquotaIbsUf, g.AliquotaEfetivaIbsUf);
        var redMun = ResolverAliquotaMunicipal(g, redUf);
        var redCbs = ResolverAliquota(g.AliquotaCbs, g.ReducaoAliquotaCbs, g.AliquotaEfetivaCbs);

        return new XElement(Ns + "IBSCBS",
            new XElement(Ns + "CST", cst),
            new XElement(Ns + "cClassTrib", classe),
            new XElement(Ns + "gIBSCBS",
                new XElement(Ns + "vBC", Num(v.Base, 2)),
                new XElement(Ns + "gIBSUF",
                    new XElement(Ns + "pIBSUF", Num(g.AliquotaIbsUf, 4)),
                    GrupoReducao(redUf),
                    new XElement(Ns + "vIBSUF", Num(v.IbsUf, 2))),
                new XElement(Ns + "gIBSMun",
                    new XElement(Ns + "pIBSMun", Num(g.AliquotaIbsMunicipal, 4)),
                    GrupoReducao(redMun),
                    new XElement(Ns + "vIBSMun", Num(v.IbsMun, 2))),
                new XElement(Ns + "vIBS", Num(v.Ibs, 2)),
                new XElement(Ns + "gCBS",
                    new XElement(Ns + "pCBS", Num(g.AliquotaCbs, 4)),
                    GrupoReducao(redCbs),
                    new XElement(Ns + "vCBS", Num(v.Cbs, 2)))));
    }

    /// <summary>
    /// Totais IBS/CBS (IBSCBSTot): soma dos itens que informaram IBS/CBS. Retorna
    /// null quando nenhum item informou, mantendo o total como antes.
    /// </summary>
    private static XElement? GerarTotalIbsCbs(NotaFiscalRequest req)
    {
        decimal bc = 0, ibsUf = 0, ibsMun = 0, cbs = 0;
        var algum = false;

        foreach (var item in req.Itens)
        {
            if (item.IbsCbs is null)
                continue;
            algum = true;
            var v = CalcularIbsCbs(item);
            bc += v.Base;
            ibsUf += v.IbsUf;
            ibsMun += v.IbsMun;
            cbs += v.Cbs;
        }

        if (!algum)
            return null;

        return new XElement(Ns + "IBSCBSTot",
            new XElement(Ns + "vBCIBSCBS", Num(bc, 2)),
            new XElement(Ns + "gIBS",
                new XElement(Ns + "gIBSUF",
                    new XElement(Ns + "vDif", Num(0, 2)),
                    new XElement(Ns + "vDevTrib", Num(0, 2)),
                    new XElement(Ns + "vIBSUF", Num(ibsUf, 2))),
                new XElement(Ns + "gIBSMun",
                    new XElement(Ns + "vDif", Num(0, 2)),
                    new XElement(Ns + "vDevTrib", Num(0, 2)),
                    new XElement(Ns + "vIBSMun", Num(ibsMun, 2))),
                new XElement(Ns + "vIBS", Num(ibsUf + ibsMun, 2)),
                new XElement(Ns + "vCredPres", Num(0, 2)),
                new XElement(Ns + "vCredPresCondSus", Num(0, 2))),
            new XElement(Ns + "gCBS",
                new XElement(Ns + "vDif", Num(0, 2)),
                new XElement(Ns + "vDevTrib", Num(0, 2)),
                new XElement(Ns + "vCBS", Num(cbs, 2)),
                new XElement(Ns + "vCredPres", Num(0, 2)),
                new XElement(Ns + "vCredPresCondSus", Num(0, 2))));
    }

    /// <summary>
    /// Valor total da nota (vNF): vProd - vDesc + vFrete + vOutro, com vOutro
    /// vindo do rateio de despesas acessorias. Fonte unica usada tanto no total
    /// (ICMSTot) quanto no grupo de pagamento, para os dois nunca divergirem.
    /// </summary>
    private static decimal CalcularVNF(NotaFiscalRequest req)
    {
        decimal vProd = 0, vDesc = 0, vFrete = 0;
        foreach (var i in req.Itens)
        {
            vProd += i.ValorBruto > 0 ? i.ValorBruto : i.Quantidade * i.ValorUnitario;
            vDesc += i.ValorDesconto;
        }

        foreach (var parcela in RatearFrete(req))
            vFrete += parcela;

        decimal vOutro = 0;
        foreach (var parcela in RatearOutrasDespesas(req))
            vOutro += parcela;

        return vProd - vDesc + vFrete + vOutro;
    }

    private static void ValidarFrete(NotaFiscalRequest req)
    {
        if (req.ValorFrete < 0 || req.Itens.Any(i => i.ValorFrete < 0))
            throw new ArgumentException("valorFrete nao pode ser negativo.", nameof(req));

        var temFrete = req.ValorFrete > 0 || req.Itens.Any(i => i.ValorFrete > 0);
        if (temFrete && req.ModalidadeFrete == ModalidadeFrete.SemTransporte)
            throw new ArgumentException(
                "Ha valor de frete, mas modalidadeFrete indica operacao sem ocorrencia de transporte (9).",
                nameof(req));
    }

    private static void ValidarVolumes(NotaFiscalRequest req)
    {
        var temDadosVolume = req.QuantidadeVolumes.HasValue ||
            !string.IsNullOrWhiteSpace(req.Especie) ||
            !string.IsNullOrWhiteSpace(req.Marca);

        if (temDadosVolume && req.QuantidadeVolumes is null)
            throw new ArgumentException(
                "quantidadeVolumes e obrigatoria quando os dados do grupo de volumes forem informados.",
                nameof(req));

        if (req.QuantidadeVolumes is { } quantidade &&
            (quantidade <= 0 || quantidade > 999_999_999_999_999L))
            throw new ArgumentException(
                "quantidadeVolumes deve ser um inteiro maior que zero com no maximo 15 digitos.",
                nameof(req));

        if (req.Especie is { Length: > 60 })
            throw new ArgumentException("especie deve ter no maximo 60 caracteres.", nameof(req));

        if (req.Marca is { Length: > 60 })
            throw new ArgumentException("marca deve ter no maximo 60 caracteres.", nameof(req));

        ValidarPeso(req.PesoLiquido, "pesoLiquido");
        ValidarPeso(req.PesoBruto, "pesoBruto");
    }

    private static string SoDigitos(string? valor) =>
        string.IsNullOrEmpty(valor) ? "" : new string(valor.Where(char.IsDigit).ToArray());

    private static bool TemTransportadora(TransportadoraRequest? t) =>
        t is not null && (
            !string.IsNullOrWhiteSpace(t.Cnpj) || !string.IsNullOrWhiteSpace(t.Cpf) ||
            !string.IsNullOrWhiteSpace(t.Nome) || !string.IsNullOrWhiteSpace(t.InscricaoEstadual) ||
            !string.IsNullOrWhiteSpace(t.Endereco) || !string.IsNullOrWhiteSpace(t.Municipio) ||
            !string.IsNullOrWhiteSpace(t.Uf));

    private static bool TemVeiculo(VeiculoTransporteRequest? v) =>
        v is not null && (
            !string.IsNullOrWhiteSpace(v.Placa) || !string.IsNullOrWhiteSpace(v.Uf) ||
            !string.IsNullOrWhiteSpace(v.Rntc));

    private static void ValidarTransporte(NotaFiscalRequest req)
    {
        if (TemTransportadora(req.Transportadora))
        {
            var t = req.Transportadora!;
            var cnpj = SoDigitos(t.Cnpj);
            var cpf = SoDigitos(t.Cpf);

            if (cnpj.Length > 0 && cpf.Length > 0)
                throw new ArgumentException("transportadora: informe cnpj ou cpf, nao os dois.", nameof(req));
            if (cnpj.Length > 0 && cnpj.Length != 14)
                throw new ArgumentException("transportadora.cnpj deve ter 14 digitos.", nameof(req));
            if (cpf.Length > 0 && cpf.Length != 11)
                throw new ArgumentException("transportadora.cpf deve ter 11 digitos.", nameof(req));
            if (t.Nome is { } nome && (nome.Trim().Length < 2 || nome.Trim().Length > 60))
                throw new ArgumentException("transportadora.nome deve ter de 2 a 60 caracteres.", nameof(req));
            if (t.Endereco is { Length: > 60 })
                throw new ArgumentException("transportadora.endereco deve ter no maximo 60 caracteres.", nameof(req));
            if (t.Municipio is { Length: > 60 })
                throw new ArgumentException("transportadora.municipio deve ter no maximo 60 caracteres.", nameof(req));
            if (!string.IsNullOrWhiteSpace(t.Municipio) && string.IsNullOrWhiteSpace(t.Uf))
                throw new ArgumentException("transportadora.uf e obrigatoria quando o municipio for informado.", nameof(req));
            if (!string.IsNullOrWhiteSpace(t.Uf) && t.Uf.Trim().Length != 2)
                throw new ArgumentException("transportadora.uf deve ter 2 letras.", nameof(req));
        }

        if (TemVeiculo(req.Veiculo))
        {
            var v = req.Veiculo!;
            if (string.IsNullOrWhiteSpace(v.Placa) || string.IsNullOrWhiteSpace(v.Uf))
                throw new ArgumentException("veiculo.placa e veiculo.uf sao obrigatorias quando o veiculo for informado.", nameof(req));
            if (LimparPlaca(v.Placa).Length is < 7 or > 7)
                throw new ArgumentException("veiculo.placa deve ter 7 caracteres, sem hifen.", nameof(req));
            if (v.Uf.Trim().Length != 2)
                throw new ArgumentException("veiculo.uf deve ter 2 letras.", nameof(req));
            if (v.Rntc is { Length: > 20 })
                throw new ArgumentException("veiculo.rntc deve ter no maximo 20 caracteres.", nameof(req));
        }
    }

    private static string LimparPlaca(string? placa) =>
        string.IsNullOrEmpty(placa)
            ? ""
            : new string(placa.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static XElement? GerarTransportadora(TransportadoraRequest? t)
    {
        if (!TemTransportadora(t)) return null;

        var cnpj = SoDigitos(t!.Cnpj);
        var cpf = SoDigitos(t.Cpf);
        var ie = t.InscricaoEstadual?.Trim();

        return new XElement(Ns + "transporta",
            cnpj.Length > 0 ? new XElement(Ns + "CNPJ", cnpj) : null,
            cnpj.Length == 0 && cpf.Length > 0 ? new XElement(Ns + "CPF", cpf) : null,
            Opcional("xNome", t.Nome?.Trim()),
            string.IsNullOrEmpty(ie)
                ? null
                : new XElement(Ns + "IE", ie.Equals("ISENTO", StringComparison.OrdinalIgnoreCase) ? "ISENTO" : SoDigitos(ie)),
            Opcional("xEnder", t.Endereco?.Trim()),
            Opcional("xMun", t.Municipio?.Trim()),
            Opcional("UF", t.Uf?.Trim().ToUpperInvariant()));
    }

    private static XElement? GerarVeiculo(VeiculoTransporteRequest? v)
    {
        if (!TemVeiculo(v)) return null;

        return new XElement(Ns + "veicTransp",
            new XElement(Ns + "placa", LimparPlaca(v!.Placa)),
            new XElement(Ns + "UF", v.Uf!.Trim().ToUpperInvariant()),
            Opcional("RNTC", v.Rntc?.Trim()));
    }

    private static void ValidarPeso(decimal? peso, string campo)
    {
        if (peso is { } valor &&
            (valor < 0 || valor > 999_999_999_999.999m || valor != decimal.Round(valor, 3)))
            throw new ArgumentException(
                $"{campo} deve ser maior ou igual a zero, ter no maximo 3 casas decimais e caber no formato da NF-e.",
                nameof(peso));
    }

    private static XElement? GerarVolume(NotaFiscalRequest req)
    {
        var temQuantidade = req.QuantidadeVolumes.HasValue;
        var temEspecie = !string.IsNullOrWhiteSpace(req.Especie);
        var temMarca = !string.IsNullOrWhiteSpace(req.Marca);
        var temPesos = req.PesoLiquido.HasValue || req.PesoBruto.HasValue;
        if (!temQuantidade && !temEspecie && !temMarca && !temPesos)
            return null;

        return new XElement(Ns + "vol",
            temQuantidade ? new XElement(Ns + "qVol", req.QuantidadeVolumes!.Value.ToString(CultureInfo.InvariantCulture)) : null,
            temEspecie ? new XElement(Ns + "esp", req.Especie) : null,
            temMarca ? new XElement(Ns + "marca", req.Marca) : null,
            req.PesoLiquido is { } pesoLiquido ? new XElement(Ns + "pesoL", Num(pesoLiquido, 3)) : null,
            req.PesoBruto is { } pesoBruto ? new XElement(Ns + "pesoB", Num(pesoBruto, 3)) : null);
    }

    /// <summary>Fatura e duplicatas efetivas (com os valores padrao preenchidos).</summary>
    public sealed record Cobranca(
        string NumeroFatura, decimal ValorOriginal, decimal ValorDesconto, decimal ValorLiquido,
        IReadOnlyList<(string Numero, DateOnly Vencimento, decimal Valor)> Duplicatas);

    /// <summary>
    /// Confere fatura/duplicatas pelas regras da NF-e e devolve os problemas
    /// (lista vazia = ok). Regras: ate 120 duplicatas; nDup "001", "002"...
    /// em sequencia; vencimento valido, nao anterior a emissao e em ordem
    /// crescente; valores positivos; soma das duplicatas = vLiq da fatura;
    /// vLiq = vOrig - vDesc.
    /// </summary>
    public static List<string> ValidarCobranca(NotaFiscalRequest req)
    {
        var erros = new List<string>();
        MontarCobranca(req, erros);
        return erros;
    }

    private static Cobranca? MontarCobranca(NotaFiscalRequest req, List<string> erros)
    {
        var dups = req.Duplicatas ?? new List<DuplicataRequest>();
        if (req.Fatura is null && dups.Count == 0) return null;

        if (dups.Count > 120)
            erros.Add($"duplicatas: no maximo 120 parcelas (recebidas {dups.Count}).");

        var emissao = DateOnly.FromDateTime(req.DataEmissao.DateTime);
        var lista = new List<(string, DateOnly, decimal)>();
        DateOnly? anterior = null;
        for (var i = 0; i < dups.Count; i++)
        {
            var d = dups[i];
            var esperado = (i + 1).ToString("000", CultureInfo.InvariantCulture);
            var numero = string.IsNullOrWhiteSpace(d.Numero) ? esperado : d.Numero.Trim();
            if (numero != esperado)
                erros.Add($"duplicatas[{i + 1}]: numero '{numero}' invalido. As parcelas devem ser numeradas " +
                          $"001, 002, 003... em sequencia (esperado '{esperado}'); deixe vazio para numerar automaticamente.");

            var venc = d.VencimentoData();
            if (venc is null)
                erros.Add($"duplicatas[{i + 1}]: vencimento '{d.Vencimento}' invalido. Use AAAA-MM-DD ou DD/MM/AAAA.");
            else
            {
                if (venc.Value < emissao)
                    erros.Add($"duplicatas[{i + 1}]: vencimento {venc:dd/MM/yyyy} anterior a emissao ({emissao:dd/MM/yyyy}).");
                if (anterior is { } a && venc.Value < a)
                    erros.Add($"duplicatas[{i + 1}]: vencimentos devem estar em ordem crescente.");
                anterior = venc;
            }

            if (d.Valor <= 0)
                erros.Add($"duplicatas[{i + 1}]: valor deve ser maior que zero.");

            lista.Add((numero, venc ?? emissao, Math.Round(d.Valor, 2)));
        }

        var somaDups = lista.Sum(x => x.Item3);
        var f = req.Fatura;
        var nFat = string.IsNullOrWhiteSpace(f?.Numero)
            ? req.Numero.ToString(CultureInfo.InvariantCulture)
            : f!.Numero!.Trim();
        if (nFat.Length > 60) erros.Add("fatura.numero: no maximo 60 caracteres.");

        var vDesc = Math.Round(f?.ValorDesconto ?? 0m, 2);
        var vOrig = Math.Round(f?.ValorOriginal ?? (f?.ValorLiquido is { } l ? l + vDesc : somaDups), 2);
        var vLiq = Math.Round(f?.ValorLiquido ?? vOrig - vDesc, 2);

        if (vOrig < 0 || vDesc < 0 || vLiq < 0)
            erros.Add("fatura: valores nao podem ser negativos.");
        if (vLiq != vOrig - vDesc)
            erros.Add($"fatura: valorLiquido ({vLiq:0.00}) deve ser valorOriginal ({vOrig:0.00}) - valorDesconto ({vDesc:0.00}).");
        if (lista.Count > 0 && somaDups != vLiq)
            erros.Add($"duplicatas: a soma das parcelas ({somaDups:0.00}) deve ser igual ao valor liquido da fatura ({vLiq:0.00}).");

        return new Cobranca(nFat, vOrig, vDesc, vLiq, lista);
    }

    /// <summary>Grupo cobr (fatura + duplicatas), entre transp e pag. Null quando nao informado.</summary>
    private static XElement? GerarCobranca(NotaFiscalRequest req)
    {
        var erros = new List<string>();
        var c = MontarCobranca(req, erros);
        if (erros.Count > 0) throw new ArgumentException(string.Join(" ", erros));
        if (c is null) return null;

        var cobr = new XElement(Ns + "cobr",
            new XElement(Ns + "fat",
                new XElement(Ns + "nFat", c.NumeroFatura),
                new XElement(Ns + "vOrig", Num(c.ValorOriginal, 2)),
                new XElement(Ns + "vDesc", Num(c.ValorDesconto, 2)),
                new XElement(Ns + "vLiq", Num(c.ValorLiquido, 2))));
        foreach (var (numero, venc, valor) in c.Duplicatas)
            cobr.Add(new XElement(Ns + "dup",
                new XElement(Ns + "nDup", numero),
                new XElement(Ns + "dVenc", venc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement(Ns + "vDup", Num(valor, 2))));
        return cobr;
    }

    private static XElement GerarPagamento(NotaFiscalRequest req)
    {
        var pag = new XElement(Ns + "pag");
        var vNF = CalcularVNF(req);

        decimal totalPago;
        if (req.Pagamentos.Count == 0)
        {
            // Sem pagamento informado assume o total da nota a vista em dinheiro.
            // Usa o mesmo vNF (com vOutro) para nunca abrir diferenca com o total.
            totalPago = vNF;
            pag.Add(new XElement(Ns + "detPag",
                new XElement(Ns + "tPag", "01"),
                new XElement(Ns + "vPag", Num(vNF, 2))));
        }
        else
        {
            totalPago = 0;
            foreach (var p in req.Pagamentos)
            {
                totalPago += p.Valor;
                pag.Add(new XElement(Ns + "detPag",
                    new XElement(Ns + "tPag", p.Meio),
                    new XElement(Ns + "vPag", Num(p.Valor, 2))));
            }
        }

        // Rejeicao 866: quando a soma dos pagamentos excede o total da nota, a
        // NF-e EXIGE o troco (vTroco = vPag - vNF) ao final do grupo pag. Sem
        // isso a SEFAZ rejeita mesmo com os valores corretos. Emitimos aqui para
        // que uma sobra de pagamento nunca derrube a autorizacao.
        var troco = decimal.Round(totalPago - vNF, 2, MidpointRounding.AwayFromZero);
        if (troco > 0)
            pag.Add(new XElement(Ns + "vTroco", Num(troco, 2)));

        return pag;
    }

    private static XElement? GerarInfoAdicional(NotaFiscalRequest req, TipoAmbiente ambiente)
    {
        var complemento = req.InformacoesComplementares;

        if (string.IsNullOrWhiteSpace(complemento))
            return null;

        return new XElement(Ns + "infAdic",
            new XElement(Ns + "infCpl", complemento));
    }

    // --- Utilitarios ---

    /// <summary>
    /// Telefone no schema da NF-e: so digitos, de 6 a 14 (TFone). O campo e opcional,
    /// entao um numero fora do padrao (ex.: dois telefones colados) e omitido em vez de
    /// derrubar a nota com a rejeicao 225 (falha no schema).
    /// </summary>
    internal static XElement? Fone(string? telefone)
    {
        var digitos = SoDigitos(telefone ?? string.Empty);
        return digitos.Length is >= 6 and <= 14 ? new XElement(Ns + "fone", digitos) : null;
    }

    private static XElement? Opcional(string nome, string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : new XElement(Ns + nome, valor);

    /// <summary>Numero decimal com ponto e casas fixas, em cultura invariante.</summary>
    private static string Num(decimal valor, int casas) =>
        valor.ToString("F" + casas, CultureInfo.InvariantCulture);

    /// <summary>
    /// Data no formato UTC com offset. A NF-e exige dhEmi com fuso; usamos o fuso
    /// oficial de Brasilia (-03:00) para o emitente.
    /// </summary>
    private static string FormatarDataHora(DateTimeOffset data, string uf)
    {
        var brasilia = data.ToOffset(TimeSpan.FromHours(-3));
        return brasilia.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string SortearCodigoNumerico()
    {
        // cNF: 8 digitos aleatorios. Evita cair na regra que proibe cNF == nNF
        // ao ser reprocessado pelo chamador.
        var n = RandomNumberGenerator.GetInt32(0, 100_000_000);
        return n.ToString("D8");
    }

    /// <summary>
    /// Serializa sem BOM e sem reindentacao adicional. A SEFAZ exige o XML numa
    /// unica linha sem quebras entre tags; producao normalmente concatena.
    /// </summary>
    private static string SerializarSemBom(XDocument doc)
    {
        // SaveOptions.DisableFormatting mantem o XML sem indentacao, o que evita
        // surpresas no digest apos a assinatura.
        return doc.Declaration is null
            ? doc.ToString(SaveOptions.DisableFormatting)
            : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
    }
}
