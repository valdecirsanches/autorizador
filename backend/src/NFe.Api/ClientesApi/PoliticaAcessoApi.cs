namespace NFe.Api.ClientesApi;

public enum TipoRegra
{
    /// <summary>Qualquer app autenticado pode chamar (dados nao ligados a empresa).</summary>
    Livre,
    /// <summary>Apenas o token mestre (NFE_API_TOKEN).</summary>
    SomenteMestre,
    /// <summary>GET /api/empresas: o endpoint filtra pelas empresas do app.</summary>
    ListaEmpresas,
    /// <summary>Cadastro de empresa: exige a permissao PodeCadastrarEmpresas.</summary>
    CadastroEmpresa,
    /// <summary>Exige acesso a todos os recursos listados em Requisitos.</summary>
    Restrita,
    /// <summary>Rota nao liberada para apps (padrao para rotas desconhecidas).</summary>
    Negada,
}

public enum TipoRecurso
{
    /// <summary>Empresa por Id (GUID) ou CNPJ.</summary>
    Empresa,
    DocumentoNfe,
    NotaServico,
    TentativaNfe,
    TentativaNfse,
    Certificado,
}

public sealed record Requisito(TipoRecurso Tipo, string Valor);

public sealed record RegraAcesso(TipoRegra Tipo, IReadOnlyList<Requisito> Requisitos, string? Motivo = null)
{
    public static RegraAcesso Livre { get; } = new(TipoRegra.Livre, []);
    public static RegraAcesso Mestre(string motivo) => new(TipoRegra.SomenteMestre, [], motivo);
    public static RegraAcesso Negar(string motivo) => new(TipoRegra.Negada, [], motivo);
}

