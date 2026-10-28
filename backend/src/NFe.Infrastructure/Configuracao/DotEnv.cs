namespace NFe.Infrastructure.Configuracao;

/// <summary>
/// Carregador minimo de arquivos .env, sem dependencias externas.
///
/// Por que existe: o Next.js le .env.local nativamente, mas o .NET nao. Para
/// que front e back compartilhem UM unico arquivo de configuracao na raiz do
/// projeto, a API procura o .env.local subindo a arvore de diretorios a partir
/// do diretorio atual e popula as variaveis de ambiente do processo.
///
/// Fica no NFe.Infrastructure (e nao no NFe.Api) para que TANTO o runtime
/// (Program.cs) QUANTO as ferramentas de design-time do EF Core
/// (DesignTimeDbContextFactory) possam carregar o .env.local. Sem isso, o
/// `dotnet ef` nao enxerga o .env.local e falha com "MYSQL_CONNECTION_STRING
/// nao definida", pois o EF nao executa o Program.cs.
///
/// Regras:
/// - Variaveis ja definidas no ambiente real TEM prioridade (nao sao
///   sobrescritas). Isso mantem producao/CI no controle.
/// - Linhas em branco e comecadas por # sao ignoradas.
/// - Aspas simples ou duplas ao redor do valor sao removidas.
/// </summary>
public static class DotEnv
{
    /// <summary>
    /// Procura os arquivos informados (na ordem) subindo ate <paramref name="maxNiveis"/>
    /// diretorios a partir do diretorio de trabalho e carrega o primeiro achado.
    /// </summary>
    /// <returns>
    /// O caminho do arquivo carregado, ou <c>null</c> se nenhum foi encontrado
    /// (nesse caso valem apenas as variaveis ja presentes no ambiente).
    /// </returns>
    public static string? Load(
        string diretorioInicial,
        string[]? nomes = null,
        int maxNiveis = 6)
    {
        nomes ??= new[] { ".env.local", ".env" };

        var dir = new DirectoryInfo(diretorioInicial);
        for (var nivel = 0; dir is not null && nivel <= maxNiveis; nivel++, dir = dir.Parent)
        {
            foreach (var nome in nomes)
            {
                var caminho = Path.Combine(dir.FullName, nome);
                if (File.Exists(caminho))
                {
                    Aplicar(caminho);
                    return caminho;
                }
            }
        }

        return null;
    }

    public static string? LoadFile(string caminho)
    {
        if (!File.Exists(caminho))
            return null;

        Aplicar(caminho);
        return caminho;
    }

    private static void Aplicar(string caminho)
    {
        foreach (var linhaBruta in File.ReadAllLines(caminho))
        {
            var linha = linhaBruta.Trim();

            // Ignora vazias e comentarios.
            if (linha.Length == 0 || linha.StartsWith('#'))
                continue;

            // Suporta o prefixo opcional "export " comum em .env de shell.
            if (linha.StartsWith("export ", StringComparison.Ordinal))
                linha = linha["export ".Length..].TrimStart();

            var igual = linha.IndexOf('=');
            if (igual <= 0)
                continue;

            var chave = linha[..igual].Trim();
            var valor = linha[(igual + 1)..].Trim();

            // Remove aspas ao redor, se houver.
            if (valor.Length >= 2 &&
                ((valor[0] == '"' && valor[^1] == '"') ||
                 (valor[0] == '\'' && valor[^1] == '\'')))
            {
                valor = valor[1..^1];
            }

            // Ambiente real sempre vence: nao sobrescreve o que ja existe.
            if (Environment.GetEnvironmentVariable(chave) is null)
                Environment.SetEnvironmentVariable(chave, valor);
        }
    }
}
