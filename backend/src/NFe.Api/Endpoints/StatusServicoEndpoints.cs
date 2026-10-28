using NFe.Api;
using NFe.Domain;
using NFe.Infrastructure.Servicos;

namespace NFe.Api.Endpoints;

public static class StatusServicoEndpoints
{
    public static void MapStatusServico(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/status").WithTags("StatusServico");

        // Ultima leitura de status por UF (o que o dashboard mostra por padrao).
        // O ambiente e escolhido via query (?ambiente=1 producao, 2 homologacao);
        // homologacao e o default por seguranca.
        g.MapGet("/", async (
            int? ambiente, MonitorStatusServico monitor, CancellationToken ct) =>
        {
            var amb = ambiente is null
                ? TipoAmbiente.Homologacao
                : (TipoAmbiente)ambiente.Value;

            var ultimos = await monitor.UltimosPorUfAsync(amb, ct);
            return Results.Ok(ultimos.Select(Contratos.StatusServicoDto.De));
        });

        // Consulta a SEFAZ agora usando o certificado da empresa. UF e ambiente
        // (1|2) vem no JSON; a API nao le o cadastro alem do certificado.
        g.MapPost("/empresa/{empresaId:guid}/consultar", async (
            Guid empresaId,
            Contratos.ConsultaStatusRequest req,
            MonitorStatusServico monitor,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Uf))
                return Results.BadRequest(new { erro = "Informe a UF no JSON." });
            if (req.Ambiente != 1 && req.Ambiente != 2)
                return Results.BadRequest(new { erro = "Ambiente deve ser 1 (producao) ou 2 (homologacao)." });

            try
            {
                var resultado = await monitor.ConsultarAsync(
                    empresaId, req.Uf, (TipoAmbiente)req.Ambiente,
                    req.UfAutorizadorOverride, req.Contingencia ?? false, ct);
                return Results.Ok(Contratos.StatusServicoDto.De(resultado));
            }
            catch (InvalidOperationException ex)
            {
                // Empresa inexistente ou sem certificado ativo.
                return Results.BadRequest(new { erro = ex.Message });
            }
        });

        return;
    }
}
