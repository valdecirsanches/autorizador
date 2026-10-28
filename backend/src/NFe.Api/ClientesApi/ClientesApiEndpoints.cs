using Microsoft.EntityFrameworkCore;
using NFe.Infrastructure.Data;

namespace NFe.Api.ClientesApi;

/// <summary>
/// Gestao das chaves de app (somente token mestre — garantido pela
/// PoliticaAcessoApi) e o endpoint "quem sou eu" para qualquer chamador.
/// </summary>
public static class ClientesApiEndpoints
{
    public sealed record CriarClienteRequest(string? Nome, List<Guid>? EmpresaIds, bool? PodeCadastrarEmpresas);

    public sealed record AtualizarClienteRequest(
        string? Nome, List<Guid>? EmpresaIds, bool? PodeCadastrarEmpresas, bool? Ativo);

    public sealed record ClienteDto(
        Guid Id,
        string Nome,
        string IdentificadorChave,
        bool PodeCadastrarEmpresas,
        bool Ativo,
        IReadOnlyList<Guid> EmpresaIds,
        DateTime CriadoEm,
        DateTime? AtualizadoEm,
        DateTime? UltimoUsoEm)
    {
        public static ClienteDto De(ClienteApi c) => new(
            c.Id, c.Nome, c.IdentificadorChave + "…", c.PodeCadastrarEmpresas, c.Ativo,
            c.Empresas.OrderBy(x => x).ToList(), c.CriadoEm, c.AtualizadoEm, c.UltimoUsoEm);
    }

    private const string AvisoChave =
        "Guarde esta chave agora: ela NAO sera exibida de novo (o servidor guarda apenas o hash). "
        + "Envie-a em toda requisicao como 'Authorization: Bearer <chave>'.";

    public static void MapClientesApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/clientes").WithTags("Chaves de app");

        // Quem esta chamando: util para o integrador conferir a chave e ver
        // quais empresas pode usar. Liberado para qualquer chamador autenticado.
        g.MapGet("/eu", async (HttpContext ctx, NFeDbContext db, CancellationToken ct) =>
        {
            var chamador = Chamador.De(ctx);
            if (chamador.EhMestre)
                return Results.Ok(new { tipo = "mestre", acessoTotal = true });

            var cli = chamador.Cliente!;
            var ids = cli.Empresas.ToList();
            var empresas = await db.Empresas.AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .OrderBy(e => e.RazaoSocial)
                .Select(e => new { e.Id, e.Cnpj, e.RazaoSocial, e.Uf, e.Ativa })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                tipo = "app",
                acessoTotal = false,
                id = cli.Id,
                nome = cli.Nome,
                podeCadastrarEmpresas = cli.PodeCadastrarEmpresas,
                empresas,
            });
        }).WithName("QuemSouEu");

        g.MapGet("/", async (RepositorioClientesApi repo, CancellationToken ct) =>
            Results.Ok((await repo.ListarAsync(ct)).Select(ClienteDto.De)))
            .WithName("ListarClientesApi");

        g.MapGet("/{id:guid}", async (Guid id, RepositorioClientesApi repo, CancellationToken ct) =>
            await repo.ObterAsync(id, ct) is { } c
                ? Results.Ok(ClienteDto.De(c))
                : Results.NotFound(new { erro = "App nao encontrado." }))
            .WithName("ObterClienteApi");

        // Cria o app e devolve a chave UMA unica vez.
        g.MapPost("/", async (
            CriarClienteRequest req, RepositorioClientesApi repo, NFeDbContext db, CancellationToken ct) =>
        {
            var nome = req.Nome?.Trim();
            if (string.IsNullOrEmpty(nome) || nome.Length > 120)
                return Results.BadRequest(new { erro = "Informe o nome do app (1 a 120 caracteres)." });

            var empresas = req.EmpresaIds?.Distinct().ToList() ?? [];
            if (await EmpresasInexistentesAsync(db, empresas, ct) is { Count: > 0 } faltando)
                return Results.BadRequest(new { erro = "Empresa(s) inexistente(s).", empresaIds = faltando });

            var (cliente, chave) = await repo.CriarAsync(nome, req.PodeCadastrarEmpresas ?? false, empresas, ct);
            return Results.Created($"/api/clientes/{cliente.Id}",
                new { cliente = ClienteDto.De(cliente), chave, aviso = AvisoChave });
        }).WithName("CriarClienteApi");

        // Altera nome, permissoes, empresas (lista substitui a anterior) ou
        // suspende/reativa (ativo). Campos ausentes ficam como estao.
        g.MapPut("/{id:guid}", async (
            Guid id, AtualizarClienteRequest req, RepositorioClientesApi repo, NFeDbContext db,
            CancellationToken ct) =>
        {
            var nome = req.Nome?.Trim();
            if (nome is not null && (nome.Length == 0 || nome.Length > 120))
                return Results.BadRequest(new { erro = "Nome deve ter de 1 a 120 caracteres." });

            var empresas = req.EmpresaIds?.Distinct().ToList();
            if (empresas is not null && await EmpresasInexistentesAsync(db, empresas, ct) is { Count: > 0 } faltando)
                return Results.BadRequest(new { erro = "Empresa(s) inexistente(s).", empresaIds = faltando });

            var c = await repo.AtualizarAsync(id, nome, req.PodeCadastrarEmpresas, req.Ativo, empresas, ct);
            return c is null
                ? Results.NotFound(new { erro = "App nao encontrado." })
                : Results.Ok(ClienteDto.De(c));
        }).WithName("AtualizarClienteApi");

        // Gera uma chave nova; a anterior para de funcionar na hora.
        g.MapPost("/{id:guid}/nova-chave", async (Guid id, RepositorioClientesApi repo, CancellationToken ct) =>
            await repo.GerarNovaChaveAsync(id, ct) is { } r
                ? Results.Ok(new { cliente = ClienteDto.De(r.Cliente), chave = r.Chave, aviso = AvisoChave })
                : Results.NotFound(new { erro = "App nao encontrado." }))
            .WithName("NovaChaveClienteApi");

        // Remove o app definitivamente (a chave deixa de valer). Para apenas
        // suspender, use PUT com "ativo": false.
        g.MapDelete("/{id:guid}", async (Guid id, RepositorioClientesApi repo, CancellationToken ct) =>
            await repo.ExcluirAsync(id, ct)
                ? Results.Ok(new { removido = true })
                : Results.NotFound(new { erro = "App nao encontrado." }))
            .WithName("ExcluirClienteApi");
    }

    private static async Task<List<Guid>> EmpresasInexistentesAsync(
        NFeDbContext db, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var existentes = await db.Empresas.AsNoTracking()
            .Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
        return ids.Except(existentes).ToList();
    }
}

/// <summary>Ajustes de resposta conforme o chamador.</summary>
public static class VisibilidadeChamador
{
    /// <summary>
    /// Apps nao veem o caminho da pasta de XML (e um caminho do disco do
    /// servidor, configuravel so pelo token mestre).
    /// </summary>
    public static Contratos.EmpresaDto ParaChamador(this Contratos.EmpresaDto dto, HttpContext ctx) =>
        Chamador.De(ctx).EhMestre ? dto : dto with { PastaXmlBackup = null };
}
