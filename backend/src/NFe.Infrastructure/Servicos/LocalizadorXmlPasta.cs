using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NFe.Infrastructure.Servicos;

/// <summary>XML de NF-e encontrado em disco, ja lido e normalizado.</summary>
public sealed record XmlLocalizado(string Caminho, string Xml);

/// <summary>Resumo da leitura de uma pasta de XMLs, para a tela de configuracao.</summary>
public sealed record DiagnosticoPastaXml(
    bool Existe,
    string? PastaEfetiva,
    int Meses,
    string? UltimoMes,
    int XmlsNoUltimoMes,
    string Mensagem);

/// <summary>
/// Procura o XML de uma NF-e em uma pasta de backup organizada por mes:
/// <c>{pasta}/AAAAMM/NFe/*.xml</c> (as notas) e <c>{pasta}/AAAAMM/Evento/*.xml</c>
/// (os eventos). So a subpasta NFe e lida: o evento nao e o XML da nota.
/// A pasta informada pode ser a que contem as pastas AAAAMM ou a do CNPJ que
/// contem uma subpasta NFe.
/// </summary>
public static class LocalizadorXmlPasta
{
    private const long LimiteBytes = 5 * 1024 * 1024;
    private const int LimiteVarreduraConteudo = 5000;
    private static readonly Regex Mes = new(@"^\d{6}$", RegexOptions.Compiled);

