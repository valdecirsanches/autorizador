using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Infrastructure.ServicosNfse;
using NFe.Nfse.Danfse;
using NFe.Nfse.Municipios;
using NFe.Nfse.Nacional;

namespace NFe.Api.Endpoints;

/// <summary>
/// Endpoints do modulo NFS-e. Totalmente separados dos endpoints de NF-e
/// (mesmo prefixo /api, rotas e tags proprias). Toda a orquestracao fica no
/// <see cref="ServicoNfse"/>; aqui so traduzimos HTTP <-> servico.
/// </summary>
public static class NfseEndpoints
{
    // ------------------------------------------------------------- DTOs

    public sealed record EventoNfseDto(
        long Id, string TipoEvento, int Sequencia, string? CodigoMotivo, string? Justificativa,
        string? Protocolo, string? CodigoStatus, string? Motivo, bool Homologado,
        DateTime? DataRegistro, DateTime CriadoEm)
    {
        public static EventoNfseDto De(EventoNotaServico e) => new(
            e.Id, e.TipoEvento, e.Sequencia, e.CodigoMotivo, e.Justificativa, e.Protocolo,
            e.CodigoStatus, e.Motivo, e.Homologado, e.DataRegistro, e.CriadoEm);
    }

    public sealed record NotaServicoDto(
        Guid Id, Guid EmpresaId, int Padrao, int Ambiente, string Serie, long NumeroDps,
        string? IdDps, string? ChaveAcesso, string? NumeroNfse, string? CodigoVerificacao,
        int Status, string StatusDescricao, DateTime DataEmissao, DateTime DataCompetencia,
        string? TomadorDocumento, string? TomadorNome, string CodigoTributacaoNacional,
        string DescricaoServico, decimal ValorServico, decimal ValorDescontos, decimal AliquotaIss,
        decimal ValorIss, bool IssRetido, decimal? ValorLiquido, string? CodigoRetorno,
        string? MotivoRetorno, DateTime? DataAutorizacao, string? ChaveIdempotencia,
        int TentativasEnvio, string? UltimoErro, DateTime CriadoEm, DateTime AtualizadoEm,
        IReadOnlyList<EventoNfseDto>? Eventos, string? XmlBase64, string? DanfsePdfBase64,
        long? DuracaoMs, bool? Reaproveitada, string? LinkNfse = null)
    {
        private static string? ExtrairLink(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(xml, @"<(?:\w+:)?Link>\s*([^<]+?)\s*</(?:\w+:)?Link>");
            return m.Success && Uri.TryCreate(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), UriKind.Absolute, out var u)
                ? u.ToString() : null;
        }

