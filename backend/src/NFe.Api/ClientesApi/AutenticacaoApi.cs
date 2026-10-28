using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NFe.Api.Endpoints;
using NFe.Infrastructure.Data;

namespace NFe.Api.ClientesApi;

/// <summary>
/// Autenticacao e autorizacao de TODA requisicao da API.
///
/// Aceita dois tipos de credencial no cabecalho "Authorization: Bearer ...":
///   1. O token mestre (NFE_API_TOKEN): acesso total, como sempre foi. E o que o
///      painel usa. Integracoes antigas continuam funcionando sem mudanca.
///   2. Uma chave de app ("nfeapp_..."): acesso so as empresas liberadas para
///      aquele app, sem rotas administrativas (ver PoliticaAcessoApi).
///
/// Rotas liberadas sem credencial (inalteradas): preflight CORS, /health,
/// /diag/auth e o WebSocket do agente A3 (que tem autenticacao propria).
/// </summary>
public static class AutenticacaoApi
{
    public static async Task ProcessarAsync(HttpContext ctx, Func<Task> next, byte[] tokenMestre)
    {
        if (RotaSemCredencial(ctx.Request))
        {
            await next();
            return;
        }

        var token = ExtrairBearer(ctx.Request.Headers.Authorization.ToString());
        Chamador? chamador = null;
        string? motivo = null;

        if (token is not null)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), tokenMestre))
            {
                chamador = Chamador.Mestre;
            }
            else if (ChaveApi.TemFormatoDeChave(token))
            {
                var repo = ctx.RequestServices.GetRequiredService<RepositorioClientesApi>();
                var cliente = await repo.BuscarPorChaveAsync(token, ctx.RequestAborted);
                if (cliente is { Ativo: true })
                    chamador = Chamador.DeApp(cliente);
                else if (cliente is not null)
                    motivo = "Chave de app desativada. Fale com o administrador da API.";
            }
        }

        if (chamador is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ctx.Response.WriteAsJsonAsync(motivo is null
                ? new { erro = "Nao autorizado." }
                : (object)new { erro = "Nao autorizado.", detalhe = motivo });
            return;
        }

        chamador.Gravar(ctx);

        if (!chamador.EhMestre)
        {
            var negacao = await AutorizarAppAsync(ctx, chamador);
            if (negacao is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { erro = "Acesso negado para esta chave de app.", detalhe = negacao });
                return;
            }
        }

        await next();
    }

    internal static bool RotaSemCredencial(HttpRequest request) =>
        HttpMethods.IsOptions(request.Method)
        || request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || request.Path.Equals(AgenteEndpoints.CaminhoWebSocket, StringComparison.OrdinalIgnoreCase)
        // /diag/auth expoe SO o comprimento e o fingerprint do token mestre.
        || request.Path.Equals("/diag/auth", StringComparison.OrdinalIgnoreCase);

    internal static string? ExtrairBearer(string header)
    {
        const string prefixo = "Bearer ";
        if (!header.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase))
            return null;
        var valor = header[prefixo.Length..].Trim();
        return valor.Length == 0 ? null : valor;
    }

    /// <summary>
    /// Retorna null se o app pode seguir, ou o motivo da negacao.
    /// </summary>
    private static async Task<string?> AutorizarAppAsync(HttpContext ctx, Chamador chamador)
    {
        // Fecha por padrao: sem endpoint casado (rota inexistente) o app recebe
        // 403 em vez de 404. Isso tambem garante que, se o roteamento um dia
        // deixar de rodar antes deste middleware, apps ficam bloqueados em vez
        // de ganharem acesso amplo sem perceber.
        if (ctx.GetEndpoint() is null)
            return "Rota inexistente ou nao liberada para chaves de app.";

        var query = ctx.Request.Query.ToDictionary(
            q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var regra = PoliticaAcessoApi.Classificar(
            ctx.Request.Method, ctx.Request.Path.Value ?? "/", ctx.Request.RouteValues, query);

        switch (regra.Tipo)
        {
            case TipoRegra.Livre:
            case TipoRegra.ListaEmpresas: // o endpoint filtra.
                return null;
            case TipoRegra.CadastroEmpresa:
                return chamador.PodeCadastrarEmpresas
                    ? null // o endpoint confere se o CNPJ ja pertence a outro app.
                    : "Esta chave nao tem permissao para cadastrar empresas.";
            case TipoRegra.SomenteMestre:
            case TipoRegra.Negada:
                return regra.Motivo;
        }

        var db = ctx.RequestServices.GetRequiredService<NFeDbContext>();
        foreach (var req in regra.Requisitos)
        {
            var empresaId = await ResolverEmpresaAsync(db, req, ctx.RequestAborted);
            switch (empresaId)
            {
                case ResultadoEmpresa.NaoExiste when req.Tipo != TipoRecurso.Empresa:
                    continue; // documento inexistente: o endpoint responde 404.
                case ResultadoEmpresa.Encontrada e when chamador.PodeAcessar(e.Id):
                    continue;
                default:
                    // Mesma resposta para "nao existe" e "nao liberada": nao revela
                    // quais empresas/CNPJs estao cadastrados para outros apps.
                    return req.Tipo == TipoRecurso.Empresa
                        ? "Empresa nao encontrada ou nao liberada para esta chave."
                        : "O recurso pertence a uma empresa nao liberada para esta chave.";
            }
        }
        return null;
    }

    private abstract record ResultadoEmpresa
    {
        public sealed record Encontrada(Guid Id) : ResultadoEmpresa;
        public sealed record NaoExiste : ResultadoEmpresa;
        public static readonly ResultadoEmpresa Nenhuma = new NaoExiste();
    }

    private static async Task<ResultadoEmpresa> ResolverEmpresaAsync(
        NFeDbContext db, Requisito req, CancellationToken ct)
    {
        if (req.Tipo == TipoRecurso.Empresa)
        {
            if (Guid.TryParse(req.Valor, out var id))
                return new ResultadoEmpresa.Encontrada(id);
            var digitos = new string(req.Valor.Where(char.IsDigit).ToArray());
            if (digitos.Length != 14)
                return ResultadoEmpresa.Nenhuma;
            var porCnpj = await db.Empresas.AsNoTracking()
                .Where(e => e.Cnpj == digitos).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
            return porCnpj is { } idCnpj ? new ResultadoEmpresa.Encontrada(idCnpj) : ResultadoEmpresa.Nenhuma;
        }

        Guid? dono = null;
        if (req.Tipo is TipoRecurso.TentativaNfe or TipoRecurso.TentativaNfse)
        {
            if (!long.TryParse(req.Valor, out var idLong))
                return ResultadoEmpresa.Nenhuma;
            dono = req.Tipo == TipoRecurso.TentativaNfe
                ? await db.TentativasEmissao.AsNoTracking()
                    .Where(t => t.Id == idLong).Select(t => (Guid?)t.EmpresaId).FirstOrDefaultAsync(ct)
                : await db.TentativasEmissaoNfse.AsNoTracking()
                    .Where(t => t.Id == idLong).Select(t => (Guid?)t.EmpresaId).FirstOrDefaultAsync(ct);
        }
        else
        {
            if (!Guid.TryParse(req.Valor, out var idGuid))
                return ResultadoEmpresa.Nenhuma;
            dono = req.Tipo switch
            {
                TipoRecurso.DocumentoNfe => await db.DocumentosFiscais.AsNoTracking()
                    .Where(d => d.Id == idGuid).Select(d => (Guid?)d.EmpresaId).FirstOrDefaultAsync(ct),
                TipoRecurso.NotaServico => await db.NotasServico.AsNoTracking()
                    .Where(n => n.Id == idGuid).Select(n => (Guid?)n.EmpresaId).FirstOrDefaultAsync(ct),
                TipoRecurso.Certificado => await db.Certificados.AsNoTracking()
                    .Where(c => c.Id == idGuid).Select(c => (Guid?)c.EmpresaId).FirstOrDefaultAsync(ct),
                _ => null,
            };
        }

        return dono is { } d2 ? new ResultadoEmpresa.Encontrada(d2) : ResultadoEmpresa.Nenhuma;
    }
}
