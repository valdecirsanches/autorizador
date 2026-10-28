using System.Reflection;
using NFe.Domain;

namespace NFe.Nfse.Municipios;

/// <summary>Dados de um municipio no catalogo ACBrNFSeXServicos.ini.</summary>
public sealed record MunicipioNfse(
    string CodigoIbge,
    string Nome,
    string Uf,
    string Provedor,
    string? Versao,
    string? UrlProducao,
    string? UrlHomologacao,
    PadraoNfse? Padrao)
{
    public string? UrlPara(TipoAmbiente ambiente)
        => ambiente == TipoAmbiente.Producao ? UrlProducao : UrlHomologacao;
}

/// <summary>
/// Catalogo de municipios no formato do ACBrNFSeXServicos.ini (projeto ACBr,
/// open source): cada secao numerica e um codigo IBGE com Provedor, Versao e
/// URLs; secoes nomeadas ([Betha], [Ginfes]...) trazem as URLs padrao do
/// provedor. O arquivo embutido pode ser substituido por uma copia chamada
/// ACBrNFSeXServicos.ini ao lado do executavel, como no ACBr.
/// </summary>
public sealed class CatalogoMunicipiosNfse
{
    public const string NomeArquivo = "ACBrNFSeXServicos.ini";
    private const string CodigoOsasco = "3534401";
    private const string UrlOsasco = "https://nfe.osasco.sp.gov.br/EissnfeWebServices/NotaFiscalEletronica.svc";

    private static readonly Dictionary<string, string> UfPorPrefixoIbge = new()
    {
        ["11"] = "RO", ["12"] = "AC", ["13"] = "AM", ["14"] = "RR", ["15"] = "PA", ["16"] = "AP", ["17"] = "TO",
        ["21"] = "MA", ["22"] = "PI", ["23"] = "CE", ["24"] = "RN", ["25"] = "PB", ["26"] = "PE", ["27"] = "AL",
        ["28"] = "SE", ["29"] = "BA", ["31"] = "MG", ["32"] = "ES", ["33"] = "RJ", ["35"] = "SP", ["41"] = "PR",
        ["42"] = "SC", ["43"] = "RS", ["50"] = "MS", ["51"] = "MT", ["52"] = "GO", ["53"] = "DF"
    };

    private static readonly Lazy<CatalogoMunicipiosNfse> _instancia = new(CarregarPadrao);

    private readonly Dictionary<string, MunicipioNfse> _municipios;

    private CatalogoMunicipiosNfse(Dictionary<string, MunicipioNfse> municipios) => _municipios = municipios;

    public static CatalogoMunicipiosNfse Instancia => _instancia.Value;

    public int Total => _municipios.Count;

    public MunicipioNfse? Buscar(string? codigoIbge)
    {
        if (string.IsNullOrWhiteSpace(codigoIbge)) return null;
        var digitos = new string(codigoIbge.Where(char.IsDigit).ToArray());
        return _municipios.TryGetValue(digitos, out var m) ? m : null;
    }

    public IReadOnlyList<MunicipioNfse> Listar()
        => _municipios.Values
            .Where(m => m.Padrao.HasValue)
            .OrderBy(m => m.Uf, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Nome, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static CatalogoMunicipiosNfse Carregar(TextReader leitor)
    {
        var secoes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? atual = null;

        string? linha;
        while ((linha = leitor.ReadLine()) is not null)
        {
            linha = linha.Trim();
            if (linha.Length == 0 || linha[0] == ';') continue;

            if (linha[0] == '[' && linha[^1] == ']')
            {
                var nome = linha[1..^1].Trim();
                if (!secoes.TryGetValue(nome, out atual))
                {
                    atual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    secoes[nome] = atual;
                }
                continue;
            }

            var igual = linha.IndexOf('=');
            if (atual is null || igual <= 0) continue;
            atual[linha[..igual].Trim()] = linha[(igual + 1)..].Trim();
        }

        var municipios = new Dictionary<string, MunicipioNfse>();
        foreach (var (nome, campos) in secoes)
        {
            if (nome.Length != 7 || !nome.All(char.IsDigit)) continue;

            var provedor = Valor(campos, "Provedor") ?? "";
            Dictionary<string, string>? baseProvedor = null;
            if (provedor.Length > 0) secoes.TryGetValue(provedor, out baseProvedor);

            string? Resolver(string chave) => Valor(campos, chave) ?? Valor(baseProvedor, chave);

            var urlProducao = Resolver("ProRecepcionar") ?? Resolver("ProGerarNFSe");
            var urlHomologacao = Resolver("HomRecepcionar") ?? Resolver("HomGerarNFSe");
            if (nome == CodigoOsasco)
            {
                // Um .ini externo pode trazer Osasco so com Provedor/NomeURL; o endpoint e fixo.
                urlProducao ??= UrlOsasco;
                urlHomologacao ??= UrlOsasco;
            }

            municipios[nome] = new MunicipioNfse(
                nome,
                Valor(campos, "Nome") ?? "",
                ResolverUf(Valor(campos, "UF"), nome),
                provedor,
                Resolver("Versao"),
                urlProducao,
                urlHomologacao,
                ResolverPadrao(nome, provedor));
        }

        return new CatalogoMunicipiosNfse(municipios);
    }

    private static string ResolverUf(string? uf, string codigoIbge)
    {
        if (uf is { Length: 2 } && uf.All(char.IsLetter)) return uf.ToUpperInvariant();
        return UfPorPrefixoIbge.TryGetValue(codigoIbge[..2], out var sigla) ? sigla : "";
    }

    private static PadraoNfse? ResolverPadrao(string codigoIbge, string provedor)
    {
        if (codigoIbge == CodigoOsasco) return PadraoNfse.Osasco;
        if (provedor.Length == 0) return null;
        if (provedor.Equals("PadraoNacional", StringComparison.OrdinalIgnoreCase)) return PadraoNfse.Nacional;
        return PadraoNfse.Abrasf;
    }

    private static string? Valor(Dictionary<string, string>? campos, string chave)
    {
        if (campos is null || !campos.TryGetValue(chave, out var v)) return null;
        return string.IsNullOrWhiteSpace(v) || v == "*" ? null : v;
    }

    private static CatalogoMunicipiosNfse CarregarPadrao()
    {
        var externo = Path.Combine(AppContext.BaseDirectory, NomeArquivo);
        if (File.Exists(externo))
        {
            using var arquivo = new StreamReader(externo, System.Text.Encoding.Latin1);
            return Carregar(arquivo);
        }

        var assembly = typeof(CatalogoMunicipiosNfse).Assembly;
        using var stream = assembly.GetManifestResourceStream(NomeArquivo)
            ?? throw new InvalidOperationException($"Recurso {NomeArquivo} nao encontrado no assembly.");
        using var leitor = new StreamReader(stream, System.Text.Encoding.UTF8);
        return Carregar(leitor);
    }
}
