using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Nfse.Contratos;
using NFe.Nfse.Nacional;

namespace NFe.Nfse;

/// <summary>Integração eGoverneISS de Osasco pelo método legado Emitir.</summary>
public sealed class ProvedorOsasco : INfseProvedor
{
    // O eGoverneISS usa o mesmo endpoint em produção e homologação; a tag
    // Homologacao no DTO controla o ambiente, conforme ACBrNFSeXServicos.ini.
    public const string UrlProducao = "https://nfe.osasco.sp.gov.br/EissnfeWebServices/NotaFiscalEletronica.svc";
    public const string UrlHomologacao = UrlProducao;

    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Ws = "http://tempuri.org/";
    private static readonly XNamespace Mensagem = "http://schemas.datacontract.org/2004/07/Eissnfe.Negocio.WebServices.Mensagem";
    private static readonly XNamespace Prestador = "http://schemas.datacontract.org/2004/07/Eissnfe.Dominio.DataTransferObject.Prestador";
    private static readonly XNamespace Contribuinte = "http://schemas.datacontract.org/2004/07/Eissnfe.Dominio.DataTransferObject.Contribuinte";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpMtlsClientFactory _http;

    public ProvedorOsasco(HttpMtlsClientFactory http) => _http = http;

    public PadraoNfse Padrao => PadraoNfse.Osasco;

    public (string Xml, string Id) GerarXml(DpsDados d)
    {
        if (string.IsNullOrWhiteSpace(d.Config.OsascoChaveAutenticacao))
            throw new NfseProvedorException("Informe a Chave de Autenticação de NFS-e de Osasco na configuração da empresa.");
        var numero = d.NumeroDps.ToString(Inv);
        var docTomador = Digitos(d.Request.Tomador?.Documento);
        var v = d.Request.Valores;
        var servico = d.Request.Servico;
        ValidarEnderecoPrestacao(servico);
        var codigoMunicipio = !string.IsNullOrWhiteSpace(servico.CodigoCidadeIBGEPrestacaoServico)
            ? servico.CodigoCidadeIBGEPrestacaoServico
            : !string.IsNullOrWhiteSpace(servico.CodigoMunicipioPrestacao)
                ? servico.CodigoMunicipioPrestacao
                : d.Prestador.CodigoMunicipio;
        var municipio = Digitos(codigoMunicipio);
        var detalhes = string.IsNullOrWhiteSpace(d.Request.InformacoesComplementares)
            ? servico.Descricao
            : $"{servico.Descricao} | {d.Request.InformacoesComplementares}";

        var nota = new XElement(Prestador + "NotaFiscal_RTC_DTO",
            // Manual de Osasco: a aliquota so pode ser diferente de 0.00 para emissor
            // enquadrado no Simples Nacional; nos demais casos o sistema da prefeitura define.
            new XElement(Prestador + "Aliquota",
                Decimal(d.Config.OpSimpNac == OpcaoSimplesNacional.NaoOptante ? 0m : v.AliquotaIss)),
            Optional("Atividade", servico.CodigoTributacaoMunicipal ?? servico.ItemListaServico ?? servico.CodigoTributacaoNacional),
            Optional("BairroPrestacaoServico", servico.BairroPrestacaoServico),
            Optional("CEPPrestacaoServico", Digitos(servico.CepPrestacaoServico)),
            Optional("CidadePrestacaoServico", servico.CidadePrestacaoServico),
            Optional("CodigoCidadeIBGEPrestacaoServico", municipio),
            Optional("ComplementoPrestacaoServico", servico.ComplementoPrestacaoServico),
            new XElement(Prestador + "DataRecibo", d.DataEmissao.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv)),
            Optional("EnderecoPrestacaoServico", servico.EnderecoPrestacaoServico),
            Optional("EstadoPrestacaoServico", servico.EstadoPrestacaoServico),
            new XElement(Prestador + "Homologacao", XmlBool(d.Ambiente == TipoAmbiente.Homologacao)),
            new XElement(Prestador + "Identificador", $"{d.Serie}-{numero}"),
            Optional("InformacoesAdicionais", detalhes),
            new XElement(Prestador + "NotificarTomadorPorEmail", XmlBool(false)),
            Optional("NumeroPrestacaoServico", servico.NumeroPrestacaoServico),
            new XElement(Prestador + "NumeroRecibo", Decimal(d.NumeroDps)),
            Optional("PaisPrestacaoServico", string.IsNullOrWhiteSpace(servico.PaisPrestacaoServico)
                ? "BRASIL" : servico.PaisPrestacaoServico.Trim()),
            new XElement(Prestador + "SimplesNacional", XmlBool(d.Config.OpSimpNac != OpcaoSimplesNacional.NaoOptante)),
            new XElement(Prestador + "SubstituicaoTributaria", XmlBool(v.RetencaoIssqn != TipoRetencaoIssqn.NaoRetido)),
            CriarTomador(d, docTomador),
            new XElement(Prestador + "Valor", Decimal(v.ValorServico)),
            Optional("ValorCSLL", Decimal(v.ValorCsll)),
            Optional("ValorCofins", Decimal(v.ValorCofins)),
            new XElement(Prestador + "ValorDeducao", Decimal(v.Deducoes)),
            Optional("ValorINSS", Decimal(v.ValorInss)),
            Optional("ValorIR", Decimal(v.ValorIr)),
            Optional("ValorPisPasep", Decimal(v.ValorPis)),
            Optional("nrExercicioReferencia", d.DataCompetencia.Year.ToString(Inv)),
            Optional("nrMesReferencia", d.DataCompetencia.Month.ToString(Inv)),
            Optional("ClassTrib", servico.ClassTrib),
            Optional("CodigoCST", servico.CodigoCst),
            Optional("CodigoNBS", servico.CodigoNbs),
            servico.IdLocalIncidencia is null ? null : new XElement(Prestador + "IdLocalIncidencia", servico.IdLocalIncidencia.Value),
            new XElement(Prestador + "IndOperacao", servico.IndOperacao ?? string.Empty));