    private static readonly EnumerationOptions SemRecursao = new()
    {
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly EnumerationOptions ComRecursao = new()
    {
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = true
    };

    /// <summary>Devolve o motivo de a pasta ser invalida, ou null quando aceita.</summary>
    public static string? Validar(string? pasta)
    {
        if (string.IsNullOrWhiteSpace(pasta)) return "Informe o caminho da pasta.";
        var p = pasta.Trim();
        if (p.Length > 500) return "O caminho excede 500 caracteres.";
        if (p.Contains('\0')) return "O caminho contem caracteres invalidos.";
        if (!Path.IsPathFullyQualified(p)) return "Informe o caminho completo (absoluto), por exemplo /mnt/hdbackup/.../NFe.";
        return null;
    }

    public static XmlLocalizado? Localizar(string pasta, string chave)
    {
        if (Validar(pasta) is not null || chave.Length != 44) return null;

        var mesDaChave = "20" + chave.Substring(2, 4);
        foreach (var raiz in Raizes(pasta))
        {
            foreach (var mesDir in MesesEmOrdem(raiz, mesDaChave))
            {
                var achado = ProcurarNoMes(mesDir, chave, varrerConteudo: Path.GetFileName(mesDir) == mesDaChave);
                if (achado is not null) return achado;
            }
        }
        return null;
    }

    public static DiagnosticoPastaXml Diagnosticar(string pasta)
    {
        var erro = Validar(pasta);
        if (erro is not null) return new(false, null, 0, null, 0, erro);

        string completo;
        try { completo = Path.GetFullPath(pasta.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(false, null, 0, null, 0, "Caminho invalido.");
        }

        if (!Directory.Exists(completo))
            return new(false, completo, 0, null, 0,
                "Pasta nao encontrada neste servidor, ou sem permissao de leitura. " +
                "Ela precisa existir na maquina onde a API roda.");

        foreach (var raiz in Raizes(completo))
        {
            var meses = ListarMeses(raiz);
            if (meses.Count == 0) continue;

            var ultimo = meses[0];
            var pastaXml = PastaDeXml(ultimo);
            var qtd = ContarXml(pastaXml);
            var nomeMes = Path.GetFileName(ultimo);
            return new(true, raiz, meses.Count, nomeMes, qtd,
                qtd > 0
                    ? $"Pasta valida: {meses.Count} mes(es) encontrado(s); o mais recente ({nomeMes}) tem {qtd} XML."
                    : $"Pasta lida, mas o mes mais recente ({nomeMes}) nao tem XML em '{Path.GetFileName(pastaXml)}'.");
        }

        return new(true, completo, 0, null, 0,
            "A pasta existe, mas nao tem subpastas de mes no formato AAAAMM (ex.: 202610). " +
            "Informe a pasta que contem 202610, 202611...");
    }

    private static IEnumerable<string> Raizes(string pasta)
    {
        string completo;
        try { completo = Path.GetFullPath(pasta.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        if (!Directory.Exists(completo)) yield break;
        yield return completo;

        var nfe = Subpasta(completo, "NFe");
        if (nfe is not null) yield return nfe;
    }

    private static string? Subpasta(string dir, string nome)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, nome, SemRecursao).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Pastas AAAAMM da raiz, da mais recente para a mais antiga.</summary>
    private static List<string> ListarMeses(string raiz)
    {
        try
        {
            return Directory.EnumerateDirectories(raiz, "*", SemRecursao)
                .Where(d => Mes.IsMatch(Path.GetFileName(d)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Mes da chave primeiro, depois os vizinhos (a nota pode ser arquivada no mes da
    /// autorizacao) e por fim os demais, do mais recente ao mais antigo.
    /// </summary>
    private static IEnumerable<string> MesesEmOrdem(string raiz, string mesDaChave)
    {
        var todos = ListarMeses(raiz);
        var porNome = todos.ToDictionary(d => Path.GetFileName(d), d => d, StringComparer.Ordinal);

        var prioritarios = new List<string> { mesDaChave };
        if (int.TryParse(mesDaChave[..4], out var ano) && int.TryParse(mesDaChave[4..], out var mes))
        {
            var seguinte = new DateTime(ano, mes, 1).AddMonths(1);
            var anterior = new DateTime(ano, mes, 1).AddMonths(-1);
            prioritarios.Add(seguinte.ToString("yyyyMM"));
            prioritarios.Add(anterior.ToString("yyyyMM"));
        }

        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nome in prioritarios)
            if (porNome.TryGetValue(nome, out var dir) && vistos.Add(nome)) yield return dir;
        foreach (var d in todos)
            if (vistos.Add(Path.GetFileName(d))) yield return d;
    }

    private static string PastaDeXml(string mesDir) => Subpasta(mesDir, "NFe") ?? mesDir;

    private static int ContarXml(string pasta)
    {
        try
        {
            return Directory.EnumerateFiles(pasta, "*.xml", SemRecursao).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static XmlLocalizado? ProcurarNoMes(string mesDir, string chave, bool varrerConteudo)
    {
        var pastaXml = PastaDeXml(mesDir);
        var semSubpastaNfe = pastaXml == mesDir;

        bool ForaDeEvento(string caminho) =>
            !semSubpastaNfe || !caminho.Contains(
                Path.DirectorySeparatorChar + "Evento" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        var porNome = Enumerar(pastaXml, $"*{chave}*.xml", ComRecursao)
            .Where(ForaDeEvento).Take(20).ToList();

        var melhor = Melhor(porNome, chave);
        if (melhor is not null || !varrerConteudo) return melhor;

        // Arquivos nomeados de outra forma (ex.: so o numero da nota): le o conteudo.
        var jaVistos = new HashSet<string>(porNome, StringComparer.Ordinal);
        var restantes = Enumerar(pastaXml, "*.xml", ComRecursao)
            .Where(ForaDeEvento)
            .Where(c => !jaVistos.Contains(c))
            .Take(LimiteVarreduraConteudo);
        return Melhor(restantes, chave);
    }

    private static IEnumerable<string> Enumerar(string pasta, string padrao, EnumerationOptions opcoes)
    {
        IEnumerable<string> arquivos;
        try { arquivos = Directory.EnumerateFiles(pasta, padrao, opcoes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var it = arquivos.GetEnumerator();
        while (true)
        {
            try { if (!it.MoveNext()) yield break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }
            yield return it.Current;
        }
    }

    /// <summary>Prefere o nfeProc (com protocolo) sobre a NFe assinada sem protocolo.</summary>
    private static XmlLocalizado? Melhor(IEnumerable<string> caminhos, string chave)
    {
        XmlLocalizado? assinada = null;
        foreach (var caminho in caminhos)
        {
            var lido = Ler(caminho, chave, out var temProtocolo);
            if (lido is null) continue;
            if (temProtocolo) return lido;
            assinada ??= lido;
        }
        return assinada;
    }

    private static XmlLocalizado? Ler(string caminho, string chave, out bool temProtocolo)
    {
        temProtocolo = false;
        try
        {
            if (new FileInfo(caminho).Length > LimiteBytes) return null;

            // Load detecta a codificacao declarada (UTF-8, ISO-8859-1...).
            using var stream = File.OpenRead(caminho);
            var doc = XDocument.Load(stream);
            var raiz = doc.Root;
            if (raiz is null) return null;

            var nome = raiz.Name.LocalName;
            if (nome is not ("NFe" or "nfeProc")) return null;

            var infNFe = raiz.Descendants().FirstOrDefault(e => e.Name.LocalName == "infNFe");
            if ((string?)infNFe?.Attribute("Id") != "NFe" + chave) return null;

            temProtocolo = nome == "nfeProc"
                && raiz.Elements().Any(e => e.Name.LocalName == "protNFe");
            return new XmlLocalizado(caminho, raiz.ToString(SaveOptions.DisableFormatting));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
