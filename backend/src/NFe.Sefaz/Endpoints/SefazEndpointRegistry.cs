using System.Text.Json;
using System.Text.Json.Serialization;
using NFe.Domain;

namespace NFe.Sefaz.Endpoints;

/// <summary>Modelo desserializado do arquivo sefaz-endpoints.json.</summary>
public sealed class SefazEndpointCatalog
{
    [JsonPropertyName("versaoLayout")]
    public string VersaoLayout { get; set; } = "4.00";

    [JsonPropertyName("autorizadorPorUf")]
    public Dictionary<string, string> AutorizadorPorUf { get; set; } = new();

    /// <summary>
    /// ConsultaCadastro tem delegacao propria: nem toda UF delegada ao SVRS
    /// oferece o servico. Ausencia da UF aqui significa indisponivel.
    /// </summary>
    [JsonPropertyName("autorizadorConsultaCadastroPorUf")]
    public Dictionary<string, string> AutorizadorConsultaCadastroPorUf { get; set; } = new();

    [JsonPropertyName("contingencia")]
    public Dictionary<string, List<string>> Contingencia { get; set; } = new();

    [JsonPropertyName("autorizadores")]
    public Dictionary<string, AutorizadorConfig> Autorizadores { get; set; } = new();

    [JsonPropertyName("codigosIbgeUf")]
    public Dictionary<string, int> CodigosIbgeUf { get; set; } = new();

    /// <summary>NFC-e: URL de consulta do QR Code por ambiente ("producao"/"homologacao") e UF.</summary>
    [JsonPropertyName("qrCode")]
    public Dictionary<string, Dictionary<string, string>> QrCode { get; set; } = new();

    /// <summary>NFC-e: endereco de consulta pela chave (urlChave) por ambiente e UF.</summary>
    [JsonPropertyName("urlChave")]
    public Dictionary<string, Dictionary<string, string>> UrlChave { get; set; } = new();
}

/// <summary>Enderecos impressos/gravados no infNFeSupl da NFC-e.</summary>
public sealed record UrlsNfce(string QrCode, string UrlChave);

public sealed class AutorizadorConfig
{
    [JsonPropertyName("nome")]
    public string Nome { get; set; } = string.Empty;

    [JsonPropertyName("producao")]
    public Dictionary<string, string> Producao { get; set; } = new();

    [JsonPropertyName("homologacao")]
    public Dictionary<string, string> Homologacao { get; set; } = new();
}

/// <summary>Endpoint resolvido para uma combinacao de UF, ambiente e servico.</summary>
public sealed record EndpointResolvido(
    string Uf,
    string Autorizador,
    string NomeAutorizador,
    TipoAmbiente Ambiente,
    ServicoSefaz Servico,
    string Url,
    int CodigoUf);

public sealed class SefazEndpointException : Exception
{
    public SefazEndpointException(string message) : base(message) { }
}

/// <summary>
/// Resolve o endpoint correto a partir da UF do emitente, do ambiente e do
/// servico desejado, respeitando as delegacoes de autorizador (SVRS, SVAN) e os
/// autorizadores de contingencia (SVC-AN, SVC-RS).
/// </summary>
public sealed class SefazEndpointRegistry
{
    private readonly SefazEndpointCatalog _catalog;
    private readonly SefazEndpointCatalog? _catalogNfce;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SefazEndpointRegistry(SefazEndpointCatalog catalog, SefazEndpointCatalog? catalogNfce = null)
    {
        _catalog = catalog;
        _catalogNfce = catalogNfce;
    }

    /// <summary>true quando o catalogo da NFC-e (modelo 65) foi carregado.</summary>
    public bool SuportaNfce => _catalogNfce is not null;

    /// <summary>
    /// Carrega os catalogos (NF-e e NFC-e) a partir dos JSON embarcados ao lado do
    /// assembly. O da NFC-e e opcional: sem ele, so o modelo 55 e atendido.
    /// </summary>
    public static SefazEndpointRegistry LoadDefault()
    {
        var path = Localizar("sefaz-endpoints.json")
            ?? throw new SefazEndpointException(
                "Catalogo de endpoints nao encontrado. Procurado em: " +
                Path.Combine(AppContext.BaseDirectory, "Endpoints", "sefaz-endpoints.json"));
        var nfce = Localizar("sefaz-endpoints-nfce.json");
        return new SefazEndpointRegistry(
            LerCatalogo(File.ReadAllText(path)),
            nfce is null ? null : LerCatalogo(File.ReadAllText(nfce)));
    }

