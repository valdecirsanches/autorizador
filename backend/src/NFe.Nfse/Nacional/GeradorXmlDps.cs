using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Modelos;
using NFe.Nfse.Contratos;

namespace NFe.Nfse.Nacional;

/// <summary>
/// Monta o XML da DPS (Declaracao de Prestacao de Servico) no layout 1.01 do
/// Sistema Nacional NFS-e a partir de <see cref="DpsDados"/>. A ordem dos
/// elementos segue o XSD: qualquer inversao e rejeitada pelo validador da SEFIN.
/// Cobre: prestador CNPJ, tomador CNPJ/CPF nacional (ou nao identificado),
/// servico unico, ISSQN tributavel/imune/exportacao, retencao pelo tomador,
/// Simples Nacional e regime normal.
/// </summary>
public sealed class GeradorXmlDps
{
    public const string Versao = "1.01";
    private static readonly XNamespace Ns = NfseNacionalEndpoints.NamespaceDps;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan FusoBrasilia = TimeSpan.FromHours(-3);

    /// <summary>Gera o XML da DPS e devolve tambem o Id (45 caracteres) usado na assinatura.</summary>
    public (string Xml, string Id) Gerar(DpsDados d)
    {
        ArgumentNullException.ThrowIfNull(d);
        Validar(d);

        var r = d.Request;
        var prest = d.Prestador;
        var cfg = d.Config;

        var cnpj = Digitos(prest.Cnpj);
        var cLocEmi = Digitos(cfg.CodigoMunicipioEmissao ?? prest.CodigoMunicipio);
        var serie = Digitos(d.Serie).PadLeft(5, '0');
        var id = MontarId(cLocEmi, cnpj, serie, d.NumeroDps);

        var dhEmi = d.DataEmissao.ToOffset(FusoBrasilia);
        var dCompet = d.DataCompetencia;

        var infDps = new XElement(Ns + "infDPS",
            new XAttribute("Id", id),
            El("tpAmb", ((int)d.Ambiente).ToString(Inv)),
            El("dhEmi", dhEmi.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv)),
            El("verAplic", Truncar(cfg.VersaoAplicativo, 20)),
            El("serie", serie),
            El("nDPS", d.NumeroDps.ToString(Inv)),
            El("dCompet", dCompet.ToString("yyyy-MM-dd", Inv)),
            El("tpEmit", "1"),
            El("cLocEmi", cLocEmi),
            Prestador(prest, cfg, cnpj),
            Tomador(r.Tomador),
            Servico(r, prest, cfg, cLocEmi),
            Valores(r.Valores));

