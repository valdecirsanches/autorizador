using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Orquestra os eventos fiscais de uma NF-e autorizada: cancelamento
/// (tpEvento 110111) e carta de correcao (110110). Ambos compartilham a mesma
/// estrutura de persistencia (<see cref="EventoFiscal"/>) e transporte
/// (<see cref="EventoClient"/>); o cancelamento muda a situacao da nota para
/// Cancelado, a carta de correcao apenas anexa o evento.
///
/// A chamada a SEFAZ acontece fora de transacao; o resultado (homologado ou
/// rejeitado) e sempre persistido, inclusive a resposta bruta, para auditoria.
/// </summary>
public sealed class ServicoEventos
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly AssinadorXml _assinador;
    private readonly EventoClient _cliente;
    private readonly SefazEndpointRegistry _registry;
    private readonly ILogger<ServicoEventos>? _log;

    public ServicoEventos(
        NFeDbContext db,
        ServicoCertificado certificados,
        AssinadorXml assinador,
        EventoClient cliente,
        SefazEndpointRegistry registry,
        ILogger<ServicoEventos>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _assinador = assinador;
        _cliente = cliente;
        _registry = registry;
        _log = log;
    }

    /// <summary>
    /// Cancela uma NF-e autorizada. Idempotente: se ja estiver cancelada,
    /// devolve o evento homologado existente sem reenviar.
    /// </summary>
    public async Task<EventoFiscal> CancelarAsync(
        Guid empresaId,
        Guid documentoId,
        string justificativa,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var documento = await _db.DocumentosFiscais
            .Include(d => d.Eventos)
            .Include(d => d.EventosFiscais)
            .FirstOrDefaultAsync(d => d.Id == documentoId && d.EmpresaId == empresaId, ct)
            ?? throw new InvalidOperationException($"Documento {documentoId} nao encontrado.");

        // Idempotencia: nota ja cancelada devolve o evento homologado.
        if (documento.Status == StatusDocumento.Cancelado)
        {
            var homologado = documento.EventosFiscais
                .Where(e => e.TipoEvento == 110111 && e.Homologado)
                .OrderByDescending(e => e.CriadoEm)
                .FirstOrDefault();
            if (homologado is not null) return homologado;
        }

        if (documento.Status != StatusDocumento.Autorizado)
            throw new EventoInvalidoException(
                $"Somente uma NF-e autorizada pode ser cancelada. Situacao atual: {documento.Status}.");

        if (string.IsNullOrWhiteSpace(documento.ChaveAcesso) || documento.ChaveAcesso.Length != 44)
            throw new EventoInvalidoException("Documento sem chave de acesso valida.");

        if (string.IsNullOrWhiteSpace(documento.Protocolo))
            throw new EventoInvalidoException(
                "Documento sem protocolo de autorizacao; nao e possivel cancelar.");

        var just = (justificativa ?? string.Empty).Trim();
        if (just.Length < 15 || just.Length > 255)
            throw new EventoInvalidoException(
                "A justificativa do cancelamento deve ter de 15 a 255 caracteres.");

        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        var dadosEmissao = await ObterDadosEmissaoAsync(empresa, documento, ct);
        var codigoOrgao = _registry.ObterCodigoUf(dadosEmissao.Uf);

        var evento = new EventoFiscal
        {
            DocumentoFiscalId = documento.Id,
            ChaveAcesso = documento.ChaveAcesso!,
            TipoEvento = 110111,
            SequenciaEvento = 1,
            Descricao = "Cancelamento",
            Justificativa = just,
            ProtocoloNFe = documento.Protocolo,
            CriadoEm = DateTime.UtcNow
        };

        try
        {
            using var certificado = await _certificados.CarregarAtivoSefazAsync(empresaId, ct);

            var (xmlEvento, idRef) = EventoClient.MontarCancelamento(new EventoClient.DadosCancelamento(
                Uf: dadosEmissao.Uf,
                Ambiente: documento.Ambiente,
                CodigoOrgao: codigoOrgao,
                Cnpj: dadosEmissao.Cnpj,
                ChaveAcesso: documento.ChaveAcesso!,
                ProtocoloNFe: documento.Protocolo!,
                Justificativa: just));

            // A Signature do evento e IRMA de infEvento, filha de <evento>.
            var xmlAssinado = _assinador.Assinar(xmlEvento, idRef, certificado, nomeTagAssinatura: "evento");

            var resultado = await _cliente.EnviarAsync(
                dadosEmissao.Uf, documento.Ambiente,
                TiposEvento.Cancelamento, nSeqEvento: 1, documento.ChaveAcesso!,
                xmlAssinado, certificado,
                autorizadorOverride: dadosEmissao.UfAutorizadorOverride,
                contingencia: contingencia, ct: ct);

            PreencherRetorno(evento, resultado);

            if (resultado.Homologado)
            {
                var anterior = documento.Status;
                documento.Status = StatusDocumento.Cancelado;
                documento.AtualizadoEm = DateTime.UtcNow;

                documento.Eventos.Add(new EventoDocumento
                {
                    StatusAnterior = anterior,
                    StatusNovo = StatusDocumento.Cancelado,
                    Descricao = "cancelamento homologado pela SEFAZ",
                    Detalhe = $"evento cStat {evento.CodigoStatus} ({evento.Motivo}); " +
                              $"protocolo {evento.ProtocoloEvento ?? "-"}",
                    OcorridoEm = DateTime.UtcNow
                });
            }

            _db.EventosFiscais.Add(evento);
            await _db.SaveChangesAsync(ct);
            return evento;
        }
        catch (SefazSoapException ex)
        {
            // Rejeicao/erro COM resposta da SEFAZ: persistimos o evento como nao
            // homologado, guardando a resposta bruta, e devolvemos (o endpoint
            // exibe o motivo). Sem resposta (rede), e falha transitoria: rethrow.
            if (string.IsNullOrWhiteSpace(ex.EnvelopeRecebido))
                throw;

            evento.Homologado = false;
            evento.Motivo = ex.Message;
            evento.XmlRetorno = ex.EnvelopeRecebido;
            _db.EventosFiscais.Add(evento);
            await _db.SaveChangesAsync(ct);
            return evento;
        }
    }

    /// <summary>
    /// Registra uma carta de correcao (evento 110110) para uma NF-e autorizada.
    /// Diferente do cancelamento, NAO altera o status da nota — apenas anexa o
    /// evento. Admite ate 20 por nota; a sequencia e calculada automaticamente
    /// (proxima apos a maior ja registrada). A correcao deve ter 15 a 1000
    /// caracteres.
    /// </summary>
    public async Task<EventoFiscal> CorrigirAsync(
        Guid empresaId,
        Guid documentoId,
        string correcao,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var documento = await _db.DocumentosFiscais
            .Include(d => d.Eventos)
            .Include(d => d.EventosFiscais)
            .FirstOrDefaultAsync(d => d.Id == documentoId && d.EmpresaId == empresaId, ct)
            ?? throw new InvalidOperationException($"Documento {documentoId} nao encontrado.");

        if (documento.Status != StatusDocumento.Autorizado)
            throw new EventoInvalidoException(
                $"Somente uma NF-e autorizada pode receber carta de correcao. Situacao atual: {documento.Status}.");

        if (string.IsNullOrWhiteSpace(documento.ChaveAcesso) || documento.ChaveAcesso.Length != 44)
            throw new EventoInvalidoException("Documento sem chave de acesso valida.");

        var texto = (correcao ?? string.Empty).Trim();
        if (texto.Length is < 15 or > 1000)
            throw new EventoInvalidoException(
                "A correcao deve ter de 15 a 1000 caracteres.");

        // Proxima sequencia: uma acima da maior CC-e ja transmitida (homologada
        // ou nao). A SEFAZ limita a 20 cartas por nota.
        var maiorSeq = documento.EventosFiscais
            .Where(e => e.TipoEvento == 110110)
            .Select(e => (int?)e.SequenciaEvento)
            .Max() ?? 0;
        var proximaSeq = maiorSeq + 1;
        if (proximaSeq > 20)
            throw new EventoInvalidoException(
                "Limite de 20 cartas de correcao por NF-e atingido.");

        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        var dadosEmissao = await ObterDadosEmissaoAsync(empresa, documento, ct);
        var codigoOrgao = _registry.ObterCodigoUf(dadosEmissao.Uf);

        var evento = new EventoFiscal
        {
            DocumentoFiscalId = documento.Id,
            ChaveAcesso = documento.ChaveAcesso!,
            TipoEvento = 110110,
            SequenciaEvento = proximaSeq,
            Descricao = "Carta de Correcao",
            Correcao = texto,
            CriadoEm = DateTime.UtcNow
        };

        try
        {
            using var certificado = await _certificados.CarregarAtivoSefazAsync(empresaId, ct);

            var (xmlEvento, idRef) = EventoClient.MontarCartaCorrecao(new EventoClient.DadosCartaCorrecao(
                Uf: dadosEmissao.Uf,
                Ambiente: documento.Ambiente,
                CodigoOrgao: codigoOrgao,
                Cnpj: dadosEmissao.Cnpj,
                ChaveAcesso: documento.ChaveAcesso!,
                Correcao: texto,
                SequenciaEvento: proximaSeq));

            var xmlAssinado = _assinador.Assinar(xmlEvento, idRef, certificado, nomeTagAssinatura: "evento");

            var resultado = await _cliente.EnviarAsync(
                dadosEmissao.Uf, documento.Ambiente,
                TiposEvento.CartaCorrecao, nSeqEvento: proximaSeq, documento.ChaveAcesso!,
                xmlAssinado, certificado,
                autorizadorOverride: dadosEmissao.UfAutorizadorOverride,
                contingencia: contingencia, ct: ct);

            PreencherRetorno(evento, resultado);

            if (resultado.Homologado)
            {
                documento.AtualizadoEm = DateTime.UtcNow;
                documento.Eventos.Add(new EventoDocumento
                {
                    StatusAnterior = documento.Status,
                    StatusNovo = documento.Status, // CC-e nao muda a situacao da nota
                    Descricao = $"carta de correcao {proximaSeq} homologada pela SEFAZ",
                    Detalhe = $"evento cStat {evento.CodigoStatus} ({evento.Motivo}); " +
                              $"protocolo {evento.ProtocoloEvento ?? "-"}",
                    OcorridoEm = DateTime.UtcNow
                });
            }

            _db.EventosFiscais.Add(evento);
            await _db.SaveChangesAsync(ct);
            return evento;
        }
        catch (SefazSoapException ex)
        {
            if (string.IsNullOrWhiteSpace(ex.EnvelopeRecebido))
                throw;

            evento.Homologado = false;
            evento.Motivo = ex.Message;
            evento.XmlRetorno = ex.EnvelopeRecebido;
            _db.EventosFiscais.Add(evento);
            await _db.SaveChangesAsync(ct);
            return evento;
        }
    }

    private async Task<(string Uf, string Cnpj, string? UfAutorizadorOverride)> ObterDadosEmissaoAsync(
        Empresa empresa,
        DocumentoFiscal documento,
        CancellationToken ct)
    {
        var uf = empresa.Uf;
        var cnpj = empresa.Cnpj;
        var chave = documento.ChaveAcesso;
        if (!string.IsNullOrWhiteSpace(chave) && chave.Length == 44 &&
            int.TryParse(chave[..2], out var codigoUf))
        {
            try
            {
                uf = CodigoUf.Sigla(codigoUf);
                cnpj = chave.Substring(6, 14);
            }
            catch (ArgumentException)
            {
                // Registros legados com chave inválida continuam usando o cadastro.
            }
        }

        var json = await _db.TentativasEmissao
            .Where(t => t.EmpresaId == empresa.Id && t.DocumentoFiscalId == documento.Id)
            .OrderByDescending(t => t.CriadoEm)
            .Select(t => t.JsonRecebido)
            .FirstOrDefaultAsync(ct);

        ConfiguracaoNFeRequest? configuracao = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                configuracao = JsonSerializer.Deserialize<NotaFiscalRequest>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Configuracao;
            }
            catch (JsonException)
            {
                // Compatibilidade com tentativas antigas ou payloads legados.
            }
        }

        return (uf, cnpj, configuracao is null
            ? empresa.UfAutorizadorOverride
            : configuracao.UfAutorizadorOverride);
    }

    /// <summary>Preenche os campos de retorno de um evento a partir do resultado.</summary>
    private static void PreencherRetorno(EventoFiscal evento, EventoResultado r)
    {
        evento.XmlEnviado = r.XmlEnviado;
        evento.XmlRetorno = r.XmlRecebido;
        evento.XmlEvento = r.XmlDistribuicao;
        evento.CodigoStatus = r.CodigoStatusEvento ?? r.CodigoStatusLote;
        evento.Motivo = PrimeiroNaoVazio(
            r.MotivoEvento, r.MotivoLote,
            $"Evento cStat {evento.CodigoStatus}.");
        evento.ProtocoloEvento = r.NumeroProtocolo;
        evento.DataRegistro = r.DataRegistro;
        evento.Homologado = r.Homologado;
    }

    private static string PrimeiroNaoVazio(params string?[] valores)
    {
        foreach (var v in valores)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "-";
    }
}

/// <summary>Erro de regra de negocio ao processar um evento fiscal.</summary>
public sealed class EventoInvalidoException : Exception
{
    public EventoInvalidoException(string message) : base(message) { }
}