    private static string? Localizar(string arquivo)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Endpoints", arquivo);
        if (File.Exists(path)) return path;
        // Fallback util em cenarios de teste onde o conteudo nao foi copiado.
        var alt = Path.Combine(AppContext.BaseDirectory, arquivo);
        return File.Exists(alt) ? alt : null;
    }

    private static SefazEndpointCatalog LerCatalogo(string json) =>
        JsonSerializer.Deserialize<SefazEndpointCatalog>(json, JsonOpts)
            ?? throw new SefazEndpointException("Catalogo de endpoints invalido ou vazio.");

    /// <summary>Carrega catalogos a partir de texto JSON (NF-e e, opcionalmente, NFC-e).</summary>
    public static SefazEndpointRegistry LoadFromJson(string json, string? jsonNfce) =>
        new(LerCatalogo(json), jsonNfce is null ? null : LerCatalogo(jsonNfce));

    public static SefazEndpointRegistry LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static SefazEndpointRegistry LoadFromJson(string json) => new(LerCatalogo(json));

    public string VersaoLayout => _catalog.VersaoLayout;

    public IReadOnlyCollection<string> UfsSuportadas => _catalog.AutorizadorPorUf.Keys.ToList();

    /// <summary>Codigo IBGE de 2 digitos da UF, usado no campo cUF.</summary>
    public int ObterCodigoUf(string uf)
    {
        var chave = Normalizar(uf);
        if (!_catalog.CodigosIbgeUf.TryGetValue(chave, out var codigo))
            throw new SefazEndpointException($"UF desconhecida: '{uf}'.");
        return codigo;
    }

    /// <summary>
    /// Autorizador responsavel pela UF em emissao normal. Um override explicito
    /// na empresa tem precedencia, para cobrir mudancas de delegacao antes de
    /// uma atualizacao do catalogo.
    /// </summary>
    public string ResolverAutorizador(
        string uf, string? override_ = null, ServicoSefaz? servico = null)
    {
        if (!string.IsNullOrWhiteSpace(override_))
        {
            var ov = Normalizar(override_);
            if (!_catalog.Autorizadores.ContainsKey(ov))
                throw new SefazEndpointException(
                    $"Autorizador de override '{override_}' nao existe no catalogo.");
            return ov;
        }

        var chave = Normalizar(uf);

        // ConsultaCadastro nao segue a delegacao padrao: apenas parte das UFs
        // delegadas ao SVRS oferece o servico. Resolver pelo mapa padrao geraria
        // chamada a um endpoint que nao existe para aquela UF.
        if (servico == ServicoSefaz.ConsultaCadastro)
        {
            if (_catalog.AutorizadorConsultaCadastroPorUf.TryGetValue(chave, out var autCad))
                return autCad;

            throw new SefazEndpointException(
                $"A UF '{chave}' nao oferece o servico ConsultaCadastro. " +
                "UFs com o servico disponivel: " +
                $"{string.Join(", ", _catalog.AutorizadorConsultaCadastroPorUf.Keys.OrderBy(k => k))}.");
        }
        if (!_catalog.AutorizadorPorUf.TryGetValue(chave, out var autorizador))
            throw new SefazEndpointException(
                $"Nenhum autorizador mapeado para a UF '{uf}'. " +
                $"UFs disponiveis: {string.Join(", ", _catalog.AutorizadorPorUf.Keys.OrderBy(k => k))}.");

        return autorizador;
    }

    /// <summary>Autorizador de contingencia (SVC) aplicavel a UF informada.</summary>
    public string ResolverAutorizadorContingencia(string uf)
    {
        var chave = Normalizar(uf);
        foreach (var (svc, ufs) in _catalog.Contingencia)
            if (ufs.Contains(chave, StringComparer.OrdinalIgnoreCase))
                return svc;

        throw new SefazEndpointException(
            $"Nenhum autorizador de contingencia mapeado para a UF '{uf}'.");
    }

    /// <summary>
    /// Resolve a URL do servico. Quando contingencia e true, usa o SVC da UF.
    /// Para a NFC-e (modelo 65) usa o catalogo proprio; NFC-e nao tem SVC.
    /// </summary>
    public EndpointResolvido Resolver(
        string uf,
        TipoAmbiente ambiente,
        ServicoSefaz servico,
        string? autorizadorOverride = null,
        bool contingencia = false,
        ModeloDocumento modelo = ModeloDocumento.NFe)
    {
        if (modelo == ModeloDocumento.NFCe)
            return ResolverNfce(uf, ambiente, servico, autorizadorOverride, contingencia);

        var ufNorm = Normalizar(uf);

        var autorizador = contingencia
            ? ResolverAutorizadorContingencia(ufNorm)
            : ResolverAutorizador(ufNorm, autorizadorOverride, servico);

        if (!_catalog.Autorizadores.TryGetValue(autorizador, out var cfg))
            throw new SefazEndpointException(
                $"Autorizador '{autorizador}' ausente do catalogo de endpoints.");

        var mapa = ambiente == TipoAmbiente.Producao ? cfg.Producao : cfg.Homologacao;
        var nomeServico = servico.ToString();

        if (!mapa.TryGetValue(nomeServico, out var url) || string.IsNullOrWhiteSpace(url))
            throw new SefazEndpointException(
                $"Servico '{nomeServico}' nao disponivel no autorizador '{autorizador}' " +
                $"para o ambiente '{ambiente}'. " +
                $"Servicos disponiveis: {string.Join(", ", mapa.Keys.OrderBy(k => k))}.");

        return new EndpointResolvido(
            Uf: ufNorm,
            Autorizador: autorizador,
            NomeAutorizador: cfg.Nome,
            Ambiente: ambiente,
            Servico: servico,
            Url: url,
            CodigoUf: ObterCodigoUf(ufNorm));
    }

    private EndpointResolvido ResolverNfce(
        string uf, TipoAmbiente ambiente, ServicoSefaz servico, string? autorizadorOverride, bool contingencia)
    {
        var c = _catalogNfce ?? throw new SefazEndpointException(
            "Catalogo da NFC-e (sefaz-endpoints-nfce.json) nao encontrado ao lado da API.");
        if (contingencia)
            throw new SefazEndpointException(
                "A NFC-e nao tem SVC: em falha de comunicacao use a contingencia offline (tpEmis 9).");

        var ufNorm = Normalizar(uf);
        string autorizador;
        if (!string.IsNullOrWhiteSpace(autorizadorOverride))
        {
            autorizador = Normalizar(autorizadorOverride);
            if (!c.Autorizadores.ContainsKey(autorizador))
                throw new SefazEndpointException(
                    $"Autorizador de override '{autorizadorOverride}' nao existe no catalogo da NFC-e.");
        }
        else if (!c.AutorizadorPorUf.TryGetValue(ufNorm, out autorizador!))
        {
            throw new SefazEndpointException(
                $"Nenhum autorizador de NFC-e mapeado para a UF '{uf}'. " +
                $"UFs disponiveis: {string.Join(", ", c.AutorizadorPorUf.Keys.OrderBy(k => k))}.");
        }

        if (!c.Autorizadores.TryGetValue(autorizador, out var cfg))
            throw new SefazEndpointException($"Autorizador '{autorizador}' ausente do catalogo da NFC-e.");

        var mapa = ambiente == TipoAmbiente.Producao ? cfg.Producao : cfg.Homologacao;
        if (!mapa.TryGetValue(servico.ToString(), out var url) || string.IsNullOrWhiteSpace(url))
            throw new SefazEndpointException(
                $"Servico '{servico}' de NFC-e nao disponivel no autorizador '{autorizador}' " +
                $"para o ambiente '{ambiente}'.");

        return new EndpointResolvido(
            Uf: ufNorm,
            Autorizador: autorizador,
            NomeAutorizador: cfg.Nome,
            Ambiente: ambiente,
            Servico: servico,
            Url: url,
            CodigoUf: ObterCodigoUf(ufNorm));
    }

    /// <summary>URLs do QR Code e de consulta pela chave da NFC-e da UF do emitente.</summary>
    public UrlsNfce ObterUrlsNfce(string uf, TipoAmbiente ambiente)
    {
        var c = _catalogNfce ?? throw new SefazEndpointException(
            "Catalogo da NFC-e (sefaz-endpoints-nfce.json) nao encontrado ao lado da API.");
        var ufNorm = Normalizar(uf);
        var amb = ambiente == TipoAmbiente.Producao ? "producao" : "homologacao";
        if (!c.QrCode.TryGetValue(amb, out var qrs) || !qrs.TryGetValue(ufNorm, out var qr)
            || string.IsNullOrWhiteSpace(qr))
            throw new SefazEndpointException($"URL do QR Code da NFC-e nao cadastrada para {ufNorm} ({amb}).");
        if (!c.UrlChave.TryGetValue(amb, out var chs) || !chs.TryGetValue(ufNorm, out var ch)
            || string.IsNullOrWhiteSpace(ch))
            throw new SefazEndpointException($"URL de consulta pela chave da NFC-e nao cadastrada para {ufNorm} ({amb}).");
        return new UrlsNfce(qr, ch);
    }

    /// <summary>Modelo do documento pelos digitos 21-22 da chave de acesso (55 ou 65).</summary>
    public static ModeloDocumento ModeloDaChave(string? chave)
    {
        var c = new string((chave ?? "").Where(char.IsDigit).ToArray());
        return c.Length == 44 && c.Substring(20, 2) == "65" ? ModeloDocumento.NFCe : ModeloDocumento.NFe;
    }

    /// <summary>Enumera todos os endpoints, util para diagnostico e validacao.</summary>
    public IEnumerable<EndpointResolvido> EnumerarTodos()
    {
        foreach (var (nomeAut, cfg) in _catalog.Autorizadores)
        {
            foreach (var (ambiente, mapa) in new[]
            {
                (TipoAmbiente.Producao, cfg.Producao),
                (TipoAmbiente.Homologacao, cfg.Homologacao)
            })
            {
                foreach (var (servicoNome, url) in mapa)
                {
                    if (!Enum.TryParse<ServicoSefaz>(servicoNome, out var servico)) continue;

                    // UF representativa do autorizador, apenas para compor o registro.
                    var uf = _catalog.AutorizadorPorUf
                        .FirstOrDefault(kv => kv.Value == nomeAut).Key ?? "RS";

                    yield return new EndpointResolvido(
                        uf, nomeAut, cfg.Nome, ambiente, servico, url,
                        _catalog.CodigosIbgeUf.TryGetValue(uf, out var c) ? c : 0);
                }
            }
        }
    }

    private static string Normalizar(string valor) =>
        (valor ?? string.Empty).Trim().ToUpperInvariant();
}
