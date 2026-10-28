using Microsoft.EntityFrameworkCore;
using NFe.Api;
using NFe.Api.ClientesApi;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Infrastructure.Servicos;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Soap;

namespace NFe.Api.Endpoints;

public static class EmpresaEndpoints
{
    // Accepts() tambem restringe o roteamento por Content-Type: sem listar os
    // demais formatos, corpo bruto e JSON base64 recebiam HTTP 415.
    private static readonly string[] TiposCorpoLogo =
    [
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp",
        "application/octet-stream", "application/json", "text/plain",
    ];

    private const string ErroCnpjDeOutroApp =
        "Este CNPJ ja esta cadastrado e nao esta liberado para esta chave de app. "
        + "Peca ao administrador da API para liberar a empresa para o seu app.";

    public static void MapEmpresas(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/empresas").WithTags("Empresas");

        // Lista todas as empresas com indicador de certificado ativo.
        // Chave de app: so as empresas liberadas para ela. Token mestre: todas.
        g.MapGet("/", async (HttpContext http, NFeDbContext db, CancellationToken ct) =>
        {
            var chamador = Chamador.De(http);
            var consultaEmpresas = db.Empresas.AsNoTracking();
            if (!chamador.EhMestre)
            {
                var liberadas = chamador.Cliente!.Empresas.ToList();
                consultaEmpresas = consultaEmpresas.Where(e => liberadas.Contains(e.Id));
            }
            var empresas = await consultaEmpresas
                .OrderBy(e => e.RazaoSocial)
                .ToListAsync(ct);

            var ativos = await db.Certificados
                .AsNoTracking()
                .Where(c => c.Ativo)
                .ToListAsync(ct);

            var porEmpresa = ativos
                .GroupBy(c => c.EmpresaId)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(c => c.ValidoAte).First());

            // Ids com logo cadastrado — sem carregar os bytes.
            var comLogo = (await db.LogosEmpresa
                .AsNoTracking()
                .Select(l => l.EmpresaId)
                .ToListAsync(ct)).ToHashSet();

            var dtos = empresas.Select(e =>
                Contratos.EmpresaDto.De(e, porEmpresa.GetValueOrDefault(e.Id), comLogo.Contains(e.Id))
                    .ParaChamador(http));

            return Results.Ok(dtos);
        });

