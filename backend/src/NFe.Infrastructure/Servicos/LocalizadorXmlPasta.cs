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
/// Procura o XML de uma NF-e em uma pasta de backup organizada por periodo. Aceita
/// as pastas de periodo nos formatos <c>AAAAMM</c>, <c>AAAAMMDD</c>, <c>NFeAAAAMM</c>
/// e <c>NFeAAAAMMDD</c> (EDS Monitor), cada uma com as subpastas <c>NFe</c> (notas)
/// e <c>Evento</c> (eventos). So a subpasta NFe e lida: o evento nao e o XML da nota.
/// A pasta informada pode ser:
/// <list type="bullet">
/// <item>a que contem as pastas de periodo (ex.: <c>.../Arqs/03707154000106</c>);</item>
/// <item>a do CNPJ com uma subpasta NFe que contem os periodos;</item>
/// <item>a que contem as pastas por CNPJ (ex.: <c>.../Arqs</c>): o CNPJ vem da chave;</item>
/// <item>um exemplo concreto (<c>.../NFe202610/NFe</c>) ou o modelo com <c>{...}</c>.</item>
/// </list>
/// </summary>
public static class LocalizadorXmlPasta
{
    private const long LimiteBytes = 5 * 1024 * 1024;
    private const int LimiteVarreduraConteudo = 5000;

    /// <summary>Pasta de periodo: prefixo NFe opcional, AAAAMM e dia opcional.</summary>
    private static readonly Regex Periodo = new(
        @"^(?:nfe)?(?<mes>(?:19|20)\d{2}(?:0[1-9]|1[0-2]))(?<dia>\d{2})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Cnpj = new(@"^\d{14}$", RegexOptions.Compiled);

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

    private sealed record PastaPeriodo(string Caminho, string Mes, string Ordem);

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
        if (Validar(pasta) is not null || chave.Length != 44 || !chave.All(char.IsDigit)) return null;

        var mesDaChave = "20" + chave.Substring(2, 4);
        var cnpj = chave.Substring(6, 14);

        // 1a passada: pelo nome do arquivo (rapido), mes da chave e vizinhos primeiro.
        var periodosVistos = new List<PastaPeriodo>();
        foreach (var raiz in Raizes(pasta, cnpj))
        {
            foreach (var periodo in PeriodosEmOrdem(raiz, mesDaChave))
            {
                periodosVistos.Add(periodo);
                var achado = Melhor(PorNome(periodo.Caminho, chave), chave);
                if (achado is not null) return achado;
            }
        }

        // 2a passada: arquivos nomeados de outra forma (ex.: so o numero da nota).
        // Le o conteudo, mas so das pastas do mes da chave.
        foreach (var periodo in periodosVistos.Where(p => p.Mes == mesDaChave))
        {
            var achado = Melhor(PorConteudo(periodo.Caminho, chave), chave);
            if (achado is not null) return achado;
        }
        return null;
    }

    public static DiagnosticoPastaXml Diagnosticar(string pasta)
    {
        var erro = Validar(pasta);
        if (erro is not null) return new(false, null, 0, null, 0, erro);

        var completo = Normalizar(pasta);
        if (completo is null) return new(false, null, 0, null, 0, "Caminho invalido.");

        if (!Directory.Exists(completo))
            return new(false, completo, 0, null, 0,
                "Pasta nao encontrada neste servidor, ou sem permissao de leitura. " +
                "Ela precisa existir na maquina onde a API roda.");

        foreach (var raiz in Raizes(pasta, cnpj: null))
        {
            var periodos = ListarPeriodos(raiz);
            if (periodos.Count == 0) continue;

            var meses = periodos.Select(p => p.Mes).Distinct().Count();
            var ultimoMes = periodos[0].Mes;
            var qtd = periodos.Where(p => p.Mes == ultimoMes).Sum(p => ContarXml(PastaDeXml(p.Caminho)));
            var exemplo = Path.GetFileName(periodos[0].Caminho);
            return new(true, raiz, meses, ultimoMes, qtd,
                qtd > 0
                    ? $"Pasta valida: {meses} mes(es) encontrado(s) (ex.: {exemplo}); o mais recente ({ultimoMes}) tem {qtd} XML de NF-e."
                    : $"Pasta lida (ex.: {exemplo}), mas o mes mais recente ({ultimoMes}) nao tem XML na subpasta NFe.");
        }

        return new(true, completo, 0, null, 0,
            "A pasta existe, mas nao tem subpastas de periodo (ex.: 202610 ou NFe202610). " +
            "Informe a pasta do CNPJ que contem NFe202610, NFe202611...");
    }

