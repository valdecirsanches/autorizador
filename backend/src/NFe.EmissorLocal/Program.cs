using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NFe.Domain.Modelos;
using NFe.EmissorLocal;
using NFe.Sefaz.Endpoints;

// Emissor local da NFC-e (contingencia off-line). Ver docs/EMISSOR_LOCAL_NFCE.md.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Host.UseWindowsService(o => o.ServiceName = "NFeEmissorLocal");

var opcoes = builder.Configuration.GetSection("EmissorLocal").Get<OpcoesEmissorLocal>() ?? new OpcoesEmissorLocal();
var problemasConfig = opcoes.Problemas();
if (problemasConfig.Count > 0)
{
    Console.Error.WriteLine("[EmissorLocal] Configuracao incompleta (appsettings.json, secao EmissorLocal):");
    foreach (var p in problemasConfig) Console.Error.WriteLine("  - " + p);
    return 1;
}

builder.WebHost.UseUrls(opcoes.Url);
builder.Services.AddSingleton(opcoes);
builder.Services.AddSingleton(_ => SefazEndpointRegistry.LoadDefault());
builder.Services.AddSingleton<CertificadoEmissor>();
builder.Services.AddSingleton(_ => new FilaContingencia(opcoes.PastaFilaEfetiva()));
builder.Services.AddSingleton<EmissaoContingencia>();
builder.Services.AddSingleton<TransmissorContingencia>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransmissorContingencia>());
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .WithOrigins(opcoes.OrigensPermitidas)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
var versao = typeof(OpcoesEmissorLocal).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion?.Split('+')[0] ?? "?";
var jsonWeb = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var tokenBytes = Encoding.UTF8.GetBytes(opcoes.Token);

// Chrome/Edge: pagina publica (https://...) chamando 127.0.0.1 faz um preflight
// com Access-Control-Request-Private-Network; sem esta resposta o pedido e barrado.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    await next();
});
app.UseCors();

// Todo /api exige o token do caixa (comparacao em tempo constante).
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsOptions(ctx.Request.Method) || !ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }
    var h = ctx.Request.Headers.Authorization.ToString();
    var recebido = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : "";
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recebido), tokenBytes))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { erro = "Token do emissor local ausente ou invalido." });
        return;
    }
    await next();
});

app.MapGet("/health", (CertificadoEmissor cert, FilaContingencia fila, TransmissorContingencia tx) =>
{
    var itens = fila.Listar();
    return Results.Ok(new
    {
        status = cert.Erro is null ? "ok" : "sem-certificado",
        servico = "NFeEmissorLocal",
        versao,
        modo = "contingencia-offline",
        certificado = new { cnpj = cert.Cnpj, validade = cert.Validade, erro = cert.Erro },
        pendentes = itens.Count(i => i.Situacao == SituacaoFila.Pendente),
        prazoVencido = itens.Count(i => i.PrazoVencido),
        apiCentralConfigurada = !string.IsNullOrWhiteSpace(opcoes.ApiCentralUrl),
        ultimaTransmissao = tx.UltimoCiclo,
        aviso = tx.UltimoAviso
    });
});

// Mesmo contrato da emissao da API central, so que sempre em contingencia off-line.
app.MapPost("/api/empresas/{empresaId:guid}/nfe", async (
    Guid empresaId, HttpRequest http, EmissaoContingencia emissao, CancellationToken ct) =>
{
    NotaFiscalRequest? req;
    try { req = await JsonSerializer.DeserializeAsync<NotaFiscalRequest>(http.Body, jsonWeb, ct); }
    catch (JsonException) { return Results.BadRequest(new { erro = "O corpo nao corresponde ao JSON de uma NFC-e." }); }
    if (req is null) return Results.BadRequest(new { erro = "Corpo da requisicao ausente." });

    var modo = http.Query["contingencia"].ToString();
    if (!string.Equals(modo, "offline", StringComparison.OrdinalIgnoreCase) && req.Configuracao?.ContingenciaOffline != true)
        return Results.Json(new
        {
            erro = "O emissor local so emite NFC-e em contingencia off-line. Chame com ?contingencia=offline; " +
                   "a emissao normal e feita pela API central."
        }, statusCode: StatusCodes.Status409Conflict);

    ResultadoEmissaoLocal r;
    try { r = await emissao.EmitirAsync(empresaId, req, ct); }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { erro = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (!r.Ok)
        return Results.BadRequest(new { erro = "Dados da NFC-e invalidos. Corrija e envie de novo.", problemas = r.Problemas });

    return Results.Ok(Resposta(r.Item!, incluirXml: true) with { JaExistia = r.JaExistia });
});

app.MapGet("/api/contingencia/pendentes", (FilaContingencia fila, bool? todas) =>
    Results.Ok(fila.Listar()
        .Where(i => todas == true || i.Situacao != SituacaoFila.Autorizada)
        .Select(i => Resposta(i, incluirXml: false))));

app.MapGet("/api/contingencia/{chave}", (string chave, FilaContingencia fila) =>
    fila.Obter(chave) is { } item
        ? Results.Ok(Resposta(item, incluirXml: true))
        : Results.NotFound(new { erro = "NFC-e nao encontrada na fila." }));

app.MapPost("/api/contingencia/transmitir", async (TransmissorContingencia tx, CancellationToken ct) =>
    Results.Ok(await tx.TransmitirAgoraAsync(ct)));

app.Run();
return 0;

static RespostaNfceLocal Resposta(ItemFila i, bool incluirXml) => new(
    ChaveAcesso: i.Chave,
    Numero: i.Numero,
    Serie: i.Serie,
    Ambiente: i.Ambiente,
    Status: i.Situacao == SituacaoFila.Pendente ? "contingencia" : i.Situacao,
    ContingenciaOffline: true,
    Situacao: i.Situacao,
    DataEmissao: i.DataEmissao,
    PrazoTransmissao: i.PrazoTransmissao,
    PrazoVencido: i.PrazoVencido,
    ValorTotal: i.ValorTotal,
    QrCode: i.QrCode,
    UrlChave: i.UrlChave,
    Tentativas: i.Tentativas,
    UltimoErro: i.UltimoErro,
    DocumentoId: i.DocumentoId,
    Protocolo: i.Protocolo,
    CodigoStatus: i.CodigoStatus,
    Motivo: i.Motivo,
    XmlBase64: incluirXml ? B64(i.Xml) : null,
    XmlAutorizadoBase64: incluirXml && i.XmlAutorizado is not null ? B64(i.XmlAutorizado) : null);

static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

/// <summary>Resposta do emissor local (camelCase no JSON).</summary>
internal sealed record RespostaNfceLocal(
    string ChaveAcesso, long Numero, int Serie, int Ambiente, string Status, bool ContingenciaOffline,
    string Situacao, DateTimeOffset DataEmissao, DateTimeOffset PrazoTransmissao, bool PrazoVencido,
    decimal ValorTotal, string QrCode, string UrlChave, int Tentativas, string? UltimoErro,
    Guid? DocumentoId, string? Protocolo, string? CodigoStatus, string? Motivo,
    string? XmlBase64, string? XmlAutorizadoBase64, bool JaExistia = false);
