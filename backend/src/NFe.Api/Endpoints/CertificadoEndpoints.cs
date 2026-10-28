using Microsoft.EntityFrameworkCore;
using NFe.Api;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;

namespace NFe.Api.Endpoints;

public static class CertificadoEndpoints
{
    public static void MapCertificados(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/certificados").WithTags("Certificados");

        // Lista TODOS os certificados (metadados). Usado pelo painel para a
        // visao geral. Nunca expoe os bytes nem a chave privada.
        g.MapGet("/", async (NFeDbContext db, CancellationToken ct) =>
        {
            var certs = await db.Certificados.AsNoTracking()
                .OrderByDescending(c => c.Ativo)
                .ThenByDescending(c => c.ValidoAte)
                .ToListAsync(ct);

            return Results.Ok(certs.Select(Contratos.CertificadoDto.De));
        });

        // Upload de A1 (.pfx) com empresaId no corpo do multipart (forma usada
        // pelo painel). Delega para a mesma logica da rota por empresa.
        g.MapPost("/", async (
            HttpRequest request,
            ServicoCertificado servico,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { erro = "Envie multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["empresaId"].ToString(), out var empresaId))
                return Results.BadRequest(new { erro = "empresaId invalido." });

            return await ImportarCertificado(form, empresaId, servico, ct);
        })
        .DisableAntiforgery();

        // Lista os certificados de uma empresa (metadados, nunca os bytes).
        g.MapGet("/empresa/{empresaId:guid}", async (
            Guid empresaId, NFeDbContext db, CancellationToken ct) =>
        {
            var certs = await db.Certificados.AsNoTracking()
                .Where(c => c.EmpresaId == empresaId)
                .OrderByDescending(c => c.Ativo)
                .ThenByDescending(c => c.ValidoAte)
                .ToListAsync(ct);

            return Results.Ok(certs.Select(Contratos.CertificadoDto.De));
        });

        // Upload de A1 (.pfx) via multipart. O arquivo e a senha vao para o
        // ServicoCertificado, que valida, extrai metadados e cifra com AES-GCM.
        // O importado vira o certificado ativo; o anterior e desativado.
        g.MapPost("/empresa/{empresaId:guid}", async (
            Guid empresaId,
            HttpRequest request,
            ServicoCertificado servico,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { erro = "Envie multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            return await ImportarCertificado(form, empresaId, servico, ct);
        })
        .DisableAntiforgery();

        // Cadastro de A3 (token): recebe so a parte PUBLICA (.cer/.crt), sem
        // senha. Devolve o token de pareamento do agente UMA unica vez; no banco
        // fica so o hash. Parear de novo revoga os agentes anteriores.
        g.MapPost("/empresa/{empresaId:guid}/a3", async (
            Guid empresaId,
            HttpRequest request,
            ServicoCertificado servico,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { erro = "Envie multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            var arquivo = form.Files["arquivo"];
            if (arquivo is null || arquivo.Length == 0)
                return Results.BadRequest(new { erro = "Arquivo do certificado publico (.cer/.crt) e obrigatorio." });

            if (arquivo.Length > 64 * 1024)
                return Results.BadRequest(new { erro = "Arquivo grande demais para um certificado publico." });

            using var ms = new MemoryStream();
            await arquivo.CopyToAsync(ms, ct);

            try
            {
                var r = await servico.RegistrarA3Async(
                    empresaId, ms.ToArray(),
                    form["descricao"].ToString(), form["nomeAgente"].ToString(), ct);

                return Results.Created(
                    $"/api/certificados/{r.Certificado.Id}",
                    new
                    {
                        certificado = Contratos.CertificadoDto.De(r.Certificado),
                        agenteId = r.Agente.Id,
                        tokenAgente = r.TokenAgente,
                        aviso = "Guarde o token agora: ele nao sera exibido novamente. " +
                                "Informe-o na configuracao do agente na maquina do token."
                    });
            }
            catch (CertificadoException ex)
            {
                return Results.BadRequest(new { erro = ex.Message });
            }
        })
        .DisableAntiforgery();

        return;
    }

    /// <summary>
    /// Logica compartilhada de upload: valida a form, chama o ServicoCertificado
    /// (que cifra a chave com AES-GCM) e mapeia os erros de negocio para 400.
    /// </summary>
    private static async Task<IResult> ImportarCertificado(
        IFormCollection form,
        Guid empresaId,
        ServicoCertificado servico,
        CancellationToken ct)
    {
        var arquivo = form.Files["arquivo"];
        var senha = form["senha"].ToString();
        var descricao = form["descricao"].ToString();

        if (arquivo is null || arquivo.Length == 0)
            return Results.BadRequest(new { erro = "Arquivo .pfx e obrigatorio." });

        if (string.IsNullOrEmpty(senha))
            return Results.BadRequest(new { erro = "Senha do certificado e obrigatoria." });

        using var ms = new MemoryStream();
        await arquivo.CopyToAsync(ms, ct);

        try
        {
            var cert = await servico.ImportarAsync(
                empresaId, ms.ToArray(), senha,
                string.IsNullOrWhiteSpace(descricao) ? arquivo.FileName : descricao,
                ct);

            return Results.Created(
                $"/api/certificados/{cert.Id}",
                Contratos.CertificadoDto.De(cert));
        }
        catch (CertificadoException ex)
        {
            // Senha errada, arquivo invalido, sem chave privada, etc.
            return Results.BadRequest(new { erro = ex.Message });
        }
    }
}