        var request = new XElement(Mensagem + "NotaFiscal_RTC", nota.Elements());
        var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null), request);
        return (envelope.ToString(SaveOptions.DisableFormatting), $"{d.Serie}-{numero}");
    }

    // Osasco valida a ChaveAutenticacao do DTO e não exige assinatura XML no contrato RTC.
    public string Assinar(string xml, string id, X509Certificate2 certificado) => xml;

    public async Task<ResultadoEmissaoNfse> EmitirAsync(string xmlAssinado, EmpresaNfseConfig config,
        TipoAmbiente ambiente, X509Certificate2 certificado, CancellationToken ct = default)
    {
        var url = Url(ambiente);
        var chave = ExigirChave(config);
        var dados = XElement.Parse(xmlAssinado, LoadOptions.PreserveWhitespace);
        dados = InserirChaveAutenticacao(dados, chave);
        var body = MontarEmitirLegado(dados);
        var (response, raw, ms) = await SoapAsync(url, "Emitir", body, certificado, chave, ct);
        return InterpretarEmissao(response, raw, ms, ambiente);
    }

    /// <summary>
    /// Usa Emitir/NotaFiscal do eGoverneISS. Mantém IndOperacao porque o XML real do
    /// ACBr eGoverneISS o envia, mesmo vazio; remove apenas os campos exclusivos do RTC.
    /// </summary>
    internal static XElement MontarEmitirLegado(XElement dadosRtc)
    {
        var nota = new XElement(Mensagem + "NotaFiscal",
            dadosRtc.Elements()
                .Where(e => !CamposRtcLegadoExclusivos.Contains(e.Name.LocalName))
                .Select(e => new XElement(e)));
        return new XElement(Ws + "Emitir", new XElement(Ws + "request", nota));
    }

    internal sealed record CorrelacaoRtc(string ClassTrib, string IndOperacao, string Nbs);

    private const string CodigoIbgeOsasco = "3534401";
    private static readonly string[] CamposRtc = ["ClassTrib", "CodigoCST", "CodigoNBS", "IdLocalIncidencia", "IndOperacao"];
    private static readonly string[] CamposRtcLegadoExclusivos = ["ClassTrib", "CodigoCST", "CodigoNBS", "IdLocalIncidencia"];

    private static string? ValorCampo(XElement dados, string nome) =>
        dados.Elements().FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim() is { Length: > 0 } valor ? valor : null;

    internal static bool PrecisaCorrelacaoRtc(XElement dados) =>
        ValorCampo(dados, "ClassTrib") is null || ValorCampo(dados, "CodigoNBS") is null || ValorCampo(dados, "IndOperacao") is null;

    /// <summary>
    /// O RTC_EmitirNFE de Osasco lança NullReferenceException quando os campos da reforma
    /// tributária faltam, então eles são completados com a correlação oficial da atividade.
    /// </summary>
    internal static XElement CompletarCamposRtc(XElement dados, IReadOnlyList<CorrelacaoRtc> correlacoes)
    {
        var classTrib = ValorCampo(dados, "ClassTrib");
        var nbs = ValorCampo(dados, "CodigoNBS");
        var indOperacao = ValorCampo(dados, "IndOperacao");

        if (classTrib is null || nbs is null || indOperacao is null)
        {
            var escolhida = correlacoes
                .Where(c => nbs is null || Digitos(c.Nbs) == Digitos(nbs))
                .Where(c => classTrib is null || c.ClassTrib == classTrib)
                .Where(c => indOperacao is null || c.IndOperacao == indOperacao)
                .OrderBy(c => c.ClassTrib == "000001" ? 0 : 1)
                .FirstOrDefault()
                ?? throw new NfseProvedorException(
                    $"Osasco não retornou NBS/ClassTrib compatível para a atividade {ValorCampo(dados, "Atividade")}. " +
                    "Informe servico.codigoNbs, servico.classTrib e servico.indOperacao.");
            classTrib ??= escolhida.ClassTrib;
            nbs = escolhida.Nbs;
            indOperacao ??= escolhida.IndOperacao;
        }

        var municipio = ValorCampo(dados, "CodigoCidadeIBGEPrestacaoServico");
        var localIncidencia = ValorCampo(dados, "IdLocalIncidencia")
            ?? (municipio is null || municipio == CodigoIbgeOsasco ? "0" : "3");
        var cst = ValorCampo(dados, "CodigoCST") ?? "000";

        var nota = new XElement(dados);
        foreach (var campo in nota.Elements().Where(e => CamposRtc.Contains(e.Name.LocalName)).ToList())
            campo.Remove();
        nota.Add(
            new XElement(Prestador + "ClassTrib", classTrib),
            new XElement(Prestador + "CodigoCST", cst),
            new XElement(Prestador + "CodigoNBS", nbs),
            new XElement(Prestador + "IdLocalIncidencia", localIncidencia),
            new XElement(Prestador + "IndOperacao", indOperacao));
        return nota;
    }

    internal static IReadOnlyList<CorrelacaoRtc> InterpretarCorrelacoes(XElement response) =>
        response.Descendants()
            .Where(e => e.Name.LocalName == "Correlacao")
            .Select(c => new CorrelacaoRtc(
                Find(c, "CodigoClassTrib")?.Value?.Trim() ?? "",
                Find(c, "CodigoIndOperacao")?.Value?.Trim() ?? "",
                Find(c, "CodigoNBS")?.Value?.Trim() ?? ""))
            .Where(c => c.ClassTrib.Length > 0 && c.IndOperacao.Length > 0 && c.Nbs.Length > 0)
            .ToList();

    private async Task<IReadOnlyList<CorrelacaoRtc>> PesquisarCorrelacoesRtcAsync(string atividade, string url,
        X509Certificate2 certificado, string chave, CancellationToken ct)
    {
        var body = new XElement(Ws + "RTC_PesquisarNbsClassTrib",
            new XElement(Ws + "request", new XElement(Mensagem + "CodigoAtividade", atividade)));
        var (response, _, _) = await SoapAsync(url, "RTC_PesquisarNbsClassTrib", body, certificado, chave, ct);
        var result = Find(response, "RTC_PesquisarNbsClassTribResult");
        if (result is not null && Bool(Find(result, "Erro")?.Value))
            throw new NfseProvedorException("Osasco recusou a consulta de NBS/ClassTrib: " + Find(result, "MensagemErro")?.Value?.Trim());
        return InterpretarCorrelacoes(response);
    }

    internal static ResultadoEmissaoNfse InterpretarEmissao(
        XElement response, string raw, long ms, TipoAmbiente ambiente)
    {
        var result = Find(response, "EmitirResult") ?? Find(response, "RTC_EmitirNFEResult");
        if (result is null)
            return FalhaEmissao(raw, "Resposta SOAP sem EmitirResult.", ms);

        var erro = Bool(Find(result, "Erro")?.Value);
        var gerada = Find(result, "NotaFiscalGerada");
        if (erro || gerada is null)
        {
            var mensagem = Find(result, "MensagemErro")?.Value?.Trim();
            return FalhaEmissao(raw, string.IsNullOrWhiteSpace(mensagem) ? "Osasco não retornou a NFS-e gerada." : mensagem, ms);
        }

        var numero = CampoDireto(gerada, "Numero")?.Value?.Trim();
        var verificador = CampoDireto(gerada, "Autenticador")?.Value?.Trim();
        if (ambiente == TipoAmbiente.Producao && EhAutenticadorHomologacao(verificador))
            return FalhaEmissao(raw,
                "O eGoverneISS respondeu HOMOLOGAÇÃO embora o app tenha solicitado produção. " +
                "A nota não será marcada como autorizada em produção. Confirme a configuração de ambiente antes de emitir novamente.", ms);

        var chave = CampoDireto(gerada, "ChaveAcesso")?.Value?.Trim();
        return new ResultadoEmissaoNfse(true,
            string.IsNullOrWhiteSpace(chave) ? numero : chave,
            numero,
            verificador,
            gerada.ToString(SaveOptions.DisableFormatting),
            "0",
            "NFS-e emitida em Osasco.",
            raw,
            null,
            ms);
    }

    public async Task<ResultadoConsultaNfse> ConsultarAsync(string chaveAcesso, EmpresaNfseConfig config,
        TipoAmbiente ambiente, X509Certificate2 certificado, CancellationToken ct = default)
    {
        var numero = Digitos(chaveAcesso);
        if (!decimal.TryParse(numero, NumberStyles.None, Inv, out var numeroNota))
            return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "501",
                "A consulta de Osasco requer o número da NFS-e, não uma chave alfanumérica.", "");

        var chave = ExigirChave(config);
        var request = new XElement(Ws + "request",
            new XElement(Mensagem + "ChaveAutenticacao", chave),
            new XElement(Mensagem + "NumeroNotaFinal", Decimal(numeroNota)),
            new XElement(Mensagem + "NumeroNotaInicial", Decimal(numeroNota)));
        var body = new XElement(Ws + "RTC_ConsultarNFE", request);
        var (response, raw, _) = await SoapAsync(Url(ambiente), "RTC_ConsultarNFE", body, certificado, chave, ct);
        return InterpretarConsulta(response, chaveAcesso, raw);
    }

    internal static ResultadoConsultaNfse InterpretarConsulta(XElement response, string chaveAcesso, string raw)
    {
        var result = Find(response, "RTC_ConsultarNFEResult");
        if (result is null)
            return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "ERR", "Resposta de consulta sem resultado.", raw);

        if (Bool(Find(result, "Erro")?.Value))
            return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "ERR",
                Find(result, "MensagemErro")?.Value?.Trim(), raw);

        var item = Find(result, "NotaFiscalConsultaDTO");
        if (item is null)
            return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "404", "NFS-e não encontrada em Osasco.", raw);

        var cancelada = !string.IsNullOrWhiteSpace(Find(item, "DataCancelamento")?.Value);
        return new ResultadoConsultaNfse(true, cancelada, chaveAcesso,
            CampoDireto(item, "Numero")?.Value?.Trim(), item.ToString(SaveOptions.DisableFormatting), "0",
            cancelada ? Find(item, "MotivoCancelamento")?.Value?.Trim() : "NFS-e encontrada.", raw);
    }

    public async Task<ResultadoEventoNfse> CancelarAsync(NotaServico nota, string codigoMotivo, string justificativa,
        int sequencia, EmpresaNfseConfig config, X509Certificate2 certificado, CancellationToken ct = default)
    {
        var chave = ExigirChave(config);
        var request = new XElement(Ws + "request",
            new XElement(Mensagem + "ChaveAutenticacao", chave),
            new XElement(Mensagem + "Homologacao", XmlBool(nota.Ambiente == TipoAmbiente.Homologacao)),
            new XElement(Mensagem + "Motivo", justificativa),
            new XElement(Mensagem + "NumeroNota", nota.NumeroNfse ?? nota.ChaveAcesso ?? ""),
            new XElement(Mensagem + "TipoCancelamento", TipoCancelamentoOsasco(codigoMotivo)));
        var body = new XElement(Ws + "Cancelar", request);
        var (response, raw, _) = await SoapAsync(Url(nota.Ambiente), "Cancelar", body, certificado, chave, ct);
        var result = Find(response, "CancelarResult");
        if (result is null)
            return new ResultadoEventoNfse(false, null, "ERR", "Resposta SOAP sem CancelarResult.", null, raw, null);

        var erro = Bool(Find(result, "Erro")?.Value);
        return new ResultadoEventoNfse(!erro, null, erro ? "ERR" : "0",
            Find(result, "MensagemErro")?.Value?.Trim() ?? (erro ? "Osasco rejeitou o cancelamento." : "Cancelamento confirmado."),
            SanitizarChave(body), raw, erro ? null : DateTime.UtcNow);
    }

    // Osasco só aceita 'N' (normal) ou 'S' (substituição); qualquer outro código vira cancelamento normal.
    internal static string TipoCancelamentoOsasco(string? codigoMotivo) =>
        string.Equals(codigoMotivo?.Trim(), "S", StringComparison.OrdinalIgnoreCase) ? "S" : "N";

    internal static string Url(TipoAmbiente ambiente) => ambiente switch
    {
        TipoAmbiente.Producao => UrlProducao,
        TipoAmbiente.Homologacao => UrlHomologacao,
        _ => throw new NfseProvedorException("Ambiente NFS-e inválido para Osasco.")
    };

    internal static string ExigirChave(EmpresaNfseConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.OsascoChaveAutenticacao))
            throw new NfseProvedorException("Informe a Chave de Autenticação de NFS-e de Osasco na configuração da empresa.");
        return config.OsascoChaveAutenticacao.Trim();
    }

    internal static XElement InserirChaveAutenticacao(XElement dados, string chave)
    {
        if (dados.Name.LocalName != "NotaFiscal_RTC")
            throw new NfseProvedorException("XML de emissão RTC de Osasco inválido.");

        var nota = new XElement(dados);
        foreach (var anterior in nota.Elements().Where(e => e.Name.LocalName == "ChaveAutenticacao").ToList())
            anterior.Remove();
        var primeiroCampoAposChave = nota.Elements().FirstOrDefault(e => e.Name.LocalName is
            "CidadePrestacaoServico" or "CodObra" or "CodigoCidadeIBGEPrestacaoServico" or
            "ComplementoPrestacaoServico" or "DataRecibo" or "DeduzirRepasse" or
            "EnderecoPrestacaoServico" or "EqptoRecibo" or "EstadoPrestacaoServico" or
            "Homologacao" or "Identificador" or "InformacoesAdicionais" or "NotaSubstituida" or
            "NotificarTomadorPorEmail" or "NumeroCDC" or "NumeroCei" or "NumeroContrato" or
            "NumeroPrestacaoServico" or "NumeroRecibo" or "PaisPrestacaoServico" or
            "ResponsavelTributario" or "SemIncidenciaISS" or "SimplesNacional" or
            "SubstituicaoTributaria" or "Tomador" or "TomadorEstrangeiro" or "Valor" or
            "ValorCSLL" or "ValorCofins" or "ValorCofinsProprio" or "ValorDeducao" or
            "ValorINSS" or "ValorIR" or "ValorOutrosImpostos" or "ValorPisPasep" or
            "ValorPisProprio" or "ValorRepasse" or "ValoresCartorios" or
            "nrExercicioReferencia" or "nrMesReferencia" or "ClassTrib" or "CodigoCST" or
            "CodigoNBS" or "IdLocalIncidencia" or "IndOperacao");
        var elemento = new XElement(Prestador + "ChaveAutenticacao", chave);
        if (primeiroCampoAposChave is null) nota.Add(elemento);
        else primeiroCampoAposChave.AddBeforeSelf(elemento);
        return nota;
    }

    private static string SanitizarChave(XElement xml)
    {
        var copia = new XElement(xml);
        foreach (var elemento in copia.DescendantsAndSelf().Where(e => e.Name.LocalName == "ChaveAutenticacao"))
            elemento.Value = "[REDACTED]";
        return copia.ToString(SaveOptions.DisableFormatting);
    }

    private static string RedigirChave(string valor, string chave) =>
        string.IsNullOrEmpty(chave) ? valor : valor.Replace(chave, "[REDACTED]", StringComparison.Ordinal);

    private async Task<(XElement Response, string Raw, long Ms)> SoapAsync(string url, string operation,
        XElement body, X509Certificate2 certificate, string chave, CancellationToken ct)
    {
        var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", Soap.NamespaceName),
                new XElement(Soap + "Body", body)));
        var http = _http.Obter(certificate, url);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/xml");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://tempuri.org/INotaFiscalEletronicaServico/{operation}\"");
        request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false), "text/xml");
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var raw = RedigirChave(await response.Content.ReadAsStringAsync(ct), chave);
            sw.Stop();
            XElement xml;
            try { xml = XDocument.Parse(raw).Root ?? throw new System.Xml.XmlException("SOAP vazio."); }
            catch (System.Xml.XmlException ex)
            {
                throw new NfseProvedorException($"Resposta inválida do Web Service de Osasco (HTTP {(int)response.StatusCode}).", (int)response.StatusCode, raw, ex);
            }

            var fault = Find(xml, "Fault");
            if (fault is not null)
            {
                var mensagem = Find(fault, "Text")?.Value ?? Find(fault, "faultstring")?.Value ?? "Falha SOAP de Osasco.";
                throw new NfseProvedorException(RedigirChave(mensagem, chave), (int)response.StatusCode, raw);
            }
            if (!response.IsSuccessStatusCode)
                throw new NfseProvedorException($"HTTP {(int)response.StatusCode} do Web Service de Osasco.", (int)response.StatusCode, raw);
            return (xml, raw, sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            throw new NfseProvedorException($"Falha de transporte ao chamar Osasco: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NfseProvedorException("Timeout ao chamar o Web Service de Osasco.", inner: ex);
        }
    }

    private static void ValidarEnderecoPrestacao(ServicoNfseRequest servico)
    {
        var camposAusentes = new List<string>();
        if (string.IsNullOrWhiteSpace(servico.EnderecoPrestacaoServico)) camposAusentes.Add("enderecoPrestacaoServico");
        if (string.IsNullOrWhiteSpace(servico.NumeroPrestacaoServico)) camposAusentes.Add("numeroPrestacaoServico");
        if (string.IsNullOrWhiteSpace(servico.BairroPrestacaoServico)) camposAusentes.Add("bairroPrestacaoServico");
        if (string.IsNullOrWhiteSpace(servico.CidadePrestacaoServico)) camposAusentes.Add("cidadePrestacaoServico");
        if (string.IsNullOrWhiteSpace(servico.EstadoPrestacaoServico)
            || servico.EstadoPrestacaoServico.Trim().Length != 2) camposAusentes.Add("estadoPrestacaoServico (UF)");
        if (Digitos(servico.CepPrestacaoServico).Length != 8) camposAusentes.Add("cepPrestacaoServico (8 digitos)");
        var codigoMunicipioInformado = !string.IsNullOrWhiteSpace(servico.CodigoCidadeIBGEPrestacaoServico)
            ? servico.CodigoCidadeIBGEPrestacaoServico
            : servico.CodigoMunicipioPrestacao;
        var codigoMunicipio = Digitos(codigoMunicipioInformado);
        if (codigoMunicipio.Length != 7) camposAusentes.Add("codigoCidadeIBGEPrestacaoServico (7 digitos)");

        if (camposAusentes.Count > 0)
            throw new NfseProvedorException(
                "Para emitir NFS-e em Osasco, informe o endereco completo do local da prestacao: "
                + string.Join(", ", camposAusentes) + ".");
    }

    /// <summary>
    /// Monta o PessoaDTO na ordem exata do WSDL (DataContract ignora elementos fora de ordem).
    /// O EnderecoDTO precisa de Cidade e Estado: sem eles o RTC_EmitirNFE de Osasco
    /// lança NullReferenceException.
    /// </summary>
    internal static XElement? CriarTomador(DpsDados d, string documento)
    {
        var tomador = d.Request.Tomador;
        if (tomador is null && documento.Length == 0) return null;

        var tomadorXml = new XElement(Prestador + "Tomador");
        if (documento.Length == 14) tomadorXml.Add(new XElement(Contribuinte + "CNPJ", documento));
        else if (documento.Length == 11) tomadorXml.Add(new XElement(Contribuinte + "CPF", documento));
        if (tomador is null) return tomadorXml;

        var telefone = Digitos(tomador.Telefone);
        string? ddd = null;
        if (telefone.Length is 10 or 11)
        {
            ddd = telefone[..2];
            telefone = telefone[2..];
        }

        tomadorXml.Add(
            OptionalIn(Contribuinte, "DDD", ddd),
            OptionalIn(Contribuinte, "Email", tomador.Email?.Trim()),
            CriarEnderecoTomador(d, tomador),
            OptionalIn(Contribuinte, "InscricaoMunicipal", Digitos(tomador.InscricaoMunicipal)),
            OptionalIn(Contribuinte, "Nome", tomador.Nome?.Trim()),
            OptionalIn(Contribuinte, "Telefone", telefone));
        return tomadorXml;
    }

    private static XElement? CriarEnderecoTomador(DpsDados d, TomadorNfseRequest tomador)
    {
        var temEndereco = !string.IsNullOrWhiteSpace(tomador.Logradouro)
            || !string.IsNullOrWhiteSpace(tomador.Cep)
            || !string.IsNullOrWhiteSpace(tomador.CodigoMunicipio);
        if (!temEndereco) return null;

        var codigoIbge = Digitos(tomador.CodigoMunicipio);
        var cidade = ResolverCidadeTomador(d, tomador, codigoIbge);
        var uf = ResolverUfTomador(d, tomador, codigoIbge);

        var faltando = new List<string>();
        if (string.IsNullOrWhiteSpace(tomador.Logradouro)) faltando.Add("tomador.logradouro");
        if (string.IsNullOrWhiteSpace(tomador.Numero)) faltando.Add("tomador.numero");
        if (string.IsNullOrWhiteSpace(tomador.Bairro)) faltando.Add("tomador.bairro");
        if (Digitos(tomador.Cep).Length != 8) faltando.Add("tomador.cep (8 digitos)");
        if (codigoIbge.Length != 7) faltando.Add("tomador.codigoMunicipio (IBGE, 7 digitos)");
        if (cidade is null) faltando.Add("tomador.cidade");
        if (uf is null) faltando.Add("tomador.uf");
        if (faltando.Count > 0)
            throw new NfseProvedorException(
                "Para emitir NFS-e em Osasco, complete o endereco do tomador: " + string.Join(", ", faltando) + ".");

        return new XElement(Contribuinte + "Endereco",
            new XElement(Contribuinte + "Bairro", tomador.Bairro!.Trim()),
            new XElement(Contribuinte + "CEP", Digitos(tomador.Cep)),
            new XElement(Contribuinte + "Cidade", cidade),
            new XElement(Contribuinte + "CodigoCidadeIBGE", codigoIbge),
            OptionalIn(Contribuinte, "Complemento", tomador.Complemento?.Trim()),
            new XElement(Contribuinte + "Estado", uf),
            new XElement(Contribuinte + "Logradouro", tomador.Logradouro!.Trim()),
            new XElement(Contribuinte + "Numero", tomador.Numero!.Trim()),
            new XElement(Contribuinte + "Pais", "BRASIL"),
            new XElement(Contribuinte + "PaisSigla", "BR"));
    }

    private static string? ResolverCidadeTomador(DpsDados d, TomadorNfseRequest tomador, string codigoIbge)
    {
        if (!string.IsNullOrWhiteSpace(tomador.Cidade)) return tomador.Cidade.Trim();
        if (codigoIbge.Length != 7) return null;

        var servico = d.Request.Servico;
        var codigoPrestacao = Digitos(servico.CodigoCidadeIBGEPrestacaoServico ?? servico.CodigoMunicipioPrestacao);
        if (codigoIbge == codigoPrestacao && !string.IsNullOrWhiteSpace(servico.CidadePrestacaoServico))
            return servico.CidadePrestacaoServico.Trim();
        if (codigoIbge == Digitos(d.Prestador.CodigoMunicipio) && !string.IsNullOrWhiteSpace(d.Prestador.NomeMunicipio))
            return d.Prestador.NomeMunicipio.Trim();
        return codigoIbge == CodigoIbgeOsasco ? "Osasco" : null;
    }

    private static string? ResolverUfTomador(DpsDados d, TomadorNfseRequest tomador, string codigoIbge)
    {
        if (!string.IsNullOrWhiteSpace(tomador.Uf) && CodigoUf.SiglaValida(tomador.Uf))
            return tomador.Uf.Trim().ToUpperInvariant();
        if (codigoIbge.Length == 7 && int.TryParse(codigoIbge[..2], out var codigoUf))
        {
            try { return CodigoUf.Sigla(codigoUf); }
            catch (ArgumentException) { return null; }
        }
        return null;
    }

    private static XElement? Optional(string name, string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : new XElement(Prestador + name, value);

    private static XElement? OptionalIn(XNamespace ns, string name, string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : new XElement(ns + name, value);

    // "Numero" também existe aninhado (ex.: endereço do tomador); o número da nota é o filho direto.
    private static XElement? CampoDireto(XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName) ?? Find(element, localName);

    private static XElement? Find(XElement? element, string localName) =>
        element?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);

    private static bool EhAutenticadorHomologacao(string? valor) =>
        valor?.Trim().Equals("HOMOLOGAÇÃO", StringComparison.OrdinalIgnoreCase) == true
        || valor?.Trim().Equals("HOMOLOGACAO", StringComparison.OrdinalIgnoreCase) == true;

    private static ResultadoEmissaoNfse FalhaEmissao(string raw, string motivo, long ms) =>
        new(false, null, null, null, null, "REJEITADA", motivo, raw, null, ms);

    private static string Decimal(decimal value) => value.ToString("0.00", Inv);
    private static string XmlBool(bool value) => value ? "true" : "false";
    private static bool Bool(string? value) => value?.Trim() is "1" || bool.TryParse(value, out var result) && result;
    private static string Digitos(string? value) => string.IsNullOrWhiteSpace(value) ? "" : Regex.Replace(value, @"\D", "");
}
