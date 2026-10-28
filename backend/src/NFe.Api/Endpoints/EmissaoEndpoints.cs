using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NFe.Api;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Infrastructure.Servicos;
using NFe.Sefaz.Danfe;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;
using Microsoft.Extensions.Logging;

namespace NFe.Api.Endpoints;

/// <summary>
/// Endpoints de emissao e consulta de NF-e. Expoem o ServicoAutorizacao, que
/// reserva numeracao, gera, assina, valida e transmite a nota em lote sincrono.
/// </summary>
public static class EmissaoEndpoints
{
    public static void MapEmissao(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Emissao NF-e");

        // -------------------------------------------------------------------
        // Emite (autoriza) uma NF-e para a empresa.
        //
        // Cada POST e uma nova tentativa. O cabecalho opcional Idempotency-Key
        // e guardado apenas na auditoria; nao recupera nem bloqueia documentos
        // anteriores. O app cliente controla a numeracao e as retentativas.
        //
        // Ambiente, contingencia e dados do emitente são enviados no JSON. O
        // cadastro persistido da empresa serve apenas para localizar o certificado.
        // -------------------------------------------------------------------
        g.MapPost("/empresas/{empresaId:guid}/nfe", async (
            Guid empresaId,
            JsonElement requestBody,
            ServicoAutorizacao servico,
            RegistradorTentativa registrador,
            NFeDbContext db,
            HttpRequest http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Danfe");
            var chaveIdem = http.Headers["Idempotency-Key"].FirstOrDefault();
            var jsonRecebido = requestBody.GetRawText();
            NotaFiscalRequest? req;
            try
            {
                req = JsonSerializer.Deserialize<NotaFiscalRequest>(
                    jsonRecebido, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { erro = "O corpo da requisicao nao corresponde ao JSON de uma NF-e." });
            }

            var ambienteNum = req?.Configuracao?.Ambiente is { } ambienteJson
                ? (int)ambienteJson
                : 0;

            // Base da linha de auditoria, preenchida com o que ja se conhece do
            // request. Cada desfecho abaixo completa Sucesso/Fase/Mensagem.
            TentativaEmissao NovaTentativa() => new()
            {
                EmpresaId = empresaId,
                Ambiente = ambienteNum,
                Serie = req?.Serie,
                ChaveIdempotencia = chaveIdem,
                DestinatarioDocumento = req?.Destinatario?.Documento,
                DestinatarioNome = req?.Destinatario?.Nome,
                JsonRecebido = jsonRecebido,
                CriadoEm = DateTime.UtcNow,
            };

            async Task RegistrarAsync(
                bool sucesso, string fase, string mensagem,
                string? detalhe = null, DocumentoFiscal? doc = null)
            {
                var t = NovaTentativa();
                t.Sucesso = sucesso;
                t.Fase = fase;
                t.Mensagem = mensagem;
                t.Detalhe = detalhe;
                if (doc is not null)
                {
                    t.DocumentoFiscalId = doc.Id;
                    t.Numero = doc.Numero;
                    t.ChaveAcesso = doc.ChaveAcesso;
                    t.CodigoStatus = doc.CodigoStatusRetorno;
                    t.ValorTotal = doc.ValorTotal;
                }
                await registrador.RegistrarAsync(t, ct);
            }

            if (req is null)
                return Results.BadRequest(new { erro = "Corpo da requisicao ausente." });
            if (req.Itens is null || req.Itens.Count == 0)
            {
                await RegistrarAsync(false, "Validacao", "A nota precisa de ao menos um item.");
                return Results.BadRequest(new { erro = "A nota precisa de ao menos um item." });
            }
            if (req.Serie < 0)
            {
                await RegistrarAsync(false, "Validacao", "Serie invalida.");
                return Results.BadRequest(new { erro = "Serie invalida." });
            }
            if (!OrientacaoDanfeParser.TryParse(req.Configuracao?.OrientacaoDanfe, out var orientacaoDanfe))
            {
                const string msg = "configuracao.orientacaoDanfe invalida. Use \"retrato\" ou \"paisagem\".";
                await RegistrarAsync(false, "Validacao", msg);
                return Results.BadRequest(new { erro = msg });
            }

            try
            {
                var doc = await servico.EmitirAsync(empresaId, req, ct);

                var dto = Contratos.DocumentoFiscalDto.De(doc, incluirEventos: true);
                var autorizada = doc.Status == StatusDocumento.Autorizado;

                // Ao autorizar, a resposta ja carrega o XML autorizado (nfeProc) e
                // o DANFE em PDF, ambos em Base64, para o app salvar sem uma segunda
                // requisicao. Se a geracao do PDF falhar por algum motivo, NAO
                // derrubamos a emissao (a nota ja esta autorizada na SEFAZ): apenas
                // logamos e seguimos com o XML — o app ainda pode gerar o DANFE via
                // GET /nfe/{id}/danfe.pdf depois.
                if (autorizada)
                {
                    var xml = doc.XmlProtocolado ?? doc.XmlAssinado;
                    var logo = await CarregarLogoAsync(db, empresaId, ct);
                    dto = dto with
                    {
                        XmlBase64 = xml is null
                            ? null
                            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml)),
                        DanfePdfBase64 = GerarDanfePdfBase64(xml, logo, logger, orientacaoDanfe)
                    };
                }

                await RegistrarAsync(
                    autorizada,
                    autorizada ? "Autorizada"
                        : doc.Status == StatusDocumento.ErroTransmissao ? "Transmissao"
                        : "Rejeicao",
                    autorizada
                        ? $"Autorizada. Protocolo {doc.Protocolo}."
                        : (doc.MotivoRetorno ?? doc.UltimoErro ?? "Nao autorizada."),
                    detalhe: doc.UltimoErro,
                    doc: doc);

                // 200 quando autorizada; 422 quando a SEFAZ rejeitou/denegou ou
                // houve erro de transmissao — o cliente inspeciona status/motivo.
                return autorizada
                    ? Results.Ok(dto)
                    : Results.UnprocessableEntity(dto);
            }
            catch (RequisicaoInvalidaException ex)
            {
                // Campos obrigatorios ausentes/invalidos: 400 com a lista exata
                // do que corrigir (qual item, qual campo).
                await RegistrarAsync(false, "Validacao",
                    "Dados da nota invalidos.", string.Join(" | ", ex.Problemas));
                return Results.BadRequest(new
                {
                    erro = "Dados da nota invalidos. Corrija os campos indicados e reenvie.",
                    problemas = ex.Problemas
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nao encontrada"))
            {
                await RegistrarAsync(false, "Validacao", ex.Message);
                return Results.NotFound(new { erro = ex.Message });
            }
            catch (CertificadoException ex)
            {
                await RegistrarAsync(false, "Certificado",
                    "Problema com o certificado da empresa.", ex.Message);
                return Results.UnprocessableEntity(new
                {
                    erro = "Problema com o certificado da empresa.",
                    detalhe = ex.Message
                });
            }
            catch (ValidacaoXsdException ex)
            {
                await RegistrarAsync(false, "Schema",
                    "XML nao passou na validacao do schema (XSD).",
                    string.Join(" | ", ex.Erros));
                return Results.UnprocessableEntity(new
                {
                    erro = "XML gerado nao passou na validacao do schema (XSD).",
                    detalhe = ex.Message,
                    erros = ex.Erros
                });
            }
            catch (SefazEndpointException ex)
            {
                await RegistrarAsync(false, "ResolucaoServico",
                    "Nao foi possivel resolver o servico da SEFAZ.", ex.Message);
                return Results.UnprocessableEntity(new
                {
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
                    detalhe = ex.Message
                });
            }
            catch (SefazSoapException ex)
            {
                await RegistrarAsync(false, "Transmissao",
                    "Falha de comunicacao com a SEFAZ.", ex.Message);
                return Results.UnprocessableEntity(new
                {
                    erro = "Falha de comunicacao com a SEFAZ.",
                    detalhe = ex.Message
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Qualquer falha nao prevista tambem precisa guardar o JSON
                // recebido para auditoria/diagnostico, e o app recebe o motivo.
                logger.LogError(ex, "[Emissao] Falha inesperada na emissao da empresa {EmpresaId}.", empresaId);
                await RegistrarAsync(false, "ErroInterno",
                    "Erro interno ao emitir a NF-e.", ex.Message);
                return Results.Json(new
                {
                    erro = "Erro interno ao emitir a NF-e.",
                    detalhe = ex.Message
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("EmitirNFe")
        .Accepts<NotaFiscalRequest>("application/json")
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Lista os documentos de uma empresa (mais recentes primeiro).
        // -------------------------------------------------------------------
        g.MapGet("/empresas/{empresaId:guid}/nfe", async (
            Guid empresaId, NFeDbContext db, int? limite, CancellationToken ct) =>
        {
            var top = Math.Clamp(limite ?? 50, 1, 200);
            var docs = await db.DocumentosFiscais
                .AsNoTracking()
                .Where(d => d.EmpresaId == empresaId)
                .OrderByDescending(d => d.CriadoEm)
                .Take(top)
                .ToListAsync(ct);

            return Results.Ok(docs.Select(d => Contratos.DocumentoFiscalDto.De(d)));
        });

        // -------------------------------------------------------------------
        // RECONCILIACAO POR NUMERO — responde "o que aconteceu com a NF-e N?".
        //
        // Caso de uso: o app emissor mandou a nota, nao recebeu o retorno
        // (timeout/queda/base limpa) e travou sem saber se pode reenviar. O app
        // conhece o NUMERO, mas nao o Id nem a chave de acesso, e por isso nao
        // conseguia consultar nada aqui.
        //
        // Responde SEMPRE 200 (nunca 404) para o chamador distinguir "nota
        // inexistente" de "rota inexistente":
        //   { existe: false } -> nada registrado: nao foi enviada, pode emitir.
        //   { existe: true, documento, historico } -> veja status e chave.
        // -------------------------------------------------------------------
        g.MapGet("/empresas/{empresaId:guid}/nfe/por-numero/{numero:long}", async (
            Guid empresaId, long numero, NFeDbContext db, int? serie, CancellationToken ct) =>
        {
            var q = db.DocumentosFiscais.AsNoTracking()
                .Where(d => d.EmpresaId == empresaId && d.Numero == numero);

            // Serie e opcional: sem ela, considera qualquer serie da empresa.
            if (serie is not null)
                q = q.Where(d => d.Serie == serie);

            // Mais recente primeiro: apos retentativas, o ultimo estado e o que vale.
            var docs = await q.OrderByDescending(d => d.CriadoEm).ToListAsync(ct);

            if (docs.Count == 0)
            {
                return Results.Ok(new
                {
                    existe = false,
                    numero,
                    serie,
                    podeEmitir = true,
                    mensagem =
                        "Nenhuma NF-e com este numero esta registrada para esta empresa. " +
                        "Esta API nao a enviou a SEFAZ, portanto e seguro emitir. " +
                        "Se a base foi limpa e a nota pode ter sido autorizada antes disso, " +
                        "confirme no portal da SEFAZ (ou via DistDFe) antes de reemitir."
                });
            }

            var atual = docs[0];
            return Results.Ok(new
            {
                existe = true,
                numero,
                serie,
                // Indicador informativo; o POST não consulta nem bloqueia com base
                // no histórico. Uma rejeição permite ao app iniciar nova tentativa.
                podeEmitir = atual.Status == StatusDocumento.Rejeitado,
                status = atual.Status.ToString(),
                documento = Contratos.DocumentoFiscalDto.De(atual, incluirEventos: true),
                // Mais de um registro no mesmo numero (ex.: rejeicao + reenvio):
                // devolvemos todos para auditoria.
                historico = docs.Count > 1
                    ? docs.Select(d => Contratos.DocumentoFiscalDto.De(d))
                    : null
            });
        })
        .WithName("ConsultarNFePorNumero");

        // -------------------------------------------------------------------
        // Lista as TENTATIVAS de emissao de uma empresa (mais recentes primeiro).
        // Diferente de /nfe, inclui as que falharam antes de virar documento —
        // e onde o operador ve "por que a nota que meu app mandou deu erro".
        // Filtro opcional: apenasFalhas=true.
        // -------------------------------------------------------------------
        g.MapGet("/empresas/{empresaId:guid}/tentativas", async (
            Guid empresaId, NFeDbContext db, int? limite, bool? apenasFalhas,
            CancellationToken ct) =>
        {
            var top = Math.Clamp(limite ?? 50, 1, 200);
            var q = db.TentativasEmissao.AsNoTracking()
                .Where(t => t.EmpresaId == empresaId);
            if (apenasFalhas == true)
                q = q.Where(t => !t.Sucesso);

            var itens = await q
                .OrderByDescending(t => t.CriadoEm)
                .Take(top)
                .ToListAsync(ct);

            return Results.Ok(itens.Select(Contratos.TentativaEmissaoDto.De));
        });

        g.MapGet("/tentativas/nfe/{id:long}/json-recebido", async (
            long id, NFeDbContext db, CancellationToken ct) =>
        {
            var tentativa = await db.TentativasEmissao.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => new { t.JsonRecebido })
                .FirstOrDefaultAsync(ct);
            return tentativa is null
                ? Results.NotFound(new { erro = "Tentativa de NF-e nao encontrada." })
                : Results.Ok(new { jsonRecebido = tentativa.JsonRecebido });
        });

        // Atalho no detalhe da nota: retorna o JSON original da tentativa mais
        // recente que gerou este documento.
        g.MapGet("/nfe/{id:guid}/json-recebido", async (
            Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var tentativa = await db.TentativasEmissao.AsNoTracking()
                .Where(t => t.DocumentoFiscalId == id)
                .OrderByDescending(t => t.Id)
                .Select(t => new { t.JsonRecebido })
                .FirstOrDefaultAsync(ct);
            return tentativa is null
                ? Results.NotFound(new { erro = "Tentativa de NF-e nao encontrada para este documento." })
                : Results.Ok(new { jsonRecebido = tentativa.JsonRecebido });
        });

        // -------------------------------------------------------------------
        // Detalhe de um documento, com a trilha de eventos.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .Include(d => d.Eventos)
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            return doc is null
                ? Results.NotFound(new { erro = "Documento nao encontrado." })
                : Results.Ok(Contratos.DocumentoFiscalDto.De(doc, incluirEventos: true));
        });

        // -------------------------------------------------------------------
        // Baixa o XML autorizado (nfeProc, com o protocolo anexado). Caso ainda
        // nao autorizado, devolve o XML assinado como fallback.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/xml", async (
            Guid id, NFeDbContext db, ServicoConsultaProtocolo consulta, CancellationToken ct) =>
        {
            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            var xml = doc.XmlProtocolado ?? doc.XmlAssinado;
            if (string.IsNullOrEmpty(doc.XmlProtocolado) && PrecisaXmlDaPasta(doc))
                xml = await consulta.TentarCompletarDaPastaAsync(doc.EmpresaId, doc.ChaveAcesso!, ct) ?? xml;
            if (string.IsNullOrEmpty(xml))
                return Results.NotFound(new { erro = "Documento ainda sem XML." });

            var nome = $"{doc.ChaveAcesso ?? doc.Id.ToString()}-procNFe.xml";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(xml),
                "application/xml", nome);
        });

        // -------------------------------------------------------------------
        // Gera e baixa o DANFE em PDF a partir do XML autorizado. O DANFE NAO vem
        // da SEFAZ: e produzido pelo emitente a partir do nfeProc. So faz sentido
        // para nota autorizada (precisa do protocolo).
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/danfe.pdf", async (
            Guid id, string? orientacao, NFeDbContext db, ServicoConsultaProtocolo consulta, CancellationToken ct) =>
        {
            if (!OrientacaoDanfeParser.TryParse(orientacao, out var orientacaoDanfe))
                return Results.BadRequest(new { erro = "orientacao invalida. Use \"retrato\" ou \"paisagem\"." });

            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            var xml = doc.XmlProtocolado ?? doc.XmlAssinado;
            if (string.IsNullOrEmpty(doc.XmlProtocolado) && PrecisaXmlDaPasta(doc))
                xml = await consulta.TentarCompletarDaPastaAsync(doc.EmpresaId, doc.ChaveAcesso!, ct) ?? xml;
            if (string.IsNullOrEmpty(xml))
                return Results.NotFound(new { erro = "Documento ainda sem XML para gerar o DANFE." });

            try
            {
                var logo = await CarregarLogoAsync(db, doc.EmpresaId, ct);
                var pdf = GeradorDanfe.GerarDeXml(xml, logo, orientacao: orientacaoDanfe);
                var nome = $"{doc.ChaveAcesso ?? doc.Id.ToString()}-danfe.pdf";
                return Results.File(pdf, "application/pdf", nome);
            }
            catch (Exception ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Nao foi possivel gerar o DANFE a partir do XML.",
                    detalhe = ex.Message
                });
            }
        });

        // -------------------------------------------------------------------
        // Logo do EMITENTE desta nota, para o DANFE renderizado no navegador
        // (rota /danfe/{id}). A pagina do DANFE conhece apenas o id da nota, nao o
        // da empresa; este endpoint resolve o emitente e devolve a imagem. 404
        // quando nao ha logo — o front simplesmente nao mostra a marca.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/logo", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var doc = await db.DocumentosFiscais.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null)
                return Results.NotFound();

            var logo = await db.LogosEmpresa.AsNoTracking()
                .FirstOrDefaultAsync(l => l.EmpresaId == doc.EmpresaId, ct);
            if (logo is null)
                return Results.NotFound();

            return Results.File(logo.Conteudo, logo.ContentType);
        });

        // -------------------------------------------------------------------
        // Reenvia um documento nao autorizado (ErroTransmissao/Assinado/Rejeitado)
        // reaproveitando o MESMO XML assinado e a MESMA numeracao. Idempotente: se
        // ja estiver autorizado, retorna 200 com o documento como esta.
        // -------------------------------------------------------------------
        g.MapPost("/nfe/{id:guid}/reenviar", async (
            Guid id,
            NFeDbContext db,
            ServicoAutorizacao servico,
            ILoggerFactory loggerFactory,
            bool? contingencia,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Danfe");

            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            try
            {
                var reenviado = await servico.ReenviarAsync(
                    doc.EmpresaId, id, contingencia ?? false, ct: ct);

                var dto = Contratos.DocumentoFiscalDto.De(reenviado, incluirEventos: true);
                var autorizada = reenviado.Status == StatusDocumento.Autorizado;

                if (autorizada)
                {
                    var xml = reenviado.XmlProtocolado ?? reenviado.XmlAssinado;
                    var logo = await CarregarLogoAsync(db, doc.EmpresaId, ct);
                    dto = dto with
                    {
                        XmlBase64 = xml is null
                            ? null
                            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml)),
                        DanfePdfBase64 = GerarDanfePdfBase64(xml, logo, logger)
                    };
                }

                return autorizada
                    ? Results.Ok(dto)
                    : Results.UnprocessableEntity(dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
            }
            catch (CertificadoException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Problema com o certificado da empresa.",
                    detalhe = ex.Message
                });
            }
        })
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Baixa a RESPOSTA BRUTA da SEFAZ (envelope retEnviNFe completo). Este e
        // o "log completo" para diagnosticar uma rejeicao — existe mesmo quando
        // a nota nao foi autorizada e nao ha nfeProc para baixar.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/retorno-sefaz", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            if (string.IsNullOrEmpty(doc.XmlRetornoSefaz))
                return Results.NotFound(new
                {
                    erro = "Ainda nao ha resposta da SEFAZ para este documento.",
                });

            var nome = $"{doc.ChaveAcesso ?? doc.Id.ToString()}-retorno-sefaz.xml";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(doc.XmlRetornoSefaz),
                "application/xml", nome);
        });

        // -------------------------------------------------------------------
        // Consulta a situacao de uma NF-e na SEFAZ pela chave de acesso.
        // Usa o certificado da empresa (mTLS). A UF e o ambiente sao derivados
        // da propria chave; "ambiente", "ufAutorizadorOverride" e "contingencia"
        // opcionais vao no JSON. Nada do cadastro da empresa e lido.
        // -------------------------------------------------------------------
        g.MapPost("/empresas/{empresaId:guid}/nfe/consultar", async (
            Guid empresaId,
            Contratos.ConsultaProtocoloRequest req,
            ServicoConsultaProtocolo servico,
            NFeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ChaveAcesso))
                return Results.BadRequest(new { erro = "Informe a chave de acesso (44 digitos)." });

            TipoAmbiente? ambienteOverride = req.Ambiente switch
            {
                1 => TipoAmbiente.Producao,
                2 => TipoAmbiente.Homologacao,
                _ => null
            };

            try
            {
                var s = await servico.ConsultarESincronizarAsync(
                    empresaId, req.ChaveAcesso, ambienteOverride,
                    req.UfAutorizadorOverride, req.Contingencia ?? false, ct: ct);

                var dto = Contratos.ConsultaProtocoloDto.De(s.Resultado) with
                {
                    CadastradaAgora = s.CadastradaAgora,
                    Aviso = s.Aviso,
                    XmlOrigem = s.XmlOrigem,
                    XmlPendente = s.XmlPendente
                };

                if (s.Documento is { } doc)
                {
                    dto = dto with { Documento = Contratos.DocumentoFiscalDto.De(doc) };

                    // So o nfeProc (com protocolo) vale como XML autorizado.
                    if (!string.IsNullOrEmpty(doc.XmlProtocolado))
                    {
                        dto = dto with
                        {
                            XmlBase64 = Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes(doc.XmlProtocolado))
                        };

                        if (s.Resultado.Autorizada)
                        {
                            var logo = await CarregarLogoAsync(db, empresaId, ct);
                            dto = dto with
                            {
                                DanfePdfBase64 = GerarDanfePdfBase64(
                                    doc.XmlProtocolado, logo, loggerFactory.CreateLogger("Danfe"))
                            };
                        }
                    }
                }

                return Results.Ok(dto);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nao encontrada"))
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
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
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
        .WithName("ConsultarNFe")
        .DisableAntiforgery();

        // Anexa o XML (NFe assinada ou nfeProc) a uma nota emitida fora da API.
        // A consulta SEFAZ nao devolve o XML e a distribuicao DF-e nao entrega ao
        // emitente a propria nota; sem isso a nota fica sem XML e sem DANFE.
        g.MapPost("/empresas/{empresaId:guid}/nfe/xml", async (
            Guid empresaId,
            Contratos.AnexarXmlRequest req,
            ServicoConsultaProtocolo servico,
            NFeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ChaveAcesso))
                return Results.BadRequest(new { erro = "Informe a chave de acesso (44 digitos)." });

            string? xml = req.Xml;
            if (string.IsNullOrWhiteSpace(xml) && !string.IsNullOrWhiteSpace(req.XmlBase64))
            {
                try { xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(req.XmlBase64)); }
                catch (FormatException)
                {
                    return Results.BadRequest(new { erro = "xmlBase64 nao e um Base64 valido." });
                }
            }
            if (string.IsNullOrWhiteSpace(xml))
                return Results.BadRequest(new { erro = "Informe 'xml' ou 'xmlBase64'." });

            TipoAmbiente? ambiente = req.Ambiente switch
            {
                1 => TipoAmbiente.Producao,
                2 => TipoAmbiente.Homologacao,
                _ => null
            };

            try
            {
                var r = await servico.AnexarXmlAsync(empresaId, req.ChaveAcesso, xml, ambiente, ct: ct);
                var doc = r.Documento;

                string? danfe = null;
                if (!string.IsNullOrEmpty(doc.XmlProtocolado))
                {
                    var logo = await CarregarLogoAsync(db, empresaId, ct);
                    danfe = GerarDanfePdfBase64(doc.XmlProtocolado, logo, loggerFactory.CreateLogger("Danfe"));
                }

                return Results.Ok(new
                {
                    documento = Contratos.DocumentoFiscalDto.De(doc),
                    cadastradaAgora = r.CadastradaAgora,
                    aviso = r.Aviso,
                    xmlBase64 = string.IsNullOrEmpty(doc.XmlProtocolado)
                        ? null
                        : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(doc.XmlProtocolado)),
                    danfePdfBase64 = danfe
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nao encontrada"))
            {
                return Results.NotFound(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
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
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
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
        .WithName("AnexarXmlNFe")
        .DisableAntiforgery();

        // Procura o XML da chave na pasta de backup configurada na empresa
        // (AAAAMM/NFe) e o anexa a nota, sem o front precisar enviar o arquivo.
        g.MapPost("/empresas/{empresaId:guid}/nfe/xml/buscar-pasta", async (
            Guid empresaId,
            Contratos.BuscarXmlPastaRequest req,
            ServicoConsultaProtocolo servico,
            NFeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ChaveAcesso))
                return Results.BadRequest(new { erro = "Informe a chave de acesso (44 digitos)." });

            try
            {
                var r = await servico.AnexarDaPastaAsync(empresaId, req.ChaveAcesso, ct);
                var doc = r.Documento;

                string? danfe = null;
                if (!string.IsNullOrEmpty(doc.XmlProtocolado))
                {
                    var logo = await CarregarLogoAsync(db, empresaId, ct);
                    danfe = GerarDanfePdfBase64(doc.XmlProtocolado, logo, loggerFactory.CreateLogger("Danfe"));
                }

                return Results.Ok(new
                {
                    documento = Contratos.DocumentoFiscalDto.De(doc),
                    cadastradaAgora = r.CadastradaAgora,
                    aviso = r.Aviso,
                    xmlBase64 = string.IsNullOrEmpty(doc.XmlProtocolado)
                        ? null
                        : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(doc.XmlProtocolado)),
                    danfePdfBase64 = danfe
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Empresa") && ex.Message.Contains("nao encontrada"))
            {
                return Results.NotFound(new { erro = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { erro = ex.Message });
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
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
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
        .WithName("BuscarXmlNaPasta")
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Cancela uma NF-e autorizada (evento 110111). Exige justificativa de
        // 15 a 255 caracteres. Idempotente: nota ja cancelada retorna 200 com o
        // evento homologado. 200 quando homologado; 422 quando a SEFAZ rejeitou.
        // -------------------------------------------------------------------
        g.MapPost("/nfe/{id:guid}/cancelar", async (
            Guid id,
            Contratos.CancelamentoRequest req,
            NFeDbContext db,
            ServicoEventos servico,
            bool? contingencia,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Justificativa))
                return Results.BadRequest(new
                {
                    erro = "Informe a justificativa do cancelamento (15 a 255 caracteres)."
                });

            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            try
            {
                var evento = await servico.CancelarAsync(
                    doc.EmpresaId, id, req.Justificativa, contingencia ?? false, ct);

                var dto = Contratos.EventoFiscalDto.De(evento);
                return evento.Homologado
                    ? Results.Ok(dto)
                    : Results.UnprocessableEntity(dto);
            }
            catch (EventoInvalidoException ex)
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
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
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
        .WithName("CancelarNFe")
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Registra uma carta de correcao (evento 110110) para uma NF-e
        // autorizada. Exige a correcao de 15 a 1000 caracteres. A sequencia e
        // calculada pelo backend (ate 20 por nota). Nao altera a situacao da
        // nota. 200 quando homologada; 422 quando a SEFAZ rejeitou.
        // -------------------------------------------------------------------
        g.MapPost("/nfe/{id:guid}/carta-correcao", async (
            Guid id,
            Contratos.CartaCorrecaoRequest req,
            NFeDbContext db,
            ServicoEventos servico,
            bool? contingencia,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Correcao))
                return Results.BadRequest(new
                {
                    erro = "Informe o texto da correcao (15 a 1000 caracteres)."
                });

            var doc = await db.DocumentosFiscais
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            try
            {
                var evento = await servico.CorrigirAsync(
                    doc.EmpresaId, id, req.Correcao, contingencia ?? false, ct);

                var dto = Contratos.EventoFiscalDto.De(evento);
                return evento.Homologado
                    ? Results.Ok(dto)
                    : Results.UnprocessableEntity(dto);
            }
            catch (EventoInvalidoException ex)
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
                    erro = "Nao foi possivel resolver o servico da SEFAZ para a UF/ambiente.",
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
        .WithName("CartaCorrecaoNFe")
        .DisableAntiforgery();

        // -------------------------------------------------------------------
        // Lista os eventos fiscais (cancelamento, CC-e) de um documento.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/eventos", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var eventos = await db.EventosFiscais
                .AsNoTracking()
                .Where(e => e.DocumentoFiscalId == id)
                .OrderByDescending(e => e.CriadoEm)
                .ToListAsync(ct);

            return Results.Ok(eventos.Select(Contratos.EventoFiscalDto.De));
        });

        // -------------------------------------------------------------------
        // Baixa o XML de guarda do evento homologado (procEventoNFe). Retorna o
        // cancelamento homologado mais recente por padrao.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/cancelamento.xml", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var evento = await db.EventosFiscais
                .AsNoTracking()
                .Where(e => e.DocumentoFiscalId == id && e.TipoEvento == 110111 && e.Homologado)
                .OrderByDescending(e => e.CriadoEm)
                .FirstOrDefaultAsync(ct);

            if (evento is null || string.IsNullOrEmpty(evento.XmlEvento))
                return Results.NotFound(new { erro = "Nao ha cancelamento homologado para este documento." });

            var nome = $"{evento.ChaveAcesso}-procEventoCanc.xml";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(evento.XmlEvento),
                "application/xml", nome);
        });

        // -------------------------------------------------------------------
        // DANFE da nota carimbada com "CANCELADA" (marca d'agua diagonal).
        // So gera quando existe cancelamento homologado; caso contrario 422.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/danfe-cancelamento.pdf", async (Guid id, string? orientacao, NFeDbContext db, CancellationToken ct) =>
        {
            if (!OrientacaoDanfeParser.TryParse(orientacao, out var orientacaoDanfe))
                return Results.BadRequest(new { erro = "orientacao invalida. Use \"retrato\" ou \"paisagem\"." });

            var doc = await db.DocumentosFiscais.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null)
                return Results.NotFound(new { erro = "Documento nao encontrado." });

            var cancelado = await db.EventosFiscais.AsNoTracking()
                .AnyAsync(e => e.DocumentoFiscalId == id && e.TipoEvento == 110111 && e.Homologado, ct);
            if (!cancelado)
                return Results.UnprocessableEntity(new { erro = "Documento nao possui cancelamento homologado." });

            var xml = doc.XmlProtocolado ?? doc.XmlAssinado;
            if (string.IsNullOrEmpty(xml))
                return Results.NotFound(new { erro = "Documento sem XML para gerar o DANFE." });

            try
            {
                var logo = await CarregarLogoAsync(db, doc.EmpresaId, ct);
                var pdf = GeradorDanfe.GerarDeXml(xml, logo, marcaDagua: "CANCELADA", orientacao: orientacaoDanfe);
                var nome = $"{doc.ChaveAcesso ?? doc.Id.ToString()}-danfe-cancelada.pdf";
                return Results.File(pdf, "application/pdf", nome);
            }
            catch (Exception ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Nao foi possivel gerar o DANFE cancelado.",
                    detalhe = ex.Message
                });
            }
        });

        // -------------------------------------------------------------------
        // Comprovante do EVENTO de cancelamento em PDF (protocolo, data/hora,
        // justificativa). Usa o cancelamento homologado mais recente.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/cancelamento.pdf", async (Guid id, NFeDbContext db, CancellationToken ct) =>
        {
            var evento = await db.EventosFiscais.AsNoTracking()
                .Where(e => e.DocumentoFiscalId == id && e.TipoEvento == 110111 && e.Homologado)
                .OrderByDescending(e => e.CriadoEm)
                .FirstOrDefaultAsync(ct);
            if (evento is null)
                return Results.NotFound(new { erro = "Nao ha cancelamento homologado para este documento." });

            var doc = await db.DocumentosFiscais.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            var xml = doc?.XmlProtocolado ?? doc?.XmlAssinado;
            if (doc is null || string.IsNullOrEmpty(xml))
                return Results.NotFound(new { erro = "Documento sem XML para montar o comprovante." });

            try
            {
                var logo = await CarregarLogoAsync(db, doc.EmpresaId, ct);
                var canc = new DadosCancelamento(
                    ProtocoloEvento: evento.ProtocoloEvento,
                    DataRegistro: evento.DataRegistro,
                    CodigoStatus: evento.CodigoStatus,
                    Motivo: evento.Motivo,
                    Justificativa: evento.Justificativa,
                    ProtocoloNFe: evento.ProtocoloNFe);
                var pdf = GeradorComprovanteCancelamento.GerarDeXml(xml, canc, logo);
                var nome = $"{evento.ChaveAcesso}-comprovante-cancelamento.pdf";
                return Results.File(pdf, "application/pdf", nome);
            }
            catch (Exception ex)
            {
                return Results.UnprocessableEntity(new
                {
                    erro = "Nao foi possivel gerar o comprovante de cancelamento.",
                    detalhe = ex.Message
                });
            }
        });

        // -------------------------------------------------------------------
        // Baixa o XML de guarda de uma carta de correcao homologada
        // (procEventoNFe). Retorna a CC-e homologada mais recente por padrao;
        // aceita ?seq=N para uma sequencia especifica.
        // -------------------------------------------------------------------
        g.MapGet("/nfe/{id:guid}/carta-correcao.xml", async (
            Guid id, int? seq, NFeDbContext db, CancellationToken ct) =>
        {
            var query = db.EventosFiscais
                .AsNoTracking()
                .Where(e => e.DocumentoFiscalId == id && e.TipoEvento == 110110 && e.Homologado);

            if (seq is not null)
                query = query.Where(e => e.SequenciaEvento == seq);

            var evento = await query
                .OrderByDescending(e => e.SequenciaEvento)
                .ThenByDescending(e => e.CriadoEm)
                .FirstOrDefaultAsync(ct);

            if (evento is null || string.IsNullOrEmpty(evento.XmlEvento))
                return Results.NotFound(new { erro = "Nao ha carta de correcao homologada para este documento." });

            var nome = $"{evento.ChaveAcesso}-procEventoCCe{evento.SequenciaEvento:D2}.xml";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(evento.XmlEvento),
                "application/xml", nome);
        });
    }

    /// <summary>
    /// Gera o DANFE em PDF (Base64) a partir do XML autorizado. Nunca lanca: se a
    /// geracao falhar, loga e retorna null — a nota ja esta autorizada e o app
    /// pode buscar o DANFE depois via GET /nfe/{id}/danfe.pdf.
    /// </summary>
    /// <summary>Nota autorizada/cancelada, com chave, mas sem o nfeProc na base.</summary>
    private static bool PrecisaXmlDaPasta(DocumentoFiscal doc) =>
        !string.IsNullOrEmpty(doc.ChaveAcesso)
        && doc.Status is StatusDocumento.Autorizado or StatusDocumento.Cancelado;

    private static string? GerarDanfePdfBase64(
        string? xml, byte[]? logo, ILogger logger,
        OrientacaoDanfe orientacao = OrientacaoDanfe.Retrato)
    {
        if (string.IsNullOrEmpty(xml))
            return null;
        try
        {
            return Convert.ToBase64String(GeradorDanfe.GerarDeXml(xml, logo, orientacao: orientacao));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Danfe] Falha ao gerar PDF do DANFE; retornando so o XML.");
            return null;
        }
    }

    /// <summary>
    /// Carrega os bytes do logo da empresa para impressao no DANFE. Retorna null
    /// quando a empresa nao tem logo — nesse caso o DANFE imprime so o texto.
    /// </summary>
    private static async Task<byte[]?> CarregarLogoAsync(
        NFeDbContext db, Guid empresaId, CancellationToken ct)
        => await db.LogosEmpresa.AsNoTracking()
            .Where(l => l.EmpresaId == empresaId)
            .Select(l => l.Conteudo)
            .FirstOrDefaultAsync(ct);
}
