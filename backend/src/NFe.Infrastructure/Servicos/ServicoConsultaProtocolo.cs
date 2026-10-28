using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Sefaz.Servicos;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Orquestra a consulta de situacao de uma NF-e na SEFAZ: carrega o certificado
/// da empresa, resolve a UF (da propria chave de acesso) e chama o web service.
/// Nao persiste nada por si so; opcionalmente sincroniza o status do documento
/// local quando a chave corresponde a uma nota conhecida.
/// </summary>
public sealed class ServicoConsultaProtocolo
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly ConsultaProtocoloClient _cliente;
    private readonly ILogger<ServicoConsultaProtocolo>? _log;

    public ServicoConsultaProtocolo(
        NFeDbContext db,
        ServicoCertificado certificados,
        ConsultaProtocoloClient cliente,
        ILogger<ServicoConsultaProtocolo>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _cliente = cliente;
        _log = log;
    }

    /// <summary>
    /// Consulta a NF-e identificada por <paramref name="chaveAcesso"/> usando o
    /// certificado da empresa. A UF vem dos 2 primeiros digitos da chave; o
    /// ambiente, do 21o digito (tpAmb), salvo override explicito. Nada do
    /// cadastro da empresa e usado alem do vinculo com o certificado.
    /// </summary>
    public async Task<ConsultaProtocoloResultado> ConsultarAsync(
        Guid empresaId,
        string chaveAcesso,
        TipoAmbiente? ambienteOverride = null,
        string? ufAutorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var chave = (chaveAcesso ?? string.Empty).Trim()
            .Replace("NFe", "", StringComparison.OrdinalIgnoreCase);

        // Valida o formato ANTES de qualquer I/O (banco/certificado): assim uma
        // chave malformada retorna 400 imediato, sem mascarar como erro de cert.
        if (chave.Length != 44 || !chave.All(char.IsDigit))
            throw new ArgumentException(
                "Chave de acesso deve conter exatamente 44 digitos.", nameof(chaveAcesso));

        var existe = await _db.Empresas.AsNoTracking().AnyAsync(e => e.Id == empresaId, ct);
        if (!existe)
            throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        // A chave de acesso NAO carrega tpAmb. Sem override, usa o ambiente da
        // nota ja cadastrada para esta empresa.
        var ambiente = ambienteOverride
            ?? (await _db.DocumentosFiscais.AsNoTracking()
                    .Where(d => d.EmpresaId == empresaId && d.ChaveAcesso == chave)
                    .Select(d => (TipoAmbiente?)d.Ambiente)
                    .FirstOrDefaultAsync(ct))
            ?? throw new ArgumentException(
                "Nota nao cadastrada nesta API: informe 'ambiente' (1 producao, 2 homologacao) no JSON.",
                nameof(chaveAcesso));

        var uf = UfDaChave(chave)
            ?? throw new ArgumentException(
                "UF (cUF) da chave de acesso invalida.", nameof(chaveAcesso));

        if (!string.IsNullOrWhiteSpace(ufAutorizadorOverride) &&
            !CodigoUf.SiglaValida(ufAutorizadorOverride))
            throw new ArgumentException("UF do autorizador invalida.", nameof(ufAutorizadorOverride));
        var autorizador = string.IsNullOrWhiteSpace(ufAutorizadorOverride)
            ? null : ufAutorizadorOverride.Trim().ToUpperInvariant();

        using var certificado = await _certificados.CarregarAtivoSefazAsync(empresaId, ct);

        var resultado = await _cliente.ConsultarAsync(
            uf, ambiente, chave, certificado,
            autorizador, contingencia, ct);

        _log?.LogInformation(
            "[SEFAZ] Consulta protocolo {Chave}: cStat {Stat} - {Motivo}",
            chave, resultado.CodigoStatus, resultado.Motivo);

        return resultado;
    }

    /// <summary>Codigo da UF (digitos 1-2 da chave) mapeado para a sigla.</summary>
    private static string? UfDaChave(string chave)
    {
        if (chave.Length < 2 || !int.TryParse(chave[..2], out var cUf)) return null;
        try { return CodigoUf.Sigla(cUf); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Consulta a SEFAZ e SINCRONIZA a base local: se a nota e desta empresa e esta
    /// autorizada/cancelada, atualiza o registro (ou cadastra, se ainda nao existe)
    /// e monta o XML autorizado (nfeProc) a partir do XML assinado guardado.
    /// </summary>
    public async Task<ConsultaSincronizada> ConsultarESincronizarAsync(
        Guid empresaId,
        string chaveAcesso,
        TipoAmbiente? ambienteOverride = null,
        string? ufAutorizadorOverride = null,
        bool contingencia = false,
        bool buscarNaPasta = true,
        CancellationToken ct = default)
    {
        var resultado = await ConsultarAsync(
            empresaId, chaveAcesso, ambienteOverride, ufAutorizadorOverride, contingencia, ct);

        var chave = resultado.ChaveAcesso;
        var doc = await _db.DocumentosFiscais
            .FirstOrDefaultAsync(d => d.EmpresaId == empresaId && d.ChaveAcesso == chave, ct);

        var situacaoFinal = resultado.Autorizada || resultado.Cancelada;
        if (!situacaoFinal || string.IsNullOrEmpty(resultado.NumeroProtocolo))
            return new ConsultaSincronizada(resultado, doc, false, null);

        var protNFe = ExtrairProtNFe(resultado.XmlRecebido);
        var cadastradaAgora = false;
        string? aviso = null;

        if (doc is null)
        {
            var empresa = await _db.Empresas.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == empresaId, ct);
            var cnpjChave = chave.Substring(6, 14);
            var cnpjEmpresa = new string((empresa?.Cnpj ?? "").Where(char.IsDigit).ToArray());

            // Nota de terceiros (ex.: recebida) nao e "emitida" por esta empresa.
            if (cnpjChave != cnpjEmpresa)
                return new ConsultaSincronizada(resultado, null, false,
                    "A chave pertence a outro emitente; nada foi cadastrado.");

            var agora = DateTime.UtcNow;
            doc = new DocumentoFiscal
            {
                EmpresaId = empresaId,
                ChaveAcesso = chave,
                Modelo = (ModeloDocumento)int.Parse(chave.Substring(20, 2)),
                Serie = int.Parse(chave.Substring(22, 3)),
                Numero = long.Parse(chave.Substring(25, 9)),
                Ambiente = resultado.Ambiente,
                TipoEmissao = (TipoEmissao)int.Parse(chave.Substring(34, 1)),
                CodigoNumerico = chave.Substring(35, 8),
                DigitoVerificador = int.Parse(chave.Substring(43, 1)),
                DataEmissao = resultado.DataAutorizacao
                    ?? new DateTime(2000 + int.Parse(chave.Substring(2, 2)),
                        int.Parse(chave.Substring(4, 2)), 1),
                NaturezaOperacao = "Cadastrada pela consulta na SEFAZ",
                CriadoEm = agora,
                AtualizadoEm = agora
            };
            _db.DocumentosFiscais.Add(doc);
            cadastradaAgora = true;
            aviso = "NF-e cadastrada sem XML: a consulta da SEFAZ devolve so o protocolo, e a " +
                    "distribuicao DF-e nao entrega ao emitente a propria nota. Envie o XML da nota " +
                    "(NFe assinada ou nfeProc) em POST /api/empresas/{empresaId}/nfe/xml.";
        }

        var statusAnterior = doc.Status;
        var novoStatus = resultado.Cancelada ? StatusDocumento.Cancelado : StatusDocumento.Autorizado;

        // Cancelamento e terminal: uma consulta nunca "descancela" a nota.
        if (doc.Status != StatusDocumento.Cancelado)
            doc.Status = novoStatus;

        doc.Protocolo = resultado.NumeroProtocolo;
        doc.DataAutorizacao = resultado.DataAutorizacao ?? doc.DataAutorizacao;
        if (statusAnterior != StatusDocumento.Autorizado && statusAnterior != StatusDocumento.Cancelado)
        {
            doc.CodigoStatusRetorno = resultado.CodigoStatus;
            doc.MotivoRetorno = Truncar(resultado.Motivo, 500);
        }

        if (string.IsNullOrEmpty(doc.XmlProtocolado) &&
            !string.IsNullOrEmpty(doc.XmlAssinado) && protNFe is not null)
        {
            doc.XmlProtocolado = AutorizacaoClient.MontarDistribuicao(doc.XmlAssinado, protNFe);
        }
        else if (string.IsNullOrEmpty(doc.XmlProtocolado) && !cadastradaAgora)
        {
            aviso = "Nota autorizada, mas a API nao tem o XML assinado para montar o nfeProc.";
        }

        if (cadastradaAgora || statusAnterior != doc.Status)
        {
            _db.EventosDocumento.Add(new EventoDocumento
            {
                DocumentoFiscal = doc,
                StatusAnterior = cadastradaAgora ? null : statusAnterior,
                StatusNovo = doc.Status,
                Descricao = cadastradaAgora
                    ? "cadastrada pela consulta de protocolo"
                    : "status sincronizado pela consulta de protocolo",
                Detalhe = $"cStat {resultado.CodigoStatus} - {resultado.Motivo}"
            });
        }

        doc.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // A SEFAZ nao entrega o XML da propria nota: completa pela pasta de backup,
        // sem depender de nenhuma acao do usuario. Se nao achar, o app e avisado
        // (XmlPendente + Aviso) e a proxima consulta tenta de novo.
        string? xmlOrigem = string.IsNullOrEmpty(doc.XmlProtocolado) ? null : "api";
        string? xmlPendente = null;
        if (buscarNaPasta && string.IsNullOrEmpty(doc.XmlProtocolado))
        {
            var (anexado, avisoPasta, motivo) = await TentarAnexarDaPastaAsync(empresaId, chave, protNFe, ct);
            if (anexado)
            {
                aviso = null;
                xmlOrigem = "pasta";
            }
            else
            {
                xmlPendente = motivo;
                aviso = avisoPasta;
            }
        }

        return new ConsultaSincronizada(resultado, doc, cadastradaAgora, aviso, xmlOrigem, xmlPendente);
    }

    /// <summary>
    /// Procura na pasta de backup da empresa o XML da chave e o anexa a nota.
    /// Lanca <see cref="PastaXmlNaoConfiguradaException"/> quando a empresa nao tem pasta
    /// e <see cref="InvalidOperationException"/> quando o arquivo nao e encontrado.
    /// </summary>
    public Task<AnexoXmlResultado> AnexarDaPastaAsync(
        Guid empresaId, string chaveAcesso, CancellationToken ct = default) =>
        AnexarDaPastaAsync(empresaId, chaveAcesso, null, ct);

    private async Task<AnexoXmlResultado> AnexarDaPastaAsync(
        Guid empresaId, string chaveAcesso, string? protNFeConfirmado, CancellationToken ct)
    {
        var chave = (chaveAcesso ?? string.Empty).Trim()
            .Replace("NFe", "", StringComparison.OrdinalIgnoreCase);
        if (chave.Length != 44 || !chave.All(char.IsDigit))
            throw new ArgumentException(
                "Chave de acesso deve conter exatamente 44 digitos.", nameof(chaveAcesso));

        var empresa = await _db.Empresas.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");
        var pasta = empresa.PastaXmlBackup;
        if (string.IsNullOrWhiteSpace(pasta)) throw new PastaXmlNaoConfiguradaException();

        var achado = await Task.Run(() => LocalizadorXmlPasta.Localizar(pasta, chave), ct)
            ?? throw new XmlNaoEncontradoNaPastaException(chave);

        _log?.LogInformation("[XML] {Chave} encontrado em {Caminho}", chave, achado.Caminho);
        return await AnexarXmlAsync(empresaId, chave, achado.Xml, null, protNFeConfirmado, ct);
    }

    /// <summary>
    /// Como <see cref="AnexarDaPastaAsync(Guid,string,CancellationToken)"/>, mas nunca lanca por
    /// falta de pasta ou de arquivo: devolve se anexou e, quando util, um aviso.
    /// </summary>
    private async Task<(bool Anexado, string? Aviso, string? Motivo)> TentarAnexarDaPastaAsync(
        Guid empresaId, string chave, string? protNFeConfirmado, CancellationToken ct)
    {
        try
        {
            await AnexarDaPastaAsync(empresaId, chave, protNFeConfirmado, ct);
            return (true, null, null);
        }
        catch (PastaXmlNaoConfiguradaException)
        {
            return (false,
                "XML indisponivel: a SEFAZ nao devolve o XML da nota e a empresa nao tem pasta de XML " +
                "configurada. Configure a pasta da empresa ou envie o XML em POST /api/empresas/{empresaId}/nfe/xml.",
                "pasta_nao_configurada");
        }
        catch (XmlNaoEncontradoNaPastaException ex)
        {
            _log?.LogWarning("[XML] {Chave} nao encontrado na pasta de backup", chave);
            return (false,
                ex.Message + " A proxima consulta tentara novamente de forma automatica.",
                "nao_encontrado");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateException)
        {
            _log?.LogWarning("[XML] {Chave} nao anexado da pasta: {Motivo}", chave, ex.Message);
            return (false, $"XML encontrado na pasta, mas nao foi anexado: {ex.Message}", "erro");
        }
    }

    /// <summary>
    /// Tenta completar da pasta de backup uma nota sem XML (usado ao baixar XML/DANFE).
    /// Devolve o nfeProc quando conseguiu, ou null.
    /// </summary>
    public async Task<string?> TentarCompletarDaPastaAsync(
        Guid empresaId, string chave, CancellationToken ct = default)
    {
        var (anexado, _, _) = await TentarAnexarDaPastaAsync(empresaId, chave, null, ct);
        if (!anexado) return null;
        return await _db.DocumentosFiscais.AsNoTracking()
            .Where(d => d.EmpresaId == empresaId && d.ChaveAcesso == chave)
            .Select(d => d.XmlProtocolado)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Anexa o XML de uma NF-e (NFe assinada ou nfeProc) a uma nota ja autorizada.
    /// A SEFAZ nao devolve o XML na consulta e nao o entrega ao emitente pela
    /// distribuicao DF-e, entao e a unica forma de a API ter XML e DANFE de uma nota
    /// emitida fora dela. Valida chave, assinatura, emitente e protocolo; confirma
    /// a autorizacao na SEFAZ, salvo quando a base ja guarda o mesmo protocolo.
    /// </summary>
    public async Task<AnexoXmlResultado> AnexarXmlAsync(
        Guid empresaId,
        string chaveAcesso,
        string xmlTexto,
        TipoAmbiente? ambienteOverride = null,
        string? protNFeConfirmado = null,
        CancellationToken ct = default)
    {
        var chave = (chaveAcesso ?? string.Empty).Trim()
            .Replace("NFe", "", StringComparison.OrdinalIgnoreCase);
        if (chave.Length != 44 || !chave.All(char.IsDigit))
            throw new ArgumentException(
                "Chave de acesso deve conter exatamente 44 digitos.", nameof(chaveAcesso));
        if (string.IsNullOrWhiteSpace(xmlTexto))
            throw new ArgumentException("Informe o XML da NF-e.", nameof(xmlTexto));

        XElement raiz;
        try { raiz = XDocument.Parse(xmlTexto.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')).Root!; }
        catch (System.Xml.XmlException)
        {
            throw new ArgumentException("O conteudo enviado nao e um XML valido.", nameof(xmlTexto));
        }

        XElement? nfe = raiz.Name.LocalName switch
        {
            "NFe" => raiz,
            "nfeProc" => raiz.Elements().FirstOrDefault(e => e.Name.LocalName == "NFe"),
            _ => null
        };
        if (nfe is null)
            throw new ArgumentException(
                "O XML deve ser uma NFe assinada ou um nfeProc.", nameof(xmlTexto));

        var infNFe = nfe.Descendants().FirstOrDefault(e => e.Name.LocalName == "infNFe");
        if ((string?)infNFe?.Attribute("Id") != "NFe" + chave)
            throw new ArgumentException("O XML nao corresponde a chave de acesso informada.", nameof(xmlTexto));
        if (!nfe.Descendants().Any(e => e.Name.LocalName == "Signature"))
            throw new ArgumentException("A NF-e do XML nao esta assinada.", nameof(xmlTexto));

        var empresa = await _db.Empresas.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");
        var cnpjEmpresa = new string((empresa.Cnpj ?? "").Where(char.IsDigit).ToArray());
        if (chave.Substring(6, 14) != cnpjEmpresa)
            throw new ArgumentException("A chave pertence a outro emitente.", nameof(chaveAcesso));

        var prot = raiz.Name.LocalName == "nfeProc"
            ? raiz.Elements().FirstOrDefault(e => e.Name.LocalName == "protNFe") : null;
        string? nProtXml = null;
        if (prot is not null)
        {
            string? Valor(string nome) => prot.Descendants().FirstOrDefault(e => e.Name.LocalName == nome)?.Value;
            nProtXml = Valor("nProt");
            if (Valor("chNFe") != chave || Valor("cStat") is not ("100" or "150") || string.IsNullOrEmpty(nProtXml))
                throw new ArgumentException(
                    "O protocolo do XML nao e de uma NF-e autorizada para esta chave.", nameof(xmlTexto));
        }
        var protStr = prot?.ToString(SaveOptions.DisableFormatting);

        // NFe assinada sem protocolo: usa o protocolo que a consulta acabou de confirmar,
        // evitando uma segunda chamada a SEFAZ (risco de 656 Consumo Indevido).
        if (protStr is null && protNFeConfirmado is not null)
        {
            protStr = protNFeConfirmado;
            nProtXml = XElement.Parse(protNFeConfirmado).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "nProt")?.Value;
        }

        var doc = await _db.DocumentosFiscais
            .FirstOrDefaultAsync(d => d.EmpresaId == empresaId && d.ChaveAcesso == chave, ct);
        if (doc is not null && !string.IsNullOrEmpty(doc.XmlProtocolado))
            return new AnexoXmlResultado(doc, false, "A API ja possui o XML desta nota; nada foi alterado.");

        var cadastradaAgora = false;
        var jaConfirmada = doc is not null
            && doc.Status is StatusDocumento.Autorizado or StatusDocumento.Cancelado
            && nProtXml is not null && doc.Protocolo == nProtXml;

        if (!jaConfirmada || protStr is null)
        {
            var tpAmb = infNFe!.Descendants().FirstOrDefault(e => e.Name.LocalName == "tpAmb")?.Value;
            var ambiente = ambienteOverride
                ?? (tpAmb == "2" ? TipoAmbiente.Homologacao : TipoAmbiente.Producao);

            var s = await ConsultarESincronizarAsync(
                empresaId, chave, ambiente, null, false, buscarNaPasta: false, ct: ct);
            if (s.Documento is null || !(s.Resultado.Autorizada || s.Resultado.Cancelada))
                throw new InvalidOperationException(
                    $"A SEFAZ nao confirma a nota como autorizada (cStat {s.Resultado.CodigoStatus}: " +
                    $"{s.Resultado.Motivo}); XML nao anexado.");
            if (nProtXml is not null && nProtXml != s.Resultado.NumeroProtocolo)
                throw new ArgumentException("O protocolo do XML difere do registrado na SEFAZ.", nameof(xmlTexto));

            doc = s.Documento;
            cadastradaAgora = s.CadastradaAgora;
            if (!string.IsNullOrEmpty(doc.XmlProtocolado))
                return new AnexoXmlResultado(doc, cadastradaAgora, null);
            protStr ??= ExtrairProtNFe(s.Resultado.XmlRecebido);
        }

        if (doc is null || protStr is null)
            throw new InvalidOperationException("Nao foi possivel obter o protocolo de autorizacao da nota.");

        var nfeXml = nfe.ToString(SaveOptions.DisableFormatting);
        if (string.IsNullOrEmpty(doc.XmlAssinado)) doc.XmlAssinado = nfeXml;
        doc.XmlProtocolado = AutorizacaoClient.MontarDistribuicao(nfeXml, protStr);
        PreencherDadosDoXml(doc, infNFe!);

        _db.EventosDocumento.Add(new EventoDocumento
        {
            DocumentoFiscal = doc,
            StatusAnterior = doc.Status,
            StatusNovo = doc.Status,
            Descricao = "XML da nota anexado manualmente",
            Detalhe = $"protocolo {doc.Protocolo}"
        });
        doc.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new AnexoXmlResultado(doc, cadastradaAgora, null);
    }

    /// <summary>Completa os campos que a consulta nao traz (so em nota cadastrada por ela).</summary>
    private static void PreencherDadosDoXml(DocumentoFiscal doc, XElement infNFe)
    {
        if (doc.NaturezaOperacao != "Cadastrada pela consulta na SEFAZ") return;

        string? Texto(XElement? pai, string nome) =>
            pai?.Descendants().FirstOrDefault(e => e.Name.LocalName == nome)?.Value;
        XElement? Grupo(string nome) =>
            infNFe.Elements().FirstOrDefault(e => e.Name.LocalName == nome);
        decimal Numero(string? v) =>
            decimal.TryParse(v, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

        var ide = Grupo("ide");
        var dest = Grupo("dest");
        var totais = Grupo("total");

        doc.NaturezaOperacao = Texto(ide, "natOp") ?? doc.NaturezaOperacao;
        if (DateTimeOffset.TryParse(Texto(ide, "dhEmi"), out var emissao))
            doc.DataEmissao = emissao.UtcDateTime;
        if (DateTimeOffset.TryParse(Texto(ide, "dhSaiEnt"), out var saida))
            doc.DataSaidaEntrada = saida.UtcDateTime;
        doc.TipoOperacao = Texto(ide, "tpNF") == "0" ? TipoOperacao.Entrada : TipoOperacao.Saida;

        doc.DestinatarioNome = Texto(dest, "xNome");
        doc.DestinatarioDocumento = Texto(dest, "CNPJ") ?? Texto(dest, "CPF");
        doc.DestinatarioUf = dest?.Descendants().FirstOrDefault(e => e.Name.LocalName == "UF")?.Value;

        doc.ValorTotal = Numero(Texto(totais, "vNF"));
        doc.ValorProdutos = Numero(Texto(totais, "vProd"));
        doc.ValorIcms = Numero(Texto(totais, "vICMS"));
    }

    private static string? ExtrairProtNFe(string envelope)
    {
        try
        {
            return XDocument.Parse(envelope)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "protNFe")
                ?.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string Truncar(string texto, int max) =>
        texto.Length <= max ? texto : texto[..max];
}

/// <summary>Resultado de anexar o XML a uma nota.</summary>
/// <summary>A empresa nao tem pasta de backup de XML configurada.</summary>
public sealed class PastaXmlNaoConfiguradaException()
    : InvalidOperationException("Nenhuma pasta de XML configurada para esta empresa.");

public sealed class XmlNaoEncontradoNaPastaException(string chave)
    : InvalidOperationException($"Nenhum XML da chave {chave} foi encontrado na pasta configurada.");

public sealed record AnexoXmlResultado(DocumentoFiscal Documento, bool CadastradaAgora, string? Aviso);

/// <summary>
/// Resultado da consulta com o estado da nota na base local. <c>XmlOrigem</c> e "api"
/// (ja estava na base) ou "pasta" (achado agora na pasta); <c>XmlPendente</c> explica a
/// falta do XML: pasta_nao_configurada, nao_encontrado ou erro.
/// </summary>
public sealed record ConsultaSincronizada(
    ConsultaProtocoloResultado Resultado,
    DocumentoFiscal? Documento,
    bool CadastradaAgora,
    string? Aviso,
    string? XmlOrigem = null,
    string? XmlPendente = null);