    /// <summary>
    /// Caminho absoluto normalizado. Se o usuario colou o modelo com {...} ou um exemplo
    /// concreto (.../NFe202610 ou .../NFe202610/NFe), sobe ate a pasta que contem os periodos.
    /// </summary>
    private static string? Normalizar(string pasta)
    {
        string completo;
        try { completo = Path.GetFullPath(pasta.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var partes = completo.Split(Path.DirectorySeparatorChar);
        var corte = Array.FindIndex(partes, p => p.Contains('{') || p.Contains('}') || p.Contains('*'));
        if (corte > 0)
            completo = string.Join(Path.DirectorySeparatorChar, partes.Take(corte));

        completo = Path.TrimEndingDirectorySeparator(completo);
        var nome = Path.GetFileName(completo);
        var pai = Path.GetDirectoryName(completo);

        // .../NFe202610/NFe  ou  .../NFe202610/Evento
        if (pai is not null && (nome.Equals("NFe", StringComparison.OrdinalIgnoreCase) ||
                                nome.Equals("Evento", StringComparison.OrdinalIgnoreCase))
            && Periodo.IsMatch(Path.GetFileName(pai)))
            return Path.GetDirectoryName(pai) ?? completo;

        // .../NFe202610
        if (pai is not null && Periodo.IsMatch(nome)) return pai;

        return string.IsNullOrEmpty(completo) ? null : completo;
    }

    private static IEnumerable<string> Raizes(string pasta, string? cnpj)
    {
        var completo = Normalizar(pasta);
        if (completo is null || !Directory.Exists(completo)) yield break;

        var vistas = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string?> Candidatas(string dir)
        {
            yield return dir;
            yield return Subpasta(dir, "NFe");
        }

        var bases = new List<string> { completo };
        if (cnpj is not null)
        {
            var doCnpj = Subpasta(completo, cnpj);
            if (doCnpj is not null) bases.Add(doCnpj);
        }
        else
        {
            // Diagnostico sem chave: aceita a pasta que contem as pastas por CNPJ.
            bases.AddRange(Subpastas(completo).Where(d => Cnpj.IsMatch(Path.GetFileName(d))).Take(20));
        }

        foreach (var b in bases)
            foreach (var c in Candidatas(b))
                if (c is not null && vistas.Add(c)) yield return c;
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

    private static List<string> Subpastas(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", SemRecursao).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>Pastas de periodo da raiz, da mais recente para a mais antiga.</summary>
    private static List<PastaPeriodo> ListarPeriodos(string raiz)
    {
        var lista = new List<PastaPeriodo>();
        foreach (var d in Subpastas(raiz))
        {
            var m = Periodo.Match(Path.GetFileName(d));
            if (!m.Success) continue;
            var mes = m.Groups["mes"].Value;
            var dia = m.Groups["dia"].Success ? m.Groups["dia"].Value : "99";
            lista.Add(new PastaPeriodo(d, mes, mes + dia));
        }
        return lista.OrderByDescending(p => p.Ordem, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Mes da chave primeiro (a chave traz o mes de emissao), depois o seguinte e o
    /// anterior (a nota pode ser arquivada no mes da autorizacao) e por fim os demais,
    /// do mais recente ao mais antigo.
    /// </summary>
    private static IEnumerable<PastaPeriodo> PeriodosEmOrdem(string raiz, string mesDaChave)
    {
        var todos = ListarPeriodos(raiz);

        var prioritarios = new List<string> { mesDaChave };
        if (int.TryParse(mesDaChave[..4], out var ano) && int.TryParse(mesDaChave[4..], out var mes)
            && mes is >= 1 and <= 12)
        {
            var inicio = new DateTime(ano, mes, 1);
            prioritarios.Add(inicio.AddMonths(1).ToString("yyyyMM"));
            prioritarios.Add(inicio.AddMonths(-1).ToString("yyyyMM"));
        }

        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in prioritarios)
            foreach (var p in todos.Where(p => p.Mes == m))
                if (vistos.Add(p.Caminho)) yield return p;
        foreach (var p in todos)
            if (vistos.Add(p.Caminho)) yield return p;
    }

    private static string PastaDeXml(string periodoDir) => Subpasta(periodoDir, "NFe") ?? periodoDir;

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

    /// <summary>Sem subpasta NFe, ignora o que estiver dentro de uma pasta Evento.</summary>
    private static Func<string, bool> ForaDeEvento(string periodoDir, out string pastaXml)
    {
        pastaXml = PastaDeXml(periodoDir);
        var semSubpastaNfe = pastaXml == periodoDir;
        return caminho => !semSubpastaNfe || !caminho.Contains(
            Path.DirectorySeparatorChar + "Evento" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> PorNome(string periodoDir, string chave)
    {
        var filtro = ForaDeEvento(periodoDir, out var pastaXml);
        return Enumerar(pastaXml, $"*{chave}*.xml", ComRecursao).Where(filtro).Take(20);
    }

    private static IEnumerable<string> PorConteudo(string periodoDir, string chave)
    {
        var filtro = ForaDeEvento(periodoDir, out var pastaXml);
        return Enumerar(pastaXml, "*.xml", ComRecursao)
            .Where(filtro)
            .Where(c => !Path.GetFileName(c).Contains(chave, StringComparison.Ordinal))
            .Take(LimiteVarreduraConteudo);
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