        public static NotaServicoDto De(NotaServico n, bool incluirEventos = false) => new(
            n.Id, n.EmpresaId, (int)n.Padrao, (int)n.Ambiente, n.Serie, n.NumeroDps, n.IdDps,
            n.ChaveAcesso, n.NumeroNfse, n.CodigoVerificacao, (int)n.Status, n.Status.ToString(),
            n.DataEmissao, n.DataCompetencia, n.TomadorDocumento, n.TomadorNome,
            n.CodigoTributacaoNacional, n.DescricaoServico, n.ValorServico, n.ValorDescontos,
            n.AliquotaIss, n.ValorIss, n.IssRetido, n.ValorLiquido, n.CodigoRetorno, n.MotivoRetorno,
            n.DataAutorizacao, n.ChaveIdempotencia, n.TentativasEnvio, n.UltimoErro, n.CriadoEm,
            n.AtualizadoEm,
            incluirEventos ? n.Eventos.OrderBy(e => e.CriadoEm).Select(EventoNfseDto.De).ToList() : null,
            null, null, null, null, ExtrairLink(n.XmlNfse));
    }

    public sealed record TentativaNfseDto(
        long Id, Guid EmpresaId, Guid? NotaServicoId, int Ambiente, string? Serie, long? NumeroDps,
        string? ChaveIdempotencia, bool Sucesso, string Fase, string? CodigoStatus, string Mensagem,
        string? Detalhe, string? ChaveAcesso, string? TomadorDocumento, string? TomadorNome,
        decimal? ValorServico, DateTime CriadoEm)
    {
        public static TentativaNfseDto De(TentativaEmissaoNfse t) => new(
            t.Id, t.EmpresaId, t.NotaServicoId, t.Ambiente, t.Serie, t.NumeroDps, t.ChaveIdempotencia,
            t.Sucesso, t.Fase, t.CodigoStatus, t.Mensagem, t.Detalhe, t.ChaveAcesso,
            t.TomadorDocumento, t.TomadorNome, t.ValorServico, t.CriadoEm);
    }

    public sealed class CancelamentoNfseRequest
    {
        /// <summary>Nacional: 1=erro na emissao, 2=servico nao prestado, 9=outros.</summary>
        public string CodigoMotivo { get; set; } = "1";
        public string Justificativa { get; set; } = string.Empty;

        /// <summary>
        /// Credenciais do provedor (osascoChaveAutenticacao para Osasco; abrasfUrl, abrasfUsuario,
        /// abrasfSenha para ABRASF). Obrigatorias nesses padroes: nada e lido do servidor.
        /// Padrao e ambiente do cancelamento sao os da propria nota.
        /// </summary>
        public ConfiguracaoNfseRequest? Configuracao { get; set; }
    }

    public sealed class ConsultaNfseRequest
    {
        /// <summary>1=Nacional, 2=ABRASF, 3=Osasco. Obrigatorio quando a nota nao existe localmente.</summary>
        public int? Padrao { get; set; }

        /// <summary>1=producao, 2=homologacao. Obrigatorio quando a nota nao existe localmente.</summary>
        public int? Ambiente { get; set; }

        /// <summary>Credenciais do provedor (obrigatorias para Osasco e ABRASF).</summary>
        public ConfiguracaoNfseRequest? Configuracao { get; set; }
    }

    // --------------------------------------------------------- mapeamento

    /// <summary>Codigo de "NFS-e cancelada" exigido pelo sistema consumidor (status=5 no corpo do 200).</summary>
    private const int StatusCanceladaContrato = 5;

    public static void MapNfse(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("NFS-e");

        // Municípios com provedor identificado no catálogo ACBrNFSeXServicos.ini.
        g.MapGet("/nfse/municipios", (
            string? uf, string? busca, int? padrao, int? ambiente, int? page, int? pageSize) =>
        {
            if (padrao is { } p && !Enum.IsDefined(typeof(PadraoNfse), p))
                return Results.BadRequest(new { erro = "padrao invalido (1=Nacional, 2=ABRASF, 3=Osasco)." });
            if (ambiente is { } a && !Enum.IsDefined(typeof(TipoAmbiente), a))
                return Results.BadRequest(new { erro = "ambiente invalido (1=Producao, 2=Homologacao)." });

            // Sem page/pageSize devolve o catalogo inteiro: a lista e ordenada por UF,
            // entao paginar por padrao esconderia as UFs do fim (SP fica na pagina 29).
            var semPaginacao = page is null && pageSize is null;
            var pagina = Math.Max(1, page ?? 1);
            var tamanhoPagina = Math.Clamp(pageSize ?? 100, 1, 500);
            var query = CatalogoMunicipiosNfse.Instancia.Listar().AsEnumerable();

            if (!string.IsNullOrWhiteSpace(uf))
                query = query.Where(m => string.Equals(m.Uf, uf.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(busca))
                query = query.Where(m => m.CodigoIbge.Contains(busca.Trim(), StringComparison.OrdinalIgnoreCase)
                    || m.Nome.Contains(busca.Trim(), StringComparison.OrdinalIgnoreCase));
            if (padrao is { } padraoFiltro)
                query = query.Where(m => (int?)m.Padrao == padraoFiltro);
            if (ambiente is { } ambienteFiltro)
            {
                var tipoAmbiente = (TipoAmbiente)ambienteFiltro;
                query = query.Where(m => m.Padrao == PadraoNfse.Nacional
                    || !string.IsNullOrWhiteSpace(m.UrlPara(tipoAmbiente)));
            }
            else
            {
                query = query.Where(m => m.Padrao == PadraoNfse.Nacional
                    || !string.IsNullOrWhiteSpace(m.UrlProducao)
                    || !string.IsNullOrWhiteSpace(m.UrlHomologacao));
            }

            var total = query.Count();
            if (semPaginacao)
            {
                pagina = 1;
                tamanhoPagina = Math.Max(total, 1);
            }
            var deslocamento = (int)Math.Min((long)(pagina - 1) * tamanhoPagina, int.MaxValue);
            var municipios = query.Skip(deslocamento).Take(tamanhoPagina)
                .Select(m => new
                {
                    codigoIbge = m.CodigoIbge,
                    nome = m.Nome,
                    uf = m.Uf,
                    provedor = m.Provedor,
                    padrao = (int?)m.Padrao,
                    padraoDescricao = m.Padrao?.ToString(),
                    urlProducao = m.UrlProducao,
                    urlHomologacao = m.UrlHomologacao
                })
                .ToArray();

            return Results.Ok(new
            {
                catalogo = CatalogoMunicipiosNfse.NomeArquivo,
                total,
                page = pagina,
                pageSize = tamanhoPagina,
                totalPaginas = (int)Math.Ceiling(total / (double)tamanhoPagina),
                municipios
            });
        });

        // ---------------------------------------------------------------
        // Emissao: POST DPS -> NFS-e. Header opcional Idempotency-Key.
        // Ambiente, padrao, serie e regime tributario vem SOMENTE do objeto
        // "configuracao" do JSON. O query param ambiente e ignorado.
        // ---------------------------------------------------------------
        g.MapPost("/empresas/{empresaId:guid}/nfse", async (
            Guid empresaId, JsonElement requestBody, ServicoNfse servico, NFeDbContext db, HttpRequest http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var jsonOriginal = requestBody.GetRawText();
            var jsonRecebido = RedigirSegredos(jsonOriginal);
            NfseRequest? req;
            try
            {
                req = JsonSerializer.Deserialize<NfseRequest>(
                    jsonOriginal, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { erro = "O corpo da requisicao nao corresponde ao JSON de uma NFS-e." });
            }
            if (req is null) return Results.BadRequest(new { erro = "Corpo da requisicao ausente." });
            var chaveIdem = http.Headers["Idempotency-Key"].FirstOrDefault();
            var ambienteQueryLegado = http.Query["ambiente"].FirstOrDefault();

            // Registra EXATAMENTE o que o app enviou (como o servidor interpretou o
            // JSON), para responder "foi o app ou o servidor?" em rejeicoes como a
            // E0015 (dCompet > dhEmi). Aparece no journalctl. Sao dados de negocio,
            // sem segredos. dataEmissao/dataCompetencia null aqui = o app NAO enviou
            // e o servidor vai assumir o padrao (agora / data da emissao).
            var log = lf.CreateLogger("NFSe.Emissao");
            if (log.IsEnabled(LogLevel.Information))
            {
                var recebido = RedigirSegredos(System.Text.Json.JsonSerializer.Serialize(req,
                    new System.Text.Json.JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                log.LogInformation(
                    "NFS-e recebida | empresa={EmpresaId} ambienteBody={AmbienteBody} " +
                    "padraoBody={PadraoBody} ambienteQueryLegadoIgnorado={AmbienteQuery} idem={Idem} " +
                    "dataEmissao={DataEmissao} dataCompetencia={DataCompetencia}\nPayload:\n{Payload}",
                    empresaId, req.Configuracao?.Ambiente?.ToString() ?? "(AUSENTE no JSON)",
                    req.Configuracao?.Padrao?.ToString() ?? "(AUSENTE no JSON)",
                    ambienteQueryLegado ?? "(não informado)", chaveIdem ?? "(sem)",
                    req.DataEmissao?.ToString("o") ?? "(null: usa agora)",
                    req.DataCompetencia?.ToString("o") ?? "(null: usa data da emissao)",
                    recebido);
            }

            try
            {
                var r = await servico.EmitirAsync(empresaId, req, chaveIdem, jsonRecebido, ct);
                var dto = await ComAnexos(r.Nota, db, lf, ct) with
                {
                    DuracaoMs = r.DuracaoMs, Reaproveitada = r.Reaproveitada
                };
                return Results.Ok(dto);
            }
            catch (NfseProvedorIndisponivelException ex)
            {
                return Results.Json(
                    new { erro = ex.Message, erros = Array.Empty<string>(), transitorio = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (NfseNegocioException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message, erros = ex.Erros });
            }
            catch (CertificadoException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
            }
        }).Accepts<NfseRequest>("application/json");

        // ---------------------------------------------------------------
        // Compatibilidade: apps antigos ainda enviam PUT /nfse/config. Nada e
        // gravado; a configuracao valida e a do objeto "configuracao" de cada
        // emissao. A resposta lista o que faltaria para emitir.
        // ---------------------------------------------------------------
        g.MapPut("/empresas/{empresaId:guid}/nfse/config", (Guid empresaId, JsonElement corpo) =>
        {
            if (corpo.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { erro = "O corpo deve ser um objeto JSON." });

            bool Tem(params string[] nomes) => corpo.EnumerateObject().Any(p =>
                nomes.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))
                && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Value.GetString())));

            var pendencias = new List<string>();
            if (!Tem("padrao")) pendencias.Add("padrao");
            if (!Tem("ambiente")) pendencias.Add("ambiente");
            if (!Tem("serie", "serieDps")) pendencias.Add("serie");
            if (!Tem("opSimpNac")) pendencias.Add("opSimpNac");
            if (!Tem("regEspTrib")) pendencias.Add("regEspTrib");
            if (!Tem("codigoMunicipioEmissao")) pendencias.Add("codigoMunicipioEmissao");
            if (!Tem("inscricaoMunicipal")) pendencias.Add("inscricaoMunicipal");

            return Results.Ok(new
            {
                empresaId,
                gravado = false,
                aviso = "Nada e gravado no servidor. Envie estes campos no objeto 'configuracao' de cada POST /nfse.",
                pendenciasParaEmitir = pendencias
            });
        });

        // ---------------------------------------------------------------
        // Listagem paginada de NFS-e da empresa.
        // ---------------------------------------------------------------
        g.MapGet("/empresas/{empresaId:guid}/nfse", async (
            Guid empresaId, NFeDbContext db, int? status, int? ambiente, string? busca,
            int? pagina, int? tamanho, CancellationToken ct) =>
        {
            var q = db.NotasServico.AsNoTracking().Where(n => n.EmpresaId == empresaId);
            if (status is not null) q = q.Where(n => (int)n.Status == status);
            if (ambiente is not null) q = q.Where(n => (int)n.Ambiente == ambiente);
            if (!string.IsNullOrWhiteSpace(busca))
            {
                var b = busca.Trim();
                q = q.Where(n => (n.ChaveAcesso != null && n.ChaveAcesso.Contains(b))
                    || (n.NumeroNfse != null && n.NumeroNfse.Contains(b))
                    || (n.TomadorNome != null && n.TomadorNome.Contains(b))
                    || (n.TomadorDocumento != null && n.TomadorDocumento.Contains(b))
                    || n.NumeroDps.ToString() == b);
            }

            var pg = Math.Max(1, pagina ?? 1);
            var tam = Math.Clamp(tamanho ?? 25, 1, 200);
            var total = await q.CountAsync(ct);
            var itens = await q.OrderByDescending(n => n.DataEmissao).ThenByDescending(n => n.NumeroDps)
                .Skip((pg - 1) * tam).Take(tam).ToListAsync(ct);

            return Results.Ok(new { total, pagina = pg, tamanho = tam, itens = itens.Select(n => NotaServicoDto.De(n)) });
        });

        g.MapGet("/nfse/{id:guid}", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var n = await db.NotasServico.AsNoTracking().Include(x => x.Eventos)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            return n is null ? Results.NotFound(new { erro = "NFS-e nao encontrada." })
                : Results.Ok(NotaServicoDto.De(n, incluirEventos: true));
        });

        // ---------------------------------------------------------------
        // Downloads: XML (NFS-e ou DPS assinada) e DANFSe em PDF.
        // ---------------------------------------------------------------
        g.MapGet("/nfse/{id:guid}/xml", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var n = await db.NotasServico.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return Results.NotFound(new { erro = "NFS-e nao encontrada." });
            var xml = n.XmlNfse ?? n.XmlDpsAssinado ?? n.XmlDps;
            if (string.IsNullOrWhiteSpace(xml)) return Results.NotFound(new { erro = "Nota sem XML." });
            var nome = $"{(n.ChaveAcesso ?? n.Id.ToString())}-{(n.XmlNfse is null ? "dps" : "nfse")}.xml";
            return Results.File(Encoding.UTF8.GetBytes(xml), "application/xml", nome);
        });

        g.MapGet("/nfse/{id:guid}/danfse.pdf", async (
            Guid id, NFeDbContext db, ILoggerFactory lf, bool? inline, CancellationToken ct) =>
        {
            var n = await db.NotasServico.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return Results.NotFound(new { erro = "NFS-e nao encontrada." });
            if (n.Status is not (StatusNfse.Autorizada or StatusNfse.Cancelada))
                return Results.UnprocessableEntity(new { erro = "DANFSe disponivel apenas para NFS-e autorizada ou cancelada." });

            var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == n.EmpresaId, ct);
            var logo = await CarregarLogoAsync(db, n.EmpresaId, ct);
            var pdf = GeradorDanfse.Gerar(DanfseDados.Montar(n, empresa, logo),
                n.Status == StatusNfse.Cancelada ? "CANCELADA" : null);
            var nome = $"DANFSe-{n.ChaveAcesso ?? n.Id.ToString()}.pdf";
            return inline == true
                ? Results.File(pdf, "application/pdf")
                : Results.File(pdf, "application/pdf", nome);
        });

        // ---------------------------------------------------------------
        // Consulta por chave de acesso (50 digitos) no provedor. POST porque as
        // credenciais do provedor vem no corpo (o servidor nao guarda credenciais).
        // ---------------------------------------------------------------
        g.MapPost("/empresas/{empresaId:guid}/nfse/consulta/{chave}", async (
            Guid empresaId, string chave, ConsultaNfseRequest? req, ServicoNfse servico,
            CancellationToken ct) =>
        {
            try
            {
                var (r, local) = await servico.ConsultarPorChaveAsync(
                    empresaId, chave, (PadraoNfse?)req?.Padrao, (TipoAmbiente?)req?.Ambiente,
                    req?.Configuracao, ct);
                return Results.Ok(new
                {
                    encontrada = r.Encontrada, cancelada = r.Cancelada, chaveAcesso = r.ChaveAcesso,
                    numeroNfse = r.NumeroNfse, codigo = r.Codigo, motivo = r.Motivo,
                    xmlBase64 = r.XmlNfse is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(r.XmlNfse)),
                    local = local is null ? null : NotaServicoDto.De(local)
                });
            }
            catch (NfseNegocioException ex) { return Results.UnprocessableEntity(new { erro = ex.Message }); }
            catch (CertificadoException ex) { return Results.UnprocessableEntity(new { erro = ex.Message }); }
        });

        // ---------------------------------------------------------------
        // Cancelamento (evento e101101 no padrao nacional).
        // ---------------------------------------------------------------
        g.MapPost("/empresas/{empresaId:guid}/nfse/{id:guid}/cancelar", async (
            Guid empresaId, Guid id, CancelamentoNfseRequest req, ServicoNfse servico, NFeDbContext db,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new { erro = "Corpo ausente." });
            try
            {
                var r = await servico.CancelarAsync(empresaId, id, req.CodigoMotivo, req.Justificativa,
                    req.Configuracao, ct);
                var nota = await db.NotasServico.AsNoTracking().Include(x => x.Eventos).FirstAsync(x => x.Id == id, ct);
                var dto = await ComAnexos(nota, db, lf, ct);
                var evento = EventoNfseDto.De(r.Evento);
                return Results.Ok(new
                {
                    status = StatusCanceladaContrato,
                    statusDescricao = "Cancelada",
                    eventos = dto.Eventos ?? new List<EventoNfseDto> { evento },
                    id = nota.Id,
                    chaveAcesso = nota.ChaveAcesso,
                    numeroNfse = nota.NumeroNfse,
                    codigoMotivo = evento.CodigoMotivo,
                    justificativa = evento.Justificativa,
                    protocolo = evento.Protocolo,
                    dataRegistro = evento.DataRegistro,
                    nota = dto,
                    evento
                });
            }
            catch (NfseNegocioException ex) { return Results.UnprocessableEntity(new { erro = ex.Message }); }
            catch (CertificadoException ex) { return Results.UnprocessableEntity(new { erro = ex.Message }); }
        });

        g.MapGet("/nfse/motivos-cancelamento", () =>
            Results.Ok(GeradorXmlEventoNfse.MotivosCancelamento.Select(k => new { codigo = k.Key, descricao = k.Value })));

        // ---------------------------------------------------------------
        // Tentativas de emissao (auditoria), paginadas.
        // ---------------------------------------------------------------
        g.MapGet("/nfse/tentativas", async (
            NFeDbContext db, Guid? empresaId, bool? sucesso, int? pagina, int? tamanho, CancellationToken ct) =>
        {
            var q = db.TentativasEmissaoNfse.AsNoTracking().AsQueryable();
            if (empresaId is not null) q = q.Where(t => t.EmpresaId == empresaId);
            if (sucesso is not null) q = q.Where(t => t.Sucesso == sucesso);

            var pg = Math.Max(1, pagina ?? 1);
            var tam = Math.Clamp(tamanho ?? 50, 1, 200);
            var total = await q.CountAsync(ct);
            var itens = await q.OrderByDescending(t => t.CriadoEm).Skip((pg - 1) * tam).Take(tam).ToListAsync(ct);
            return Results.Ok(new { total, pagina = pg, tamanho = tam, itens = itens.Select(TentativaNfseDto.De) });
        });

        g.MapGet("/tentativas/nfse/{id:long}/json-recebido", async (
            long id, NFeDbContext db, CancellationToken ct) =>
        {
            var tentativa = await db.TentativasEmissaoNfse.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => new { t.JsonRecebido })
                .FirstOrDefaultAsync(ct);
            return tentativa is null
                ? Results.NotFound(new { erro = "Tentativa de NFS-e nao encontrada." })
                : Results.Ok(new { jsonRecebido = tentativa.JsonRecebido });
        });
    }

    // ---------------------------------------------------------- auxiliares

    /// <summary>
    /// Anexa XML e DANFSe (Base64) ao DTO quando a nota esta autorizada/cancelada,
    /// para o app salvar sem segunda chamada — mesma conveniencia da NF-e. Falha
    /// no PDF nunca derruba a resposta.
    /// </summary>
    private static async Task<NotaServicoDto> ComAnexos(
        NotaServico nota, NFeDbContext db, ILoggerFactory lf, CancellationToken ct)
    {
        var dto = NotaServicoDto.De(nota, incluirEventos: true);
        if (nota.Status is not (StatusNfse.Autorizada or StatusNfse.Cancelada)) return dto;

        var xml = nota.XmlNfse ?? nota.XmlDpsAssinado;
        string? pdf = null;
        try
        {
            var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == nota.EmpresaId, ct);
            var logo = await CarregarLogoAsync(db, nota.EmpresaId, ct);
            pdf = Convert.ToBase64String(GeradorDanfse.Gerar(
                DanfseDados.Montar(nota, empresa, logo),
                nota.Status == StatusNfse.Cancelada ? "CANCELADA" : null));
        }
        catch (Exception ex)
        {
            lf.CreateLogger("Danfse").LogWarning(ex, "Falha ao gerar DANFSe da NFS-e {Id}.", nota.Id);
        }

        return dto with
        {
            XmlBase64 = xml is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)),
            DanfsePdfBase64 = pdf
        };
    }

    private static async Task<byte[]?> CarregarLogoAsync(NFeDbContext db, Guid empresaId, CancellationToken ct)
        => await db.LogosEmpresa.AsNoTracking()
            .Where(l => l.EmpresaId == empresaId).Select(l => l.Conteudo).FirstOrDefaultAsync(ct);

    /// <summary>Mascara as credenciais do provedor antes de logar ou gravar o JSON recebido.</summary>
    internal static string RedigirSegredos(string json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject raiz) return json;
            foreach (var par in raiz)
            {
                if (!par.Key.Equals("configuracao", StringComparison.OrdinalIgnoreCase)
                    || par.Value is not System.Text.Json.Nodes.JsonObject cfg) continue;
                foreach (var campo in cfg.Select(p => p.Key).ToList())
                    if (campo.Equals("osascoChaveAutenticacao", StringComparison.OrdinalIgnoreCase)
                        || campo.Equals("abrasfSenha", StringComparison.OrdinalIgnoreCase))
                        if (cfg[campo] is not null) cfg[campo] = "***";
            }
            return raiz.ToJsonString();
        }
        catch (JsonException) { return json; }
    }

    private static string? SoDigitos(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