        // Resolve o Id (GUID) da empresa a partir do CNPJ. Util para o app
        // emissor que so conhece o CNPJ: chame isto uma vez, guarde o Id e use
        // nas emissoes. Aceita CNPJ com ou sem mascara (pontos, barra, hifen).
        g.MapGet("/por-cnpj/{cnpj}", async (string cnpj, HttpContext http, NFeDbContext db, CancellationToken ct) =>
        {
            var digitos = SomenteDigitos(cnpj);
            if (digitos.Length != 14)
                return Results.BadRequest(new { erro = "CNPJ deve ter 14 digitos." });

            var e = await db.Empresas.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Cnpj == digitos, ct);
            if (e is null)
                return Results.NotFound(new { erro = "Nenhuma empresa com este CNPJ.", cnpj = digitos });

            var cert = await db.Certificados.AsNoTracking()
                .Where(c => c.EmpresaId == e.Id && c.Ativo)
                .OrderByDescending(c => c.ValidoAte)
                .FirstOrDefaultAsync(ct);

            var temLogo = await db.LogosEmpresa.AsNoTracking().AnyAsync(l => l.EmpresaId == e.Id, ct);
            return Results.Ok(Contratos.EmpresaDto.De(e, cert, temLogo).ParaChamador(http));
        });

        // Detalhe de uma empresa.
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, NFeDbContext db, CancellationToken ct) =>
        {
            var e = await db.Empresas.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();

            var cert = await db.Certificados.AsNoTracking()
                .Where(c => c.EmpresaId == id && c.Ativo)
                .OrderByDescending(c => c.ValidoAte)
                .FirstOrDefaultAsync(ct);

            var temLogo = await db.LogosEmpresa.AsNoTracking().AnyAsync(l => l.EmpresaId == id, ct);
            return Results.Ok(Contratos.EmpresaDto.De(e, cert, temLogo).ParaChamador(http));
        });

        // Cria empresa. Valida CNPJ e unicidade.
        g.MapPost("/", async (
            Contratos.CriarEmpresaRequest req, NFeDbContext db, HttpContext http,
            RepositorioClientesApi clientesApi,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Empresas.Cadastro");
            // Loga EXATAMENTE o que chegou no corpo (sem dados sensiveis) para
            // diagnosticar rejeicoes 400 vindas de qualquer cliente.
            log.LogInformation(
                "POST /api/empresas recebido: cnpj='{Cnpj}' ({DigitosCnpj} digitos), razaoSocial='{Razao}', uf='{Uf}', regime={Regime}",
                req.Cnpj, SomenteDigitos(req.Cnpj ?? string.Empty).Length,
                req.RazaoSocial, req.Uf, req.RegimeTributario);

            var (empresa, erro) = ConstruirEmpresa(req);
            if (empresa is null)
            {
                log.LogWarning("Cadastro de empresa recusado (400): {Erro}", erro);
                return Results.BadRequest(new { erro });
            }

            // CNPJ ja cadastrado: em vez de recusar, COMPARA e ATUALIZA os dados
            // divergentes, avisando o cliente (atualizado=true + lista de campos).
            // Assim recadastrar o mesmo CNPJ vira uma forma pratica de sincronizar
            // o cadastro (ex.: apos uma busca na Receita com dados novos).
            var existente = await db.Empresas
                .FirstOrDefaultAsync(e => e.Cnpj == empresa.Cnpj, ct);
            var chamador = Chamador.De(http);
            // Um app nao pode sobrescrever o cadastro de empresa de outro app.
            if (existente is not null && !chamador.PodeAcessar(existente.Id))
                return Results.Json(new { erro = ErroCnpjDeOutroApp }, statusCode: StatusCodes.Status403Forbidden);
            if (existente is not null)
            {
                var alterados = AtualizarCampos(existente, empresa);
                if (alterados.Count > 0)
                {
                    existente.AtualizadoEm = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                log.LogInformation(
                    "CNPJ {Cnpj} ja cadastrado: {N} campo(s) atualizado(s){Lista}.",
                    existente.Cnpj, alterados.Count,
                    alterados.Count > 0 ? " -> " + string.Join(", ", alterados) : string.Empty);

                var temLogoExistente = await db.LogosEmpresa.AsNoTracking()
                    .AnyAsync(l => l.EmpresaId == existente.Id, ct);
                return Results.Ok(new
                {
                    atualizado = true,
                    camposAlterados = alterados,
                    empresa = Contratos.EmpresaDto.De(existente, null, temLogoExistente),
                });
            }

            db.Empresas.Add(empresa);
            await db.SaveChangesAsync(ct);

            // Empresa criada por um app fica liberada para ele automaticamente.
            if (chamador.Cliente is { } appCriador)
                await clientesApi.ConcederEmpresaAsync(appCriador.Id, empresa.Id, ct);

            return Results.Created(
                $"/api/empresas/{empresa.Id}",
                new
                {
                    atualizado = false,
                    camposAlterados = Array.Empty<string>(),
                    empresa = Contratos.EmpresaDto.De(empresa, null),
                });
        });

        // -------------------------------------------------------------------
        // Onboarding self-service: cria a empresa E importa o certificado A1
        // (.pfx + senha) numa unica chamada multipart. Transacional: se o
        // certificado for invalido (senha errada, CNPJ divergente, vencido), a
        // empresa NAO e criada. Assim a empresa nunca fica cadastrada sem poder
        // emitir. Os campos cadastrais vao como campos de formulario; o arquivo
        // vai no campo "arquivo" e a senha no campo "senha".
        // -------------------------------------------------------------------
        g.MapPost("/onboarding", async (
            HttpRequest request,
            NFeDbContext db,
            ServicoCertificado servicoCert,
            RepositorioClientesApi clientesApi,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { erro = "Envie multipart/form-data." });

            var form = await request.ReadFormAsync(ct);

            var req = new Contratos.CriarEmpresaRequest(
                Cnpj: form["cnpj"].ToString(),
                RazaoSocial: form["razaoSocial"].ToString(),
                NomeFantasia: NuloSeVazio(form["nomeFantasia"].ToString()),
                InscricaoEstadual: form["inscricaoEstadual"].ToString(),
                InscricaoMunicipal: NuloSeVazio(form["inscricaoMunicipal"].ToString()),
                CnaeFiscal: NuloSeVazio(form["cnaeFiscal"].ToString()),
                RegimeTributario: int.TryParse(form["regimeTributario"], out var rt) ? rt : 3,
                Logradouro: form["logradouro"].ToString(),
                Numero: form["numero"].ToString(),
                Complemento: NuloSeVazio(form["complemento"].ToString()),
                Bairro: form["bairro"].ToString(),
                CodigoMunicipio: form["codigoMunicipio"].ToString(),
                NomeMunicipio: form["nomeMunicipio"].ToString(),
                Uf: form["uf"].ToString(),
                Cep: form["cep"].ToString(),
                Telefone: NuloSeVazio(form["telefone"].ToString()),
                UfAutorizadorOverride: NuloSeVazio(form["ufAutorizadorOverride"].ToString()));

            var (empresa, erro) = ConstruirEmpresa(req);
            if (empresa is null)
                return Results.BadRequest(new { erro });

            // Ambiente padrao opcional no onboarding (1 producao / 2 homologacao).
            if (int.TryParse(form["ambientePadrao"], out var amb) && (amb == 1 || amb == 2))
                empresa.AmbientePadrao = (TipoAmbiente)amb;

            // CNPJ ja cadastrado: em vez de recusar, atualiza os dados divergentes
            // e importa o novo certificado (ImportarAsync desativa o ativo anterior
            // e ativa o novo). Consistente com o POST /api/empresas.
            var existente = await db.Empresas
                .FirstOrDefaultAsync(e => e.Cnpj == empresa.Cnpj, ct);
            var chamadorOnboarding = Chamador.De(request.HttpContext);
            if (existente is not null && !chamadorOnboarding.PodeAcessar(existente.Id))
                return Results.Json(new { erro = ErroCnpjDeOutroApp }, statusCode: StatusCodes.Status403Forbidden);

            var arquivo = form.Files["arquivo"];
            var senha = form["senha"].ToString();
            if (arquivo is null || arquivo.Length == 0)
                return Results.BadRequest(new { erro = "Arquivo .pfx do certificado e obrigatorio." });
            if (string.IsNullOrEmpty(senha))
                return Results.BadRequest(new { erro = "Senha do certificado e obrigatoria." });

            using var ms = new MemoryStream();
            await arquivo.CopyToAsync(ms, ct);

            // Transacional: cria (ou atualiza) a empresa e importa o certificado;
            // qualquer falha do certificado desfaz TODAS as alteracoes.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                Empresa alvo;
                bool atualizado;
                List<string> alterados;

                if (existente is null)
                {
                    db.Empresas.Add(empresa);
                    alvo = empresa;
                    atualizado = false;
                    alterados = new List<string>();
                }
                else
                {
                    alterados = AtualizarCampos(existente, empresa);
                    if (alterados.Count > 0)
                        existente.AtualizadoEm = DateTime.UtcNow;
                    alvo = existente;
                    atualizado = true;
                }

                await db.SaveChangesAsync(ct);

                var cert = await servicoCert.ImportarAsync(
                    alvo.Id, ms.ToArray(), senha,
                    NuloSeVazio(form["descricao"].ToString()) ?? arquivo.FileName, ct);

                await tx.CommitAsync(ct);

                if (!atualizado && chamadorOnboarding.Cliente is { } appCriador)
                    await clientesApi.ConcederEmpresaAsync(appCriador.Id, alvo.Id, ct);

                var dto = Contratos.EmpresaDto.De(alvo, cert);
                if (atualizado)
                    return Results.Ok(new { atualizado = true, camposAlterados = alterados, empresa = dto });

                return Results.Created($"/api/empresas/{alvo.Id}",
                    new { atualizado = false, camposAlterados = Array.Empty<string>(), empresa = dto });
            }
            catch (CertificadoException ex)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { erro = ex.Message });
            }
        })
        .DisableAntiforgery();

        // Atualiza dados cadastrais e ambiente padrao.
        g.MapPut("/{id:guid}", async (
            Guid id, Contratos.AtualizarEmpresaRequest req,
            NFeDbContext db, CancellationToken ct) =>
        {
            var e = await db.Empresas.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();

            e.RazaoSocial = req.RazaoSocial?.Trim() ?? e.RazaoSocial;
            e.NomeFantasia = req.NomeFantasia?.Trim();
            e.InscricaoEstadual = SomenteDigitosOuIsento(req.InscricaoEstadual);
            e.RegimeTributario = (RegimeTributario)req.RegimeTributario;
            e.Logradouro = req.Logradouro?.Trim() ?? e.Logradouro;
            e.Numero = req.Numero?.Trim() ?? e.Numero;
            e.Complemento = req.Complemento?.Trim();
            e.Bairro = req.Bairro?.Trim() ?? e.Bairro;
            e.CodigoMunicipio = SomenteDigitos(req.CodigoMunicipio ?? e.CodigoMunicipio);
            e.NomeMunicipio = req.NomeMunicipio?.Trim() ?? e.NomeMunicipio;
            e.Uf = (req.Uf?.Trim().ToUpperInvariant()) ?? e.Uf;
            e.Cep = SomenteDigitos(req.Cep ?? e.Cep);
            e.Telefone = req.Telefone is null ? null : SomenteDigitos(req.Telefone);
            e.UfAutorizadorOverride = string.IsNullOrWhiteSpace(req.UfAutorizadorOverride)
                ? null : req.UfAutorizadorOverride.Trim().ToUpperInvariant();
            e.AmbientePadrao = (TipoAmbiente)req.AmbientePadrao;
            e.Ativa = req.Ativa;
            e.AtualizadoEm = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return Results.Ok(Contratos.EmpresaDto.De(e, null));
        });

        // -------------------------------------------------------------------
        // Define o ambiente padrao da empresa (1 producao / 2 homologacao). O
        // ambiente e uma propriedade de CADA empresa: cada emitente opera no
        // seu proprio ambiente. Promover para producao e um ato explicito.
        // -------------------------------------------------------------------
        g.MapPut("/{id:guid}/ambiente", async (
            Guid id, Contratos.DefinirAmbienteRequest req,
            NFeDbContext db, CancellationToken ct) =>
        {
            if (req.Ambiente != 1 && req.Ambiente != 2)
                return Results.BadRequest(new { erro = "Ambiente deve ser 1 (producao) ou 2 (homologacao)." });

            var e = await db.Empresas.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();

            e.AmbientePadrao = (TipoAmbiente)req.Ambiente;
            e.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var temLogo = await db.LogosEmpresa.AsNoTracking().AnyAsync(l => l.EmpresaId == id, ct);
            return Results.Ok(Contratos.EmpresaDto.De(e, null, temLogo));
        });

        // -------------------------------------------------------------------
        // Pasta (na maquina da API) com os XMLs ja emitidos, organizada em
        // AAAAMM/NFe e AAAAMM/Evento. Usada para completar notas sem XML.
        // Pasta vazia remove a configuracao. A pasta precisa existir no servidor.
        // -------------------------------------------------------------------
        g.MapPut("/{id:guid}/pasta-xml", async (
            Guid id, Contratos.PastaXmlRequest req,
            NFeDbContext db, CancellationToken ct) =>
        {
            var e = await db.Empresas.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound(new { erro = "Empresa nao encontrada." });

            var pasta = req?.Pasta?.Trim();
            if (string.IsNullOrEmpty(pasta))
            {
                e.PastaXmlBackup = null;
            }
            else
            {
                var motivo = LocalizadorXmlPasta.Validar(pasta);
                if (motivo is not null) return Results.BadRequest(new { erro = motivo });
                if (!Directory.Exists(pasta))
                    return Results.BadRequest(new
                    {
                        erro = "A pasta nao existe ou nao esta acessivel no servidor da API."
                    });
                e.PastaXmlBackup = pasta;
            }

            e.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var temLogo = await db.LogosEmpresa.AsNoTracking().AnyAsync(l => l.EmpresaId == id, ct);
            return Results.Ok(Contratos.EmpresaDto.De(e, null, temLogo));
        });

        // Le a pasta configurada e resume o que encontrou (meses, ultimo mes e
        // quantidade de XMLs), para o usuario conferir se apontou o lugar certo.
        g.MapGet("/{id:guid}/pasta-xml/diagnostico", async (
            Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var e = await db.Empresas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound(new { erro = "Empresa nao encontrada." });
            if (string.IsNullOrWhiteSpace(e.PastaXmlBackup))
                return Results.UnprocessableEntity(new { erro = "Nenhuma pasta de XML configurada." });

            var pasta = e.PastaXmlBackup;
            var diag = await Task.Run(() => LocalizadorXmlPasta.Diagnosticar(pasta), ct);
            return Results.Ok(diag);
        });

        // -------------------------------------------------------------------
        // Lista os contadores de numeracao da empresa (por modelo, serie e
        // ambiente). Serve de base para a tela de inicializacao de numeracao.
        // -------------------------------------------------------------------
        g.MapGet("/{id:guid}/series", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var existe = await db.Empresas.AsNoTracking().AnyAsync(e => e.Id == id, ct);
            if (!existe) return Results.NotFound(new { erro = "Empresa nao encontrada." });

            // A numeracao e controlada pelo app cliente (req.Numero na emissao) e
            // gravada em documento_fiscal — a emissao NAO toca serie_numeracao.
            // Portanto a verdade sobre "ultimo numero" e o MAIOR numero realmente
            // emitido, nao o contador. Aqui combinamos as duas fontes:
            //   1) o maior numero de notas que consumiram numeracao de fato
            //      (autorizadas, canceladas ou denegadas — rejeitadas/erro podem
            //       ser reenviadas com o mesmo numero, entao nao contam);
            //   2) contadores inicializados manualmente (migracao), que podem
            //      estar a frente quando ainda nao houve emissao.
            var consumidos = new[]
            {
                StatusDocumento.Autorizado,
                StatusDocumento.Cancelado,
                StatusDocumento.Denegado,
            };

            var emitidos = await db.DocumentosFiscais
                .AsNoTracking()
                .Where(d => d.EmpresaId == id && consumidos.Contains(d.Status))
                .GroupBy(d => new { d.Modelo, d.Serie, d.Ambiente })
                .Select(gp => new
                {
                    gp.Key.Modelo,
                    gp.Key.Serie,
                    gp.Key.Ambiente,
                    MaiorNumero = gp.Max(x => x.Numero),
                    UltimaEmissao = gp.Max(x => x.CriadoEm),
                })
                .ToListAsync(ct);

            var contadores = await db.SeriesNumeracao
                .AsNoTracking()
                .Where(s => s.EmpresaId == id)
                .ToListAsync(ct);

            var mapa = new Dictionary<(int Modelo, int Serie, int Ambiente), Contratos.SerieNumeracaoDto>();

            foreach (var c in contadores)
            {
                var chave = ((int)c.Modelo, c.Serie, (int)c.Ambiente);
                mapa[chave] = new Contratos.SerieNumeracaoDto(
                    c.Id,
                    (int)c.Modelo, c.Modelo.ToString(),
                    c.Serie,
                    (int)c.Ambiente, c.Ambiente.ToString(),
                    c.UltimoNumero, c.UltimoNumero + 1,
                    c.AtualizadoEm);
            }

            foreach (var e in emitidos)
            {
                var chave = ((int)e.Modelo, e.Serie, (int)e.Ambiente);
                mapa.TryGetValue(chave, out var contador);

                var ultimo = Math.Max(contador?.UltimoNumero ?? 0, e.MaiorNumero);
                var atualizadoEm = contador is not null && contador.AtualizadoEm > e.UltimaEmissao
                    ? contador.AtualizadoEm
                    : e.UltimaEmissao;

                mapa[chave] = new Contratos.SerieNumeracaoDto(
                    contador?.Id ?? Guid.Empty,
                    (int)e.Modelo, e.Modelo.ToString(),
                    e.Serie,
                    (int)e.Ambiente, e.Ambiente.ToString(),
                    ultimo, ultimo + 1,
                    atualizadoEm);
            }

            var lista = mapa.Values
                .OrderBy(s => s.Ambiente).ThenBy(s => s.Modelo).ThenBy(s => s.Serie)
                .ToList();

            return Results.Ok(lista);
        });

        // -------------------------------------------------------------------
        // Inicializa/ajusta a numeracao de uma serie: define qual sera o PROXIMO
        // numero a emitir. Uso tipico: migracao de outro sistema. Recusa (422) um
        // proximo numero que repetiria uma NF-e ja emitida na serie.
        // -------------------------------------------------------------------
        g.MapPut("/{id:guid}/series", async (
            Guid id, Contratos.DefinirNumeracaoRequest req,
            NFeDbContext db, ReservadorNumeracao reservador, CancellationToken ct) =>
        {
            if (req is null)
                return Results.BadRequest(new { erro = "Corpo do pedido ausente." });
            if (req.Ambiente != 1 && req.Ambiente != 2)
                return Results.BadRequest(new { erro = "Ambiente deve ser 1 (producao) ou 2 (homologacao)." });
            if (!Enum.IsDefined(typeof(ModeloDocumento), req.Modelo))
                return Results.BadRequest(new { erro = "Modelo de documento invalido." });

            var existe = await db.Empresas.AsNoTracking().AnyAsync(e => e.Id == id, ct);
            if (!existe) return Results.NotFound(new { erro = "Empresa nao encontrada." });

            try
            {
                await reservador.DefinirProximoNumeroAsync(
                    id, (ModeloDocumento)req.Modelo, req.Serie,
                    (TipoAmbiente)req.Ambiente, req.ProximoNumero, ct);

                var atualizado = await db.SeriesNumeracao
                    .AsNoTracking()
                    .FirstAsync(s => s.EmpresaId == id
                        && s.Modelo == (ModeloDocumento)req.Modelo
                        && s.Serie == req.Serie
                        && s.Ambiente == (TipoAmbiente)req.Ambiente, ct);

                return Results.Ok(Contratos.SerieNumeracaoDto.De(atualizado));
            }
            catch (NumeracaoInvalidaException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
            }
        })
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Lista o historico de inutilizacoes de faixa da empresa.
        // -------------------------------------------------------------------
        g.MapGet("/{id:guid}/inutilizacoes", async (
            Guid id, NFeDbContext db, ServicoInutilizacao servico, CancellationToken ct) =>
        {
            var existe = await db.Empresas.AsNoTracking().AnyAsync(e => e.Id == id, ct);
            if (!existe) return Results.NotFound(new { erro = "Empresa nao encontrada." });

            var lista = await servico.ListarAsync(id, ct);
            return Results.Ok(lista.Select(Contratos.InutilizacaoDto.De));
        });

        // -------------------------------------------------------------------
        // Inutiliza uma faixa de numeracao (NfeInutilizacao4). IRREVERSIVEL.
        // Recusa (422) faixa que contenha NF-e ja autorizada/cancelada ou dados
        // invalidos. 200 quando homologada (cStat 102); 422 quando rejeitada.
        // -------------------------------------------------------------------
        g.MapPost("/{id:guid}/inutilizacoes", async (
            Guid id, Contratos.InutilizarRequest req,
            NFeDbContext db, ServicoInutilizacao servico, CancellationToken ct) =>
        {
            if (req is null)
                return Results.BadRequest(new { erro = "Corpo do pedido ausente." });
            if (req.Ambiente != 1 && req.Ambiente != 2)
                return Results.BadRequest(new { erro = "Ambiente deve ser 1 (producao) ou 2 (homologacao)." });
            if (!Enum.IsDefined(typeof(ModeloDocumento), req.Modelo))
                return Results.BadRequest(new { erro = "Modelo de documento invalido." });
            if (string.IsNullOrWhiteSpace(req.Justificativa))
                return Results.BadRequest(new { erro = "Informe a justificativa (15 a 255 caracteres)." });

            var existe = await db.Empresas.AsNoTracking().AnyAsync(e => e.Id == id, ct);
            if (!existe) return Results.NotFound(new { erro = "Empresa nao encontrada." });

            try
            {
                var registro = await servico.InutilizarAsync(
                    id, req.Uf, req.Cnpj, req.UfAutorizadorOverride,
                    (ModeloDocumento)req.Modelo, req.Serie, (TipoAmbiente)req.Ambiente,
                    req.Ano ?? 0, req.NumeroInicial, req.NumeroFinal, req.Justificativa, ct);

                var dto = Contratos.InutilizacaoDto.De(registro);
                return registro.Homologado ? Results.Ok(dto) : Results.UnprocessableEntity(dto);
            }
            catch (InutilizacaoInvalidaException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nao encontrad"))
            {
                return Results.NotFound(new { erro = ex.Message });
            }
            catch (CertificadoException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Problema com o certificado da empresa.",
                    detalhe = ex.Message
                });
            }
            catch (SefazEndpointException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Nao foi possivel resolver o servico de inutilizacao para a UF/ambiente.",
                    detalhe = ex.Message
                });
            }
            catch (SefazSoapException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Falha de comunicacao com a SEFAZ.",
                    detalhe = ex.Message
                });
            }
        })
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Descadastra (exclui) a empresa. Regra FISCAL: empresa que ja emitiu
        // qualquer documento NAO pode ser apagada — a guarda fiscal e obrigatoria
        // e o proprio FK de documento_fiscal e Restrict. Nesse caso retornamos 409
        // e orientamos DESATIVAR (PUT /{id} com ativa=false), que preserva o
        // historico e some da operacao. Empresa sem documentos e removida de fato:
        // logo, certificados e series saem em cascata; as tentativas de emissao
        // (trilha de auditoria, FK Restrict) sao apagadas explicitamente porque,
        // sem documentos, nao ha o que auditar.
        // -------------------------------------------------------------------
        g.MapDelete("/{id:guid}", async (
            Guid id, NFeDbContext db, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Empresas.Descadastro");

            var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (empresa is null) return Results.NotFound();

            var qtdDocumentos = await db.DocumentosFiscais.CountAsync(d => d.EmpresaId == id, ct);
            if (qtdDocumentos > 0)
            {
                log.LogWarning(
                    "Descadastro recusado: empresa {Id} ({Cnpj}) possui {N} documento(s) fiscal(is).",
                    id, empresa.Cnpj, qtdDocumentos);
                return Results.Conflict(new
                {
                    erro =
                        $"Nao e possivel excluir: a empresa possui {qtdDocumentos} documento(s) fiscal(is) " +
                        "e a guarda e obrigatoria por lei. Desative a empresa (ativa=false) para tira-la de " +
                        "operacao mantendo o historico.",
                    documentosFiscais = qtdDocumentos,
                });
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Tentativas de emissao tem FK Restrict; como nao ha documentos, e a
            // trilha de uma empresa que nunca emitiu — pode ser removida.
            await db.TentativasEmissao.Where(t => t.EmpresaId == id).ExecuteDeleteAsync(ct);

            // Logo, certificados e series saem em cascata; consultas de status
            // ficam com empresa_id nulo (FK SetNull).
            db.Empresas.Remove(empresa);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation("Empresa {Id} ({Cnpj}) descadastrada.", id, empresa.Cnpj);
            return Results.Ok(new { removido = true, cnpj = empresa.Cnpj, razaoSocial = empresa.RazaoSocial });
        });

        // ------------------------------ Logo -------------------------------
        // Upload do logotipo do emitente (PNG ou JPEG), impresso no DANFE. Sem
        // logo, o DANFE imprime apenas o texto. Upsert: substitui o anterior.
        // -------------------------------------------------------------------
        // Aceita tres formatos, para nao quebrar apps com clientes diferentes:
        //  1. multipart/form-data (campo "arquivo"; "file", "logo", "imagem" ou
        //     o primeiro arquivo enviado tambem servem);
        //  2. corpo bruto image/png ou image/jpeg;
        //  3. JSON com a imagem em base64 (campo "arquivo", "logo", "imagem",
        //     "base64" ou "conteudo"; prefixo "data:image/...;base64," opcional).
        // A empresa e localizada pelo CNPJ (com ou sem mascara) ou pelo Id (GUID):
        //   /api/empresas/{cnpj-ou-id}/logo  e  /api/empresas/por-cnpj/{cnpj}/logo
        g.MapPost("/{empresa}/logo", (
            string empresa, HttpRequest request, NFeDbContext db,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
            SalvarLogoAsync(empresa, request, db, loggerFactory, ct))
        .Accepts<IFormFile>("multipart/form-data", TiposCorpoLogo)
        .DisableAntiforgery();

        g.MapPost("/por-cnpj/{cnpj}/logo", (
            string cnpj, HttpRequest request, NFeDbContext db,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
            SalvarLogoAsync(cnpj, request, db, loggerFactory, ct))
        .Accepts<IFormFile>("multipart/form-data", TiposCorpoLogo)
        .DisableAntiforgery();

        static async Task<IResult> SalvarLogoAsync(
            string chaveEmpresa, HttpRequest request, NFeDbContext db,
            ILoggerFactory loggerFactory, CancellationToken ct)
        {
            var logger = loggerFactory.CreateLogger("EmpresaLogo");
            const long limite = 3 * 1024 * 1024; // abaixo do max_allowed_packet padrao do MySQL/MariaDB.

            var contentTypeRecebido = request.ContentType ?? "";
            logger.LogInformation(
                "Upload de logo recebido: {Metodo} {Caminho}; empresa={Chave}; Content-Type={Tipo}; Content-Length={Tamanho}; Origem={Ip}; User-Agent={Agente}",
                request.Method, request.Path.Value, chaveEmpresa, contentTypeRecebido,
                request.ContentLength?.ToString() ?? "(chunked)",
                request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                request.Headers.UserAgent.ToString());

            var e = await LocalizarEmpresaAsync(chaveEmpresa, db, rastrear: true, ct);
            if (e is null)
            {
                logger.LogWarning("Upload de logo recusado: empresa {Chave} nao encontrada.", chaveEmpresa);
                return Results.NotFound(new
                {
                    erro = "Empresa nao encontrada. Informe o CNPJ (14 digitos, com ou sem mascara) ou o Id.",
                    empresa = chaveEmpresa,
                });
            }
            var id = e.Id;

            byte[] bytes;

            if (request.HasFormContentType)
            {
                IFormCollection form;
                try
                {
                    form = await request.ReadFormAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex,
                        "Upload de logo da empresa {Id}: falha ao ler o multipart (Content-Type: {Tipo}).",
                        id, contentTypeRecebido);
                    return Results.BadRequest(new
                    {
                        erro = "Nao foi possivel ler o multipart/form-data. Confira o boundary e o corpo enviados.",
                        detalhe = ex.GetBaseException().Message,
                        contentTypeRecebido,
                    });
                }

                logger.LogInformation(
                    "Upload de logo da empresa {Id}: multipart com arquivos=[{Arquivos}] e campos=[{Campos}].",
                    id,
                    string.Join(", ", form.Files.Select(f => $"{f.Name}:{f.FileName}:{f.Length}B:{f.ContentType}")),
                    string.Join(", ", form.Keys));

                var arquivo = form.Files["arquivo"] ?? form.Files["file"]
                    ?? form.Files["logo"] ?? form.Files["imagem"]
                    ?? form.Files.FirstOrDefault();

                if (arquivo is null || arquivo.Length == 0)
                {
                    var campos = string.Join(", ", form.Files.Select(f => f.Name)
                        .Concat(form.Keys).Distinct());
                    logger.LogWarning(
                        "Upload de logo da empresa {Id} sem arquivo. Content-Type: {Tipo}; campos: {Campos}",
                        id, contentTypeRecebido, campos);
                    return Results.BadRequest(new
                    {
                        erro = "Arquivo de imagem e obrigatorio. Envie no campo multipart \"arquivo\".",
                        camposRecebidos = campos,
                    });
                }

                if (arquivo.Length > limite)
                {
                    logger.LogWarning("Upload de logo da empresa {Id} recusado: {Bytes} bytes excedem 3 MB.", id, arquivo.Length);
                    return Results.BadRequest(new { erro = "Imagem muito grande (maximo 3 MB)." });
                }

                using var ms = new MemoryStream();
                await arquivo.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            else if (contentTypeRecebido.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                System.Text.Json.JsonDocument doc;
                try
                {
                    doc = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogWarning(ex, "Upload de logo da empresa {Id} recusado: JSON invalido.", id);
                    return Results.BadRequest(new { erro = "JSON invalido." });
                }

                string? base64 = null;
                using (doc)
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var campo in new[] { "arquivo", "logo", "imagem", "base64", "conteudo" })
                        {
                            if (doc.RootElement.TryGetProperty(campo, out var el)
                                && el.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                base64 = el.GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(base64))
                {
                    logger.LogWarning("Upload de logo da empresa {Id} recusado: JSON sem campo de imagem em base64.", id);
                    return Results.BadRequest(new
                    {
                        erro = "Informe a imagem em base64 em \"arquivo\" (ou \"logo\", \"imagem\", \"base64\", \"conteudo\").",
                    });
                }

                var virgula = base64.IndexOf(',');
                if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && virgula >= 0)
                    base64 = base64[(virgula + 1)..];

                try
                {
                    bytes = Convert.FromBase64String(base64.Trim());
                }
                catch (FormatException ex)
                {
                    logger.LogWarning(ex, "Upload de logo da empresa {Id} recusado: base64 invalido.", id);
                    return Results.BadRequest(new { erro = "Base64 invalido." });
                }

                if (bytes.Length > limite)
                {
                    logger.LogWarning("Upload de logo da empresa {Id} recusado: {Bytes} bytes excedem 3 MB.", id, bytes.Length);
                    return Results.BadRequest(new { erro = "Imagem muito grande (maximo 3 MB)." });
                }
            }
            else
            {
                if (request.ContentLength is > limite)
                {
                    logger.LogWarning("Upload de logo da empresa {Id} recusado: Content-Length {Bytes} excede 3 MB.", id, request.ContentLength);
                    return Results.BadRequest(new { erro = "Imagem muito grande (maximo 3 MB)." });
                }

                logger.LogInformation(
                    "Upload de logo da empresa {Id}: tratando como corpo bruto (Content-Type: {Tipo}).",
                    id, contentTypeRecebido);
                using var ms = new MemoryStream();
                await request.Body.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
                if (bytes.Length == 0)
                {
                    logger.LogWarning(
                        "Upload de logo da empresa {Id} com corpo vazio. Content-Type: {Tipo}",
                        id, contentTypeRecebido);
                    return Results.BadRequest(new
                    {
                        erro = "Corpo vazio. Envie multipart/form-data, JSON com base64 ou a imagem bruta.",
                    });
                }
                if (bytes.Length > limite)
                {
                    logger.LogWarning("Upload de logo da empresa {Id} recusado: {Bytes} bytes excedem 3 MB.", id, bytes.Length);
                    return Results.BadRequest(new { erro = "Imagem muito grande (maximo 3 MB)." });
                }
            }

            var contentType = DetectarImagem(bytes);
            if (contentType is null)
            {
                logger.LogWarning(
                    "Upload de logo da empresa {Id} em formato nao suportado ({Bytes} bytes, Content-Type: {Tipo})",
                    id, bytes.Length, contentTypeRecebido);
                return Results.BadRequest(new
                {
                    erro = "Formato invalido. Envie PNG, JPEG, WebP, GIF ou BMP (nao SVG/PDF).",
                    bytesRecebidos = bytes.Length,
                    contentTypeRecebido,
                });
            }

            try
            {
                var logo = await db.LogosEmpresa.FirstOrDefaultAsync(l => l.EmpresaId == id, ct);
                if (logo is null)
                {
                    logo = new LogoEmpresa { EmpresaId = id };
                    db.LogosEmpresa.Add(logo);
                }
                logo.Conteudo = bytes;
                logo.ContentType = contentType;
                logo.AtualizadoEm = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha ao gravar o logo da empresa {Id} ({Bytes} bytes).", id, bytes.Length);
                return Results.Json(new
                {
                    erro = "Falha ao gravar o logo no banco.",
                    detalhe = ex.GetBaseException().Message,
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            logger.LogInformation(
                "Logo da empresa {Id} gravado: {Bytes} bytes, {ContentType}.", id, bytes.Length, contentType);
            return Results.Ok(new { temLogo = true, contentType });
        }

        // Retorna a imagem do logo para pre-visualizacao no painel.
        g.MapGet("/{empresa}/logo", (string empresa, NFeDbContext db, CancellationToken ct) =>
            LerLogoAsync(empresa, db, ct));
        g.MapGet("/por-cnpj/{cnpj}/logo", (string cnpj, NFeDbContext db, CancellationToken ct) =>
            LerLogoAsync(cnpj, db, ct));

        static async Task<IResult> LerLogoAsync(string chaveEmpresa, NFeDbContext db, CancellationToken ct)
        {
            var e = await LocalizarEmpresaAsync(chaveEmpresa, db, rastrear: false, ct);
            if (e is null) return Results.NotFound();
            var logo = await db.LogosEmpresa.AsNoTracking()
                .FirstOrDefaultAsync(l => l.EmpresaId == e.Id, ct);
            if (logo is null) return Results.NotFound();
            return Results.File(logo.Conteudo, logo.ContentType);
        }

        // Remove o logo. O DANFE volta a imprimir apenas o texto do emitente.
        g.MapDelete("/{empresa}/logo", (string empresa, NFeDbContext db, CancellationToken ct) =>
            RemoverLogoAsync(empresa, db, ct));
        g.MapDelete("/por-cnpj/{cnpj}/logo", (string cnpj, NFeDbContext db, CancellationToken ct) =>
            RemoverLogoAsync(cnpj, db, ct));

        static async Task<IResult> RemoverLogoAsync(string chaveEmpresa, NFeDbContext db, CancellationToken ct)
        {
            var e = await LocalizarEmpresaAsync(chaveEmpresa, db, rastrear: false, ct);
            if (e is null) return Results.NotFound();
            var logo = await db.LogosEmpresa.FirstOrDefaultAsync(l => l.EmpresaId == e.Id, ct);
            if (logo is null) return Results.NoContent();
            db.LogosEmpresa.Remove(logo);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        // Localiza a empresa por Id (GUID) ou por CNPJ (14 digitos, com ou sem mascara).
        static async Task<Empresa?> LocalizarEmpresaAsync(
            string chave, NFeDbContext db, bool rastrear, CancellationToken ct)
        {
            IQueryable<Empresa> consulta = rastrear ? db.Empresas : db.Empresas.AsNoTracking();
            if (Guid.TryParse(chave, out var id))
                return await consulta.FirstOrDefaultAsync(x => x.Id == id, ct);

            var digitos = SomenteDigitos(chave);
            if (digitos.Length != 14) return null;
            return await consulta.FirstOrDefaultAsync(x => x.Cnpj == digitos, ct);
        }

        // Constroi e valida a entidade Empresa a partir do request. Retorna a
        // empresa pronta ou uma mensagem de erro (sem tocar no banco).
        static (Empresa? empresa, string? erro) ConstruirEmpresa(Contratos.CriarEmpresaRequest req)
        {
        var cnpj = SomenteDigitos(req.Cnpj);
        if (cnpj.Length != 14)
            // Ecoa o que chegou (ja sem mascara) para o cliente diagnosticar sem
            // depender de logs: mostra o valor recebido e quantos digitos tinha.
            return (null,
                $"CNPJ deve ter 14 digitos. Recebido: '{req.Cnpj}' " +
                $"({cnpj.Length} digito(s) apos remover a mascara).");
        if (string.IsNullOrWhiteSpace(req.RazaoSocial))
            return (null, "Razao social e obrigatoria.");
        if (req.Uf?.Trim().Length != 2)
            return (null, $"UF deve ter 2 letras. Recebido: '{req.Uf}'.");

            var empresa = new Empresa
            {
                Cnpj = cnpj,
                RazaoSocial = req.RazaoSocial.Trim(),
                NomeFantasia = req.NomeFantasia?.Trim(),
                InscricaoEstadual = SomenteDigitosOuIsento(req.InscricaoEstadual),
                InscricaoMunicipal = req.InscricaoMunicipal?.Trim(),
                CnaeFiscal = req.CnaeFiscal is null ? null : SomenteDigitos(req.CnaeFiscal),
                RegimeTributario = (RegimeTributario)req.RegimeTributario,
                Logradouro = req.Logradouro?.Trim() ?? string.Empty,
                Numero = req.Numero?.Trim() ?? string.Empty,
                Complemento = req.Complemento?.Trim(),
                Bairro = req.Bairro?.Trim() ?? string.Empty,
                CodigoMunicipio = SomenteDigitos(req.CodigoMunicipio ?? string.Empty),
                NomeMunicipio = req.NomeMunicipio?.Trim() ?? string.Empty,
                Uf = req.Uf.Trim().ToUpperInvariant(),
                Cep = SomenteDigitos(req.Cep ?? string.Empty),
                Telefone = req.Telefone is null ? null : SomenteDigitos(req.Telefone),
                UfAutorizadorOverride = string.IsNullOrWhiteSpace(req.UfAutorizadorOverride)
                    ? null : req.UfAutorizadorOverride.Trim().ToUpperInvariant()
            };
            return (empresa, null);
        }

        // Atualiza, em uma empresa JA existente, apenas os campos cujo novo valor
        // foi informado E difere do atual. Nao apaga um campo ja preenchido quando
        // o novo valor vem vazio (o formulario nem sempre envia todos os opcionais,
        // ex.: inscricao municipal / CNAE) — evita perda de dados no recadastro.
        // Retorna os rotulos legiveis dos campos que mudaram, para exibir ao usuario.
        static List<string> AtualizarCampos(Empresa e, Empresa novo)
        {
            var mudou = new List<string>();

            void Texto(string rotulo, Func<string?> ler, Action<string> gravar, string? valor)
            {
                if (!string.IsNullOrWhiteSpace(valor) &&
                    !string.Equals(ler(), valor, StringComparison.Ordinal))
                {
                    gravar(valor);
                    mudou.Add(rotulo);
                }
            }

            Texto("Razao social", () => e.RazaoSocial, v => e.RazaoSocial = v, novo.RazaoSocial);
            Texto("Nome fantasia", () => e.NomeFantasia, v => e.NomeFantasia = v, novo.NomeFantasia);
            Texto("Inscricao estadual", () => e.InscricaoEstadual, v => e.InscricaoEstadual = v, novo.InscricaoEstadual);
            Texto("Inscricao municipal", () => e.InscricaoMunicipal, v => e.InscricaoMunicipal = v, novo.InscricaoMunicipal);
            Texto("CNAE fiscal", () => e.CnaeFiscal, v => e.CnaeFiscal = v, novo.CnaeFiscal);
            Texto("Logradouro", () => e.Logradouro, v => e.Logradouro = v, novo.Logradouro);
            Texto("Numero", () => e.Numero, v => e.Numero = v, novo.Numero);
            Texto("Complemento", () => e.Complemento, v => e.Complemento = v, novo.Complemento);
            Texto("Bairro", () => e.Bairro, v => e.Bairro = v, novo.Bairro);
            Texto("Codigo do municipio", () => e.CodigoMunicipio, v => e.CodigoMunicipio = v, novo.CodigoMunicipio);
            Texto("Municipio", () => e.NomeMunicipio, v => e.NomeMunicipio = v, novo.NomeMunicipio);
            Texto("UF", () => e.Uf, v => e.Uf = v, novo.Uf);
            Texto("CEP", () => e.Cep, v => e.Cep = v, novo.Cep);
            Texto("Telefone", () => e.Telefone, v => e.Telefone = v, novo.Telefone);
            Texto("UF autorizador", () => e.UfAutorizadorOverride, v => e.UfAutorizadorOverride = v, novo.UfAutorizadorOverride);

            if (e.RegimeTributario != novo.RegimeTributario)
            {
                e.RegimeTributario = novo.RegimeTributario;
                mudou.Add("Regime tributario");
            }

            return mudou;
        }

        // Detecta PNG/JPEG pelos magic bytes. Retorna o MIME ou null se nao for
        // uma imagem suportada. Nao confia no content-type enviado pelo cliente.
        static string? DetectarImagem(byte[] b)
        {
            if (b.Length >= 8 &&
                b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        // Formatos que o QuestPDF tambem renderiza no DANFE.
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return "image/webp";
        if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
            return "image/gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M')
            return "image/bmp";
        return null;
        }

        static string? NuloSeVazio(string? v) =>
            string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        static string SomenteDigitos(string v) =>
            new string((v ?? string.Empty).Where(char.IsDigit).ToArray());

        static string SomenteDigitosOuIsento(string v)
        {
            if (string.Equals(v?.Trim(), "ISENTO", StringComparison.OrdinalIgnoreCase))
                return "ISENTO";
            return SomenteDigitos(v ?? string.Empty);
        }
    }
}
