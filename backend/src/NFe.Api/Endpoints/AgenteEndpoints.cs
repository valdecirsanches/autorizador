using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;

namespace NFe.Api.Endpoints;

/// <summary>
/// Canal do agente A3. O WebSocket NAO usa o token da API: o agente se
/// autentica com o token de pareamento proprio, que so ele conhece.
/// </summary>
public static class AgenteEndpoints
{
    public const string CaminhoWebSocket = "/agente/ws";

    private static readonly TimeSpan PrazoHello = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IntervaloGravacaoContato = TimeSpan.FromSeconds(60);

    public static void MapAgentes(this IEndpointRouteBuilder app)
    {
        app.Map(CaminhoWebSocket, ConectarAgenteAsync);

        var g = app.MapGroup("/api/certificados").WithTags("Agente A3");

        // Situacao do agente de um certificado A3 (online/offline) para o painel.
        g.MapGet("/{certificadoId:guid}/agente", async (
            Guid certificadoId, NFeDbContext db, AgenteHub hub, CancellationToken ct) =>
        {
            var agentes = await db.AgentesCertificado.AsNoTracking()
                .Where(a => a.CertificadoId == certificadoId)
                .OrderByDescending(a => a.CriadoEm)
                .ToListAsync(ct);

            var conexao = hub.Status(certificadoId);
            return Results.Ok(new
            {
                online = conexao is not null,
                conectadoEm = conexao?.ConectadoEm,
                agentes = agentes.Select(a => new
                {
                    a.Id,
                    a.Nome,
                    a.Revogado,
                    versao = a.VersaoAgente,
                    ultimoContatoEm = a.UltimoContatoEm,
                    a.CriadoEm,
                    online = !a.Revogado && conexao?.AgenteId == a.Id,
                }),
            });
        });

        // Revoga um agente: derruba a conexao e o token deixa de valer.
        g.MapDelete("/{certificadoId:guid}/agente/{agenteId:guid}", async (
            Guid certificadoId, Guid agenteId, NFeDbContext db, AgenteHub hub, CancellationToken ct) =>
        {
            var agente = await db.AgentesCertificado
                .FirstOrDefaultAsync(a => a.Id == agenteId && a.CertificadoId == certificadoId, ct);
            if (agente is null) return Results.NotFound(new { erro = "Agente nao encontrado." });

            agente.Revogado = true;
            await db.SaveChangesAsync(ct);
            hub.Desconectar(certificadoId);
            return Results.NoContent();
        });
    }

    private static async Task ConectarAgenteAsync(
        HttpContext ctx, NFeDbContext db, AgenteHub hub, IServiceScopeFactory escopos, ILoggerFactory logs)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { erro = "Use uma conexao WebSocket." });
            return;
        }

        var log = logs.CreateLogger("AgenteA3");
        var token = LerToken(ctx);
        if (token is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var agente = await db.AgentesCertificado.AsNoTracking()
            .Include(a => a.Certificado)
            .FirstOrDefaultAsync(a => a.TokenHash == hash && !a.Revogado, ctx.RequestAborted);

        if (agente?.Certificado is not { Ativo: true, Tipo: TipoCertificado.A3 } cert)
        {
            log.LogWarning("Conexao de agente recusada: token invalido, revogado ou certificado inativo.");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

        var hello = await LerHelloAsync(socket, ctx.RequestAborted);
        if (hello is null)
        {
            await FecharAsync(socket, "Mensagem inicial invalida.");
            return;
        }

        if (!string.Equals(hello.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "Agente {Agente} apresentou o certificado {Visto}, mas o cadastrado e {Esperado}.",
                agente.Id, hello.Thumbprint, cert.Thumbprint);
            await FecharAsync(socket,
                "O certificado do token nao e o cadastrado para este agente.");
            return;
        }

        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { tipo = "ok" }),
            WebSocketMessageType.Text, true, ctx.RequestAborted);

        var agenteId = agente.Id;
        var ultimaGravacao = DateTime.MinValue;

        async Task<bool> AoContato(CancellationToken ct)
        {
            if (DateTime.UtcNow - ultimaGravacao < IntervaloGravacaoContato)
                return true;

            await using var escopo = escopos.CreateAsyncScope();
            var banco = escopo.ServiceProvider.GetRequiredService<NFeDbContext>();
            var atual = await banco.AgentesCertificado.FirstOrDefaultAsync(a => a.Id == agenteId, ct);
            if (atual is null || atual.Revogado) return false;

            atual.UltimoContatoEm = DateTime.UtcNow;
            atual.VersaoAgente = hello.Versao;
            await banco.SaveChangesAsync(ct);
            ultimaGravacao = DateTime.UtcNow;
            return true;
        }

        await AoContato(ctx.RequestAborted);
        await hub.AtenderAsync(
            socket, agenteId, agente.EmpresaId, cert.Id, hello.Versao, AoContato, ctx.RequestAborted);
    }

    private static string? LerToken(HttpContext ctx)
    {
        const string prefixo = "Bearer ";
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[prefixo.Length..].Trim();
        return token.Length is >= 20 and <= 256 ? token : null;
    }

    private sealed record Hello(string Thumbprint, string? Versao);

    private static async Task<Hello?> LerHelloAsync(WebSocket socket, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PrazoHello);

        var buffer = new byte[4096];
        try
        {
            var r = await socket.ReceiveAsync(buffer, limite.Token);
            if (r.MessageType != WebSocketMessageType.Text || !r.EndOfMessage) return null;

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
            var raiz = doc.RootElement;
            if (raiz.GetProperty("tipo").GetString() != "hello") return null;

            var thumb = raiz.GetProperty("thumbprint").GetString();
            if (string.IsNullOrWhiteSpace(thumb)) return null;

            var versao = raiz.TryGetProperty("versao", out var v) ? v.GetString() : null;
            return new Hello(thumb.Replace(" ", string.Empty), versao is { Length: <= 40 } ? versao : null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException
                                       or WebSocketException)
        {
            return null;
        }
    }

    private static async Task FecharAsync(WebSocket socket, string motivo)
    {
        try
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation, motivo, CancellationToken.None);
        }
        catch (WebSocketException) { /* o agente ja saiu */ }
    }
}