        var dps = new XElement(Ns + "DPS",
            new XAttribute("versao", Versao),
            infDps);

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), dps);
        var sb = new StringBuilder();
        using (var w = new StringWriter(sb, Inv))
            doc.Save(w, SaveOptions.DisableFormatting);

        // StringWriter forca encoding utf-16 na declaracao; corrigimos para UTF-8.
        var xml = sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
        return (xml, id);
    }

    /// <summary>
    /// Id da DPS: "DPS" + cLocEmi(7) + tpInsc(1; 2 = CNPJ) + inscricao(14) +
    /// serie(5) + nDPS(15) = 45 caracteres.
    /// </summary>
    public static string MontarId(string cLocEmi, string cnpj, string serie, long nDps)
        => "DPS" + cLocEmi.PadLeft(7, '0') + "2" + cnpj.PadLeft(14, '0')
           + serie.PadLeft(5, '0') + nDps.ToString(Inv).PadLeft(15, '0');

    // ---------------------------------------------------------------- grupos

    private static XElement Prestador(Domain.Entities.Empresa p, Domain.Entities.EmpresaNfseConfig cfg, string cnpj)
    {
        var regTrib = new XElement(Ns + "regTrib",
            El("opSimpNac", ((int)cfg.OpSimpNac).ToString(Inv)));

        // regApTribSN so existe (e e obrigatorio) para optantes ME/EPP.
        if (cfg.OpSimpNac == OpcaoSimplesNacional.OptanteMeEpp)
            regTrib.Add(El("regApTribSN", Math.Clamp(cfg.RegApTribSN, 1, 3).ToString(Inv)));

        regTrib.Add(El("regEspTrib", ((int)cfg.RegEspTrib).ToString(Inv)));

        var prest = new XElement(Ns + "prest", El("CNPJ", cnpj));
        var im = Digitos(p.InscricaoMunicipal);
        if (im.Length > 0) prest.Add(El("IM", Truncar(im, 15)));
        prest.Add(regTrib);
        return prest;
    }

    private static XElement? Tomador(TomadorNfseRequest? t)
    {
        if (t is null) return null;

        var doc = Digitos(t.Documento);
        var nif = Limpar(t.Nif);
        if (doc.Length == 0 && nif.Length == 0 && string.IsNullOrWhiteSpace(t.Nome))
            return null; // tomador nao identificado: grupo omitido

        var toma = new XElement(Ns + "toma");
        if (doc.Length == 14) toma.Add(El("CNPJ", doc));
        else if (doc.Length == 11) toma.Add(El("CPF", doc));
        else if (nif.Length > 0) toma.Add(El("NIF", Truncar(nif, 40)));
        else toma.Add(El("cNaoNIF", "0"));

        var im = Digitos(t.InscricaoMunicipal);
        if (im.Length > 0) toma.Add(El("IM", Truncar(im, 15)));

        toma.Add(El("xNome", Truncar(Limpar(t.Nome), 300)));

        var cMun = Digitos(t.CodigoMunicipio);
        var cep = Digitos(t.Cep);
        if (cMun.Length == 7 && !string.IsNullOrWhiteSpace(t.Logradouro))
        {
            var end = new XElement(Ns + "end",
                new XElement(Ns + "endNac",
                    El("cMun", cMun),
                    El("CEP", cep.PadLeft(8, '0'))),
                El("xLgr", Truncar(Limpar(t.Logradouro), 255)),
                El("nro", Truncar(Limpar(string.IsNullOrWhiteSpace(t.Numero) ? "S/N" : t.Numero), 60)));
            if (!string.IsNullOrWhiteSpace(t.Complemento))
                end.Add(El("xCpl", Truncar(Limpar(t.Complemento), 156)));
            end.Add(El("xBairro", Truncar(Limpar(string.IsNullOrWhiteSpace(t.Bairro) ? "CENTRO" : t.Bairro), 60)));
            toma.Add(end);
        }

        var fone = Digitos(t.Telefone);
        if (fone.Length is >= 6 and <= 20) toma.Add(El("fone", fone));
        if (!string.IsNullOrWhiteSpace(t.Email)) toma.Add(El("email", Truncar(t.Email.Trim(), 80)));

        return toma;
    }

    private static XElement Servico(NfseRequest r, Domain.Entities.Empresa p,
        Domain.Entities.EmpresaNfseConfig cfg, string cLocEmi)
    {
        var s = r.Servico;
        var cLocPrest = Digitos(s.CodigoMunicipioPrestacao);
        if (cLocPrest.Length != 7) cLocPrest = cLocEmi;

        var cServ = new XElement(Ns + "cServ", El("cTribNac", Digitos(s.CodigoTributacaoNacional).PadLeft(6, '0')));
        // cTribMun e um codigo de 3 digitos parametrizado pelo municipio no Sistema
        // Nacional. Valores no formato do item da LC 116 (ex.: "1.01") nao sao
        // cTribMun: enviados, viram "101" e a SEFIN rejeita com E0314.
        var cTribMunBruto = s.CodigoTributacaoMunicipal?.Trim() ?? string.Empty;
        if (System.Text.RegularExpressions.Regex.IsMatch(cTribMunBruto, "^[0-9]{1,3}$"))
            cServ.Add(El("cTribMun", cTribMunBruto.PadLeft(3, '0')));
        cServ.Add(El("xDescServ", Truncar(Limpar(s.Descricao), 2000)));
        var nbs = Digitos(s.CodigoNbs);
        if (nbs.Length == 9) cServ.Add(El("cNBS", nbs));

        var serv = new XElement(Ns + "serv",
            new XElement(Ns + "locPrest", El("cLocPrestacao", cLocPrest)),
            cServ);

        if (!string.IsNullOrWhiteSpace(r.InformacoesComplementares))
            serv.Add(new XElement(Ns + "infoCompl",
                El("xInfComp", Truncar(Limpar(r.InformacoesComplementares), 2000))));

        return serv;
    }

    private static XElement Valores(ValoresNfseRequest v)
    {
        var valores = new XElement(Ns + "valores",
            new XElement(Ns + "vServPrest", El("vServ", Dec(v.ValorServico))));

        if (v.DescontoIncondicionado > 0 || v.DescontoCondicionado > 0)
        {
            var desc = new XElement(Ns + "vDescCondIncond");
            if (v.DescontoIncondicionado > 0) desc.Add(El("vDescIncond", Dec(v.DescontoIncondicionado)));
            if (v.DescontoCondicionado > 0) desc.Add(El("vDescCond", Dec(v.DescontoCondicionado)));
            valores.Add(desc);
        }

        if (v.Deducoes > 0)
            valores.Add(new XElement(Ns + "vDedRed", El("vDR", Dec(v.Deducoes))));

        // ---- trib ----
        var tribMun = new XElement(Ns + "tribMun", El("tribISSQN", ((int)v.TributacaoIssqn).ToString(Inv)));
        if (v.TributacaoIssqn == TributacaoIssqn.OperacaoTributavel)
        {
            if (v.AliquotaIss > 0) tribMun.Add(El("pAliq", Dec(v.AliquotaIss)));
            tribMun.Add(El("tpRetISSQN", ((int)v.RetencaoIssqn).ToString(Inv)));
        }
        else
        {
            // Imunidade/nao incidencia/exportacao: sem aliquota; retencao sempre 1.
            if (v.TributacaoIssqn == TributacaoIssqn.Imunidade) tribMun.Add(El("tpImunidade", "0"));
            tribMun.Add(El("tpRetISSQN", "1"));
        }

        var trib = new XElement(Ns + "trib", tribMun);

        var federal = TributosFederais(v);
        if (federal is not null) trib.Add(federal);

        var totTrib = new XElement(Ns + "totTrib");
        if (v.PercentualTotalTributos is { } pTot && pTot > 0)
            totTrib.Add(new XElement(Ns + "pTotTrib",
                El("pTotTribFed", Dec(pTot)),
                El("pTotTribEst", "0.00"),
                El("pTotTribMun", "0.00")));
        else
            totTrib.Add(El("indTotTrib", "0"));
        trib.Add(totTrib);

        valores.Add(trib);
        return valores;
    }

    private static XElement? TributosFederais(ValoresNfseRequest v)
    {
        var temPisCofins = v.ValorPis > 0 || v.ValorCofins > 0;
        var temRet = v.ValorInss > 0 || v.ValorIr > 0 || v.ValorCsll > 0;
        if (!temPisCofins && !temRet) return null;

        var fed = new XElement(Ns + "tribFed");
        if (temPisCofins)
        {
            var pc = new XElement(Ns + "piscofins", El("CST", "01"));
            pc.Add(El("vBCPisCofins", Dec(v.ValorServico)));
            if (v.ValorPis > 0) pc.Add(El("vPis", Dec(v.ValorPis)));
            if (v.ValorCofins > 0) pc.Add(El("vCofins", Dec(v.ValorCofins)));
            pc.Add(El("tpRetPisCofins", "1"));
            fed.Add(pc);
        }
        if (v.ValorInss > 0) fed.Add(El("vRetCP", Dec(v.ValorInss)));
        if (v.ValorIr > 0) fed.Add(El("vRetIRRF", Dec(v.ValorIr)));
        if (v.ValorCsll > 0) fed.Add(El("vRetCSLL", Dec(v.ValorCsll)));
        return fed;
    }

    // ------------------------------------------------------------ validacao

    /// <summary>Valida o minimo necessario ANTES de reservar numeracao/assinar.</summary>
    public static IReadOnlyList<string> ValidarRequest(NfseRequest r, Domain.Entities.Empresa prest,
        Domain.Entities.EmpresaNfseConfig cfg)
    {
        var erros = new List<string>();
        if (Digitos(prest.Cnpj).Length != 14) erros.Add("Empresa sem CNPJ valido (14 digitos).");
        var cMunEmi = Digitos(cfg.CodigoMunicipioEmissao ?? prest.CodigoMunicipio);
        if (cMunEmi.Length != 7)
            erros.Add(
                $"Codigo IBGE do municipio emissor deve ter 7 digitos, mas veio " +
                $"\"{cMunEmi}\" ({cMunEmi.Length} digito(s)). Envie-o na requisicao em " +
                "configuracao.codigoMunicipioEmissao (ex.: 3534401) ou preencha o cadastro " +
                "da empresa / Configuracao NFS-e no painel.");
        if (Digitos(r.Servico.CodigoTributacaoNacional).Length != 6)
            erros.Add("servico.codigoTributacaoNacional deve ter 6 digitos (ex.: 010701).");
        if (string.IsNullOrWhiteSpace(r.Servico.Descricao))
            erros.Add("servico.descricao e obrigatoria.");
        if (r.Valores.ValorServico <= 0)
            erros.Add("valores.valorServico deve ser maior que zero.");
        if (r.Valores.TributacaoIssqn == TributacaoIssqn.OperacaoTributavel && r.Valores.AliquotaIss < 0)
            erros.Add("valores.aliquotaIss nao pode ser negativa.");

        var t = r.Tomador;
        if (t is not null)
        {
            var doc = Digitos(t.Documento);
            if (doc.Length is not (0 or 11 or 14))
                erros.Add("tomador.documento deve ter 11 (CPF) ou 14 (CNPJ) digitos.");
            if (doc.Length > 0 && string.IsNullOrWhiteSpace(t.Nome))
                erros.Add("tomador.nome e obrigatorio quando ha documento.");
            var cMun = Digitos(t.CodigoMunicipio);
            if (cMun.Length is not (0 or 7))
                erros.Add(
                    $"tomador.codigoMunicipio (enviado na requisicao) deve ter 7 digitos ou ficar vazio, " +
                    $"mas veio \"{cMun}\" ({cMun.Length} digito(s)).");
        }
        return erros;
    }

    private static void Validar(DpsDados d)
    {
        var erros = ValidarRequest(d.Request, d.Prestador, d.Config);
        if (erros.Count > 0)
            throw new ArgumentException("DPS invalida: " + string.Join(" | ", erros));
        if (d.NumeroDps <= 0 || d.NumeroDps > 999_999_999_999_999)
            throw new ArgumentException("nDPS fora do intervalo permitido.");
    }

    // ------------------------------------------------------------- helpers

    private static XElement El(string nome, string valor) => new(Ns + nome, valor);

    private static string Dec(decimal v) => v.ToString("0.00", Inv);

    private static string Digitos(string? s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\D", "");

    private static string Limpar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Remove controles e normaliza espacos; o XSD nao aceita quebras/tabs em xNome etc.
        var t = Regex.Replace(s, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string Truncar(string s, int max) => s.Length <= max ? s : s[..max];
}