/// <summary>
/// Decide o que um APP (chave "nfeapp_...") precisa ter para chamar uma rota.
/// O token mestre nao passa por aqui.
///
/// E uma funcao pura (metodo, caminho, valores de rota, query) para poder ser
/// testada sem banco. Regra de ouro: rota que nao se encaixa em nenhuma regra
/// e NEGADA. Assim, endpoint novo nasce fechado para apps ate alguem decidir.
/// </summary>
public static class PoliticaAcessoApi
{
    public static RegraAcesso Classificar(
        string metodo,
        string caminho,
        IReadOnlyDictionary<string, object?> rotas,
        IReadOnlyDictionary<string, string?> query)
    {
        var p = Normalizar(caminho);
        var segmentos = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var get = string.Equals(metodo, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(metodo, "HEAD", StringComparison.OrdinalIgnoreCase);
        var post = string.Equals(metodo, "POST", StringComparison.OrdinalIgnoreCase);
        var delete = string.Equals(metodo, "DELETE", StringComparison.OrdinalIgnoreCase);

        string? Rota(string nome) =>
            rotas.TryGetValue(nome, out var v) && v is not null && v.ToString() is { Length: > 0 } s ? s : null;

        // ---------------------------------------------------- somente mestre
        if (p == "/api/clientes/eu")
            return RegraAcesso.Livre;
        if (p == "/api/clientes" || p.StartsWith("/api/clientes/", StringComparison.Ordinal))
            return RegraAcesso.Mestre("A gestao de chaves de app e exclusiva do token mestre.");
        if (p.StartsWith("/api/monitor", StringComparison.Ordinal))
            return RegraAcesso.Mestre("O monitor da API e exclusivo do token mestre.");
        if (p.StartsWith("/diag/", StringComparison.Ordinal))
            return RegraAcesso.Mestre("Diagnostico do servidor e exclusivo do token mestre.");
        if (p == "/api/certificados")
            return RegraAcesso.Mestre(
                "Esta rota lista/recebe certificados de todas as empresas. "
                + "Use /api/certificados/empresa/{empresaId}.");
        if (p.EndsWith("/pasta-xml", StringComparison.Ordinal)
            || p.EndsWith("/pasta-xml/diagnostico", StringComparison.Ordinal))
            return RegraAcesso.Mestre(
                "A pasta de XML aponta para o disco do servidor; so o token mestre configura.");
        if (delete && segmentos is ["api", "empresas", _])
            return RegraAcesso.Mestre("Excluir empresa e exclusivo do token mestre. Desative com PUT (ativa=false).");

        // ------------------------------------------------------------ livres
        if (get && p == "/api/status")
            return RegraAcesso.Livre;
        if (get && (p == "/api/nfse/municipios" || p == "/api/nfse/motivos-cancelamento"))
            return RegraAcesso.Livre;
        if (get && (p.StartsWith("/openapi/", StringComparison.Ordinal)
                    || p.StartsWith("/swagger/", StringComparison.Ordinal)))
            return RegraAcesso.Livre;

        // ---------------------------------------------- empresas (cadastro)
        if (get && p == "/api/empresas")
            return new RegraAcesso(TipoRegra.ListaEmpresas, []);
        if (post && (p == "/api/empresas" || p == "/api/empresas/onboarding"))
            return new RegraAcesso(TipoRegra.CadastroEmpresa, []);

        // ---------------------------------------------- rotas por recurso
        var requisitos = new List<Requisito>();

        if (p == "/api/nfse/tentativas")
        {
            return query.TryGetValue("empresaId", out var emp) && !string.IsNullOrWhiteSpace(emp)
                ? new RegraAcesso(TipoRegra.Restrita, [new Requisito(TipoRecurso.Empresa, emp!)])
                : RegraAcesso.Negar("Informe ?empresaId=... para listar as tentativas de NFS-e.");
        }

        var empresaIdRota = Rota("empresaId");
        if (empresaIdRota is not null)
            requisitos.Add(new Requisito(TipoRecurso.Empresa, empresaIdRota));

        if (p.StartsWith("/api/empresas/", StringComparison.Ordinal))
        {
            // Em /api/empresas/{id} o "id" e a empresa. Quando a rota ja tem
            // {empresaId}, o "id" e outro recurso (ex.: a NFS-e no cancelamento).
            if (empresaIdRota is null && Rota("id") is { } idEmpresa)
                requisitos.Add(new Requisito(TipoRecurso.Empresa, idEmpresa));
            if (empresaIdRota is not null && Rota("id") is { } idNota && p.Contains("/nfse/", StringComparison.Ordinal))
                requisitos.Add(new Requisito(TipoRecurso.NotaServico, idNota));
            if (Rota("empresa") is { } chaveEmpresa)
                requisitos.Add(new Requisito(TipoRecurso.Empresa, chaveEmpresa));
            if (Rota("cnpj") is { } cnpj)
                requisitos.Add(new Requisito(TipoRecurso.Empresa, cnpj));
        }
        else if (p.StartsWith("/api/nfe/", StringComparison.Ordinal) && Rota("id") is { } idNfe)
        {
            requisitos.Add(new Requisito(TipoRecurso.DocumentoNfe, idNfe));
        }
        else if (p.StartsWith("/api/nfse/", StringComparison.Ordinal) && Rota("id") is { } idNfse)
        {
            requisitos.Add(new Requisito(TipoRecurso.NotaServico, idNfse));
        }
        else if (p.StartsWith("/api/tentativas/nfe/", StringComparison.Ordinal) && Rota("id") is { } idTent)
        {
            requisitos.Add(new Requisito(TipoRecurso.TentativaNfe, idTent));
        }
        else if (p.StartsWith("/api/tentativas/nfse/", StringComparison.Ordinal) && Rota("id") is { } idTentNfse)
        {
            requisitos.Add(new Requisito(TipoRecurso.TentativaNfse, idTentNfse));
        }
        else if (p.StartsWith("/api/certificados/", StringComparison.Ordinal) && Rota("certificadoId") is { } idCert)
        {
            requisitos.Add(new Requisito(TipoRecurso.Certificado, idCert));
        }

        return requisitos.Count > 0
            ? new RegraAcesso(TipoRegra.Restrita, requisitos)
            : RegraAcesso.Negar("Esta rota nao esta liberada para chaves de app.");
    }

    private static string Normalizar(string caminho)
    {
        var p = (caminho ?? "/").ToLowerInvariant();
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}
