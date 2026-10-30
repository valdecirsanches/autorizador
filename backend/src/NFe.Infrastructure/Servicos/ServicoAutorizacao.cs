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
/// Orquestra uma tentativa de emissao de NF-e usando a numeracao enviada pelo
/// app cliente: gera o XML, valida contra o XSD, assina, transmite em lote
/// sincrono e persiste a trilha de auditoria com o protocolo retornado.
///
/// A persistencia inicial ocorre numa transacao curta. A chamada a SEFAZ fica
/// fora da transacao para nao manter locks do banco durante o I/O de rede.
/// </summary>
public sealed class ServicoAutorizacao
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly GeradorXmlNFe _gerador;
    private readonly AssinadorXml _assinador;
    private readonly AutorizacaoClient _cliente;
    private readonly ValidadorXsd? _validador;
    private readonly ArquivadorXml? _arquivador;
    private readonly ILogger<ServicoAutorizacao>? _log;

    public ServicoAutorizacao(
        NFeDbContext db,
        ServicoCertificado certificados,
        GeradorXmlNFe gerador,
        AssinadorXml assinador,
        AutorizacaoClient cliente,
        ValidadorXsd? validador = null,
        ArquivadorXml? arquivador = null,
        ILogger<ServicoAutorizacao>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _gerador = gerador;
        _assinador = assinador;
        _cliente = cliente;
        _validador = validador;
        _arquivador = arquivador;
        _log = log;
    }

    /// <summary>
    /// Processa cada POST como uma nova tentativa de emissao. O chamador controla
    /// a numeracao e decide como tratar retentativas; rejeicoes anteriores nao
    /// sao reutilizadas nem bloqueiam um novo pedido.
    /// </summary>
    public async Task<DocumentoFiscal> EmitirAsync(
        Guid empresaId,
        NotaFiscalRequest req,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var problemas = ValidarRequest(req);
        if (problemas.Count > 0)
            throw new RequisicaoInvalidaException(problemas);

        // A empresa persistida identifica o certificado e o espaco de dados. Os
        // dados fiscais usados para gerar e transmitir esta NF-e vêm do JSON.
        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        var configuracao = req.Configuracao!;
        var emitente = CriarEmitente(empresaId, req.Emitente!);
        if (!string.Equals(
                SomenteDigitos(empresa.Cnpj), emitente.Cnpj, StringComparison.Ordinal))
            throw new RequisicaoInvalidaException(
                ["O CNPJ do emitente no JSON deve corresponder ao CNPJ vinculado ao certificado."]);

        var ambiente = configuracao.Ambiente!.Value;

        // ---- Fase 1: gerar + assinar + persistir (transacao curta) ----
        var documento = await PrepararDocumentoAsync(emitente, req, ambiente, ct);

        // ---- Fase 2: transmitir a SEFAZ (fora de transacao) ----
        await TransmitirAsync(
            empresa, emitente.Uf, configuracao.UfAutorizadorOverride,
            documento, ambiente, configuracao.Contingencia, ct);

        return documento;
    }

    private async Task<DocumentoFiscal> PrepararDocumentoAsync(
        Empresa empresa,
        NotaFiscalRequest req,
        TipoAmbiente ambiente,
        CancellationToken ct)
    {
        // Carrega o certificado antes de abrir a transacao para evitar persistir
        // qualquer documento quando o certificado estiver ausente ou vencido.
        using var certificado = await _certificados.CarregarAtivoSefazAsync(empresa.Id, ct);
        var cnpjCertificado = ServicoCertificado.ExtrairCnpj(certificado.Publico);
        var cnpjEmitente = SomenteDigitos(empresa.Cnpj);
        if (cnpjCertificado is not null &&
            !string.Equals(cnpjCertificado, cnpjEmitente, StringComparison.Ordinal))
            throw new CertificadoException(
                "O CNPJ do certificado ativo nao corresponde ao CNPJ do emitente enviado no JSON.");

        var estrategia = _db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // A numeracao e de responsabilidade do app cliente. A API usa
            // exatamente o req.Numero enviado e nao bloqueia com base em tentativas
            // anteriores; a SEFAZ determina se a nova emissao pode ser autorizada.
            var numero = req.Numero;

            var gerada = _gerador.Gerar(empresa, req, ambiente);

            // A validacao XSD, quando um validador esta configurado, roda sobre o
            // XML JA assinado: o schema da NF-e exige o elemento Signature.
            var xmlAssinado = _assinador.Assinar(
                gerada.Xml, "NFe" + gerada.ChaveAcesso, certificado);

            _validador?.Validar(xmlAssinado, "nfe_v4.00.xsd");

            var documento = new DocumentoFiscal
            {
                EmpresaId = empresa.Id,
                ChaveAcesso = gerada.ChaveAcesso,
                Modelo = req.Modelo,
                Serie = req.Serie,
                Numero = numero,
                Ambiente = ambiente,
                TipoEmissao = req.TipoEmissao,
                TipoOperacao = req.TipoOperacao,
                Status = StatusDocumento.Assinado,
                CodigoNumerico = gerada.CodigoNumerico,
                DigitoVerificador = gerada.DigitoVerificador,
                DataEmissao = req.DataEmissao.UtcDateTime,
                DataSaidaEntrada = req.DataSaidaEntrada?.UtcDateTime,
                DestinatarioDocumento = req.Destinatario.Documento,
                DestinatarioNome = req.Destinatario.Nome,
                DestinatarioUf = req.Destinatario.Uf,
                ValorProdutos = req.Itens.Sum(i => i.ValorBruto > 0 ? i.ValorBruto : i.Quantidade * i.ValorUnitario),
                ValorIcms = req.Itens.Sum(i => i.ValorIcms),
                NaturezaOperacao = req.NaturezaOperacao,
                XmlGerado = gerada.Xml,
                XmlAssinado = xmlAssinado
            };
            documento.ValorTotal = documento.ValorProdutos;

            RegistrarEvento(documento, null, StatusDocumento.Assinado, "documento gerado e assinado");

            _db.DocumentosFiscais.Add(documento);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return documento;
        });
    }

    /// <summary>Numero maximo de tentativas de transmissao numa unica chamada.</summary>
    private const int MaxTentativasTransmissao = 3;

    private async Task TransmitirAsync(
        Empresa empresa,
        string ufEmitente,
        string? ufAutorizadorOverride,
        DocumentoFiscal documento,
        TipoAmbiente ambiente,
        bool contingencia,
        CancellationToken ct)
    {
        var statusAnterior = documento.Status;

        // Espelha em disco o XML assinado ANTES da rede: mesmo que a transmissao
        // falhe (timeout, SEFAZ fora), fica registrado o que foi enviado. Cobre
        // tanto a emissao quanto o reenvio, pois ambos passam por aqui.
        _arquivador?.SalvarAssinado(empresa, documento);

        // Auto-retry: uma falha de rede/timeout NAO trava o documento — reenviamos
        // o MESMO XML assinado ate MaxTentativasTransmissao vezes. Reenviar o mesmo
        // lote e idempotente na SEFAZ (mesma chave => a SEFAZ deduplica; se a nota
        // ja tiver sido autorizada num envio cujo retorno se perdeu, o reenvio
        // retorna duplicidade cStat 539, que o painel exibe). So retentamos falhas
        // TRANSITORIAS; um erro deterministico (schema, certificado, parsing) para
        // na hora e vira ErroTransmissao com o motivo real.
        for (var tentativa = 1; ; tentativa++)
        {
            documento.TentativasEnvio += 1;

            try
            {
                using var certificado = await _certificados.CarregarAtivoSefazAsync(empresa.Id, ct);

                var resultado = await _cliente.EnviarAsync(
                    ufEmitente, ambiente, documento.XmlAssinado!, certificado,
                    autorizadorOverride: ufAutorizadorOverride,
                    contingencia: contingencia, ct: ct);

                AplicarResultado(documento, statusAnterior, resultado);

                // Persistir o desfecho da SEFAZ. Sem este SaveChanges o documento
                // ficava congelado em "Assinado" no banco — status, cStat, motivo,
                // XmlProtocolado e XmlRetornoSefaz eram calculados em memoria e
                // devolvidos na resposta do POST, mas NUNCA gravados. Era a causa de
                // "codigo nao informado" e do retorno da SEFAZ nao baixar.
                await _db.SaveChangesAsync(ct);

                // Espelha o retorno da SEFAZ em disco SEMPRE que a SEFAZ respondeu
                // — inclusive rejeicao — para haver XML a avaliar mesmo sem
                // autorizacao. Se autorizada, salva tambem o nfeProc definitivo.
                _arquivador?.SalvarRetornoSefaz(empresa, documento);
                if (documento.Status == StatusDocumento.Autorizado)
                    _arquivador?.SalvarAutorizado(empresa, documento);
                return;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var transitoria = EhFalhaTransitoria(ex);

                if (transitoria && tentativa < MaxTentativasTransmissao)
                {
                    _log?.LogWarning(ex,
                        "[NFe] Falha transitoria ao transmitir {Id} (tentativa {N}/{Max}); reenviando.",
                        documento.Id, tentativa, MaxTentativasTransmissao);

                    RegistrarEvento(documento, statusAnterior, statusAnterior,
                        $"reenvio automatico (tentativa {tentativa}/{MaxTentativasTransmissao})",
                        DescreverExcecao(ex));

                    // Backoff progressivo curto entre reenvios.
                    await Task.Delay(TimeSpan.FromMilliseconds(800 * tentativa), ct);
                    continue;
                }

                // Definitiva OU esgotou as tentativas: QUALQUER falha na transmissao
                // deixa o documento em ErroTransmissao com a numeracao preservada,
                // elegivel a reenvio manual pelo endpoint /reenviar. Antes, o catch
                // so pegava tres tipos especificos; um erro de parsing do retorno, um
                // FormatException ou um NullReference escapava sem gravar nada — e o
                // documento ficava ORFAO em "Assinado" com tentativas 0, sem motivo
                // visivel. Era a causa do "a API nao autorizou, motivo: assinado".
                documento.Status = StatusDocumento.ErroTransmissao;
                documento.UltimoErro = Truncar(DescreverExcecao(ex), 1000);
                documento.AtualizadoEm = DateTime.UtcNow;

                // NUNCA descarte a resposta crua da SEFAZ. Um SOAP Fault (erro de
                // schema), um HTTP 500/403 ou um envelope que o parser nao entendeu
                // chegam aqui como SefazSoapException carregando o envelope recebido.
                if (ex is SefazSoapException soap && !string.IsNullOrWhiteSpace(soap.EnvelopeRecebido))
                    documento.XmlRetornoSefaz = soap.EnvelopeRecebido;

                RegistrarEvento(documento, statusAnterior, StatusDocumento.ErroTransmissao,
                    "falha de transmissao", DescreverExcecao(ex));

                _log?.LogError(ex,
                    "[NFe] Falha ao transmitir documento {Id} ({Chave}) apos {N} tentativa(s).",
                    documento.Id, documento.ChaveAcesso, tentativa);

                await _db.SaveChangesAsync(ct);

                // Se a SEFAZ chegou a responder (ex.: SOAP fault de schema), espelha
                // esse envelope em disco para diagnostico — e o unico XML disponivel
                // quando a transmissao falha.
                _arquivador?.SalvarRetornoSefaz(empresa, documento);
                return;
            }
        }
    }

    /// <summary>
    /// Reenvia um documento nao autorizado com o XML assinado original, a mesma
    /// numeracao e a mesma chave. A configuração de roteamento vem do JSON da
    /// tentativa original, sem reconstruir dados fiscais a partir do cadastro.
    /// Se ja estiver autorizado, retorna sem reenviar.
    /// </summary>
    public async Task<DocumentoFiscal> ReenviarAsync(
        Guid empresaId,
        Guid documentoId,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var documento = await _db.DocumentosFiscais
            .Include(d => d.Eventos)
            .FirstOrDefaultAsync(d => d.Id == documentoId && d.EmpresaId == empresaId, ct)
            ?? throw new InvalidOperationException($"Documento {documentoId} nao encontrado.");

        if (documento.Status == StatusDocumento.Autorizado)
            return documento;

        if (string.IsNullOrEmpty(documento.XmlAssinado))
            throw new InvalidOperationException(
                "Documento sem XML assinado; nao pode ser reenviado.");

        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        // O JSON original da tentativa guarda a configuração escolhida pelo app.
        // O reenvio usa o XML assinado original; nunca reconstrói o emitente com
        // dados cadastrais atuais do servidor.
        var jsonOriginal = await _db.TentativasEmissao
            .Where(t => t.EmpresaId == empresaId && t.DocumentoFiscalId == documentoId)
            .OrderByDescending(t => t.CriadoEm)
            .Select(t => t.JsonRecebido)
            .FirstOrDefaultAsync(ct);

        NotaFiscalRequest? requestOriginal = null;
        if (!string.IsNullOrWhiteSpace(jsonOriginal))
        {
            try
            {
                requestOriginal = JsonSerializer.Deserialize<NotaFiscalRequest>(
                    jsonOriginal, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                // Compatibilidade com tentativas antigas ou payloads legados.
            }
        }

        var ufEmitente = requestOriginal?.Emitente?.Uf;
        if (string.IsNullOrWhiteSpace(ufEmitente))
            ufEmitente = empresa.Uf;

        var ufAutorizadorOverride = requestOriginal?.Configuracao is null
            ? empresa.UfAutorizadorOverride
            : requestOriginal.Configuracao.UfAutorizadorOverride;

        await TransmitirAsync(
            empresa, ufEmitente, ufAutorizadorOverride,
            documento, documento.Ambiente, contingencia, ct);
        return documento;
    }

    /// <summary>
    /// Uma falha e transitoria quando faz sentido reenviar: SEFAZ fora do ar,
    /// timeout, ou uma falha SOAP em que NENHUMA resposta chegou (rede). Quando a
    /// SEFAZ respondeu mas o envelope nao parseou, o erro e deterministico e nao
    /// deve ser retentado — reenviar produziria o mesmo erro.
    /// </summary>
    private static bool EhFalhaTransitoria(Exception ex) => ex switch
    {
        SefazEndpointException => true,
        TimeoutException => true,
        System.Net.Http.HttpRequestException => true,
        SefazSoapException soap => string.IsNullOrWhiteSpace(soap.EnvelopeRecebido),
        _ => false
    };

    /// <summary>Traduz o retorno da SEFAZ em estado persistido do documento.</summary>
    internal static void AplicarResultado(
        DocumentoFiscal documento,
        StatusDocumento statusAnterior,
        AutorizacaoResultado resultado)
    {
        documento.AtualizadoEm = DateTime.UtcNow;

        // Guarda SEMPRE a resposta bruta da SEFAZ (envelope retEnviNFe), mesmo
        // em rejeicao sem protocolo. E o "log completo" para diagnostico.
        documento.XmlRetornoSefaz = resultado.XmlRecebido;

        var prot = resultado.Protocolo;
        if (prot is not null)
        {
            documento.Protocolo = prot.NumeroProtocolo;
            documento.CodigoStatusRetorno = prot.CodigoStatus;
            // Rejeicao COM protocolo: o motivo da nota pode vir vazio; caimos
            // para o motivo/cStat do lote para nunca exibir "sem motivo".
            documento.MotivoRetorno = PrimeiroNaoVazio(
                prot.Motivo, resultado.MotivoLote,
                $"Rejeitada (cStat {prot.CodigoStatus}).");
            documento.DataAutorizacao = prot.DataAutorizacao;
            documento.DigestValue = prot.DigestValue;
            documento.XmlProtocolado = resultado.XmlDistribuicao;
        }
        else
        {
            // Rejeicao de lote sem protocolo: o veredito esta no cStat do lote.
            // O motivo NUNCA fica nulo — inclui o cStat como ultimo recurso para
            // que o operador tenha ao menos o codigo para pesquisar.
            documento.CodigoStatusRetorno = resultado.CodigoStatusLote;
            documento.MotivoRetorno = PrimeiroNaoVazio(
                resultado.MotivoLote,
                $"Lote rejeitado pela SEFAZ (cStat {resultado.CodigoStatusLote}).");
        }

        documento.Status = MapearStatus(resultado);

        var descricao = documento.Status switch
        {
            StatusDocumento.Autorizado => "autorizado pela SEFAZ",
            StatusDocumento.Denegado => "denegado pela SEFAZ",
            StatusDocumento.Rejeitado => "rejeitado pela SEFAZ",
            _ => "retorno da SEFAZ processado"
        };

        // Detalhe do evento com o veredito legivel — aparece na trilha do painel.
        var detalhe =
            $"lote cStat {resultado.CodigoStatusLote} ({PrimeiroNaoVazio(resultado.MotivoLote, "-")}); " +
            $"nota cStat {prot?.CodigoStatus ?? "-"} ({PrimeiroNaoVazio(prot?.Motivo, "-")})";

        RegistrarEvento(documento, statusAnterior, documento.Status, descricao, detalhe);
    }

    /// <summary>Primeiro valor nao nulo/vazio, ja aparado. Sempre retorna algo.</summary>
    private static string PrimeiroNaoVazio(params string?[] valores)
    {
        foreach (var v in valores)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "-";
    }

    private static StatusDocumento MapearStatus(AutorizacaoResultado r)
    {
        var prot = r.Protocolo;
        if (prot is null)
            return StatusDocumento.Rejeitado; // lote rejeitado antes de protocolar

        if (prot.Autorizada) return StatusDocumento.Autorizado;
        if (prot.Denegada) return StatusDocumento.Denegado;
        return StatusDocumento.Rejeitado;
    }

    private static void RegistrarEvento(
        DocumentoFiscal documento,
        StatusDocumento? anterior,
        StatusDocumento novo,
        string descricao,
        string? detalhe = null)
    {
        documento.Eventos.Add(new EventoDocumento
        {
            StatusAnterior = anterior,
            StatusNovo = novo,
            Descricao = descricao,
            Detalhe = detalhe,
            OcorridoEm = DateTime.UtcNow
        });
    }

    private static string Truncar(string valor, int max) =>
        valor.Length <= max ? valor : valor[..max];

    /// <summary>
    /// Mensagem legivel de uma excecao, desdobrando as inner exceptions. Sem
    /// isso, erros embrulhados aparecem so como "see inner exception" e a causa
    /// real (ex.: erro de parsing do retorno, TLS, formato de data) fica oculta —
    /// deixando o operador sem saber por que a nota nao foi transmitida. Prefixa
    /// o tipo quando a excecao NAO e uma falha conhecida da SEFAZ, para tornar
    /// obvio que se trata de um erro inesperado a investigar.
    /// </summary>
    private static string DescreverExcecao(Exception ex)
    {
        var partes = new List<string>();
        for (Exception? atual = ex; atual is not null; atual = atual.InnerException)
        {
            var texto = atual.Message?.Trim();
            if (!string.IsNullOrEmpty(texto) &&
                (partes.Count == 0 || partes[^1] != texto))
                partes.Add(texto);
        }

        var mensagem = partes.Count > 0 ? string.Join(" -> ", partes) : ex.ToString();

        var conhecida = ex is SefazSoapException or CertificadoException or SefazEndpointException;
        return conhecida ? mensagem : $"[{ex.GetType().Name}] {mensagem}";
    }

    private static Empresa CriarEmitente(Guid empresaId, EmitenteNFeRequest request) => new()
    {
        Id = empresaId,
        Cnpj = SomenteDigitos(request.Cnpj),
        RazaoSocial = request.RazaoSocial.Trim(),
        NomeFantasia = request.NomeFantasia?.Trim(),
        InscricaoEstadual = request.InscricaoEstadual.Trim(),
        InscricaoMunicipal = request.InscricaoMunicipal?.Trim(),
        CnaeFiscal = request.CnaeFiscal?.Trim(),
        RegimeTributario = request.RegimeTributario!.Value,
        Logradouro = request.Logradouro.Trim(),
        Numero = request.Numero.Trim(),
        Complemento = request.Complemento?.Trim(),
        Bairro = request.Bairro.Trim(),
        CodigoMunicipio = SomenteDigitos(request.CodigoMunicipio),
        NomeMunicipio = request.NomeMunicipio.Trim(),
        Uf = request.Uf.Trim().ToUpperInvariant(),
        Cep = SomenteDigitos(request.Cep),
        CodigoPais = string.IsNullOrWhiteSpace(request.CodigoPais) ? "1058" : request.CodigoPais.Trim(),
        NomePais = string.IsNullOrWhiteSpace(request.NomePais) ? "BRASIL" : request.NomePais.Trim(),
        Telefone = request.Telefone?.Trim()
    };

    private static string SomenteDigitos(string? valor) =>
        new((valor ?? string.Empty).Where(char.IsDigit).ToArray());

    /// <summary>
    /// Valida a configuração, o emitente e os campos fiscais antes de gerar XML.
    /// Retorna a lista de problemas legíveis (vazia = tudo certo).
    /// </summary>
    internal static List<string> ValidarRequest(NotaFiscalRequest req)
    {
        var erros = new List<string>();

        if (req.Configuracao is null)
        {
            erros.Add("Configuracao da emissao nao informada.");
        }
        else
        {
            if (req.Configuracao.Ambiente is null ||
                !Enum.IsDefined(typeof(TipoAmbiente), req.Configuracao.Ambiente.Value))
                erros.Add("Ambiente da emissao obrigatorio: 1=producao ou 2=homologacao.");
            if (!string.IsNullOrWhiteSpace(req.Configuracao.UfAutorizadorOverride) &&
                !CodigoUf.SiglaValida(req.Configuracao.UfAutorizadorOverride))
                erros.Add("UF do autorizador invalida.");
        }

        if (req.Emitente is null)
        {
            erros.Add("Cadastro do emitente nao informado no JSON.");
        }
        else
        {
            var emitente = req.Emitente;
            if (SomenteDigitos(emitente.Cnpj).Length != 14)
                erros.Add("CNPJ do emitente deve conter 14 digitos.");
            if (string.IsNullOrWhiteSpace(emitente.RazaoSocial))
                erros.Add("Razao social do emitente nao informada.");
            if (string.IsNullOrWhiteSpace(emitente.InscricaoEstadual))
                erros.Add("Inscricao estadual do emitente nao informada.");
            if (emitente.RegimeTributario is null ||
                !Enum.IsDefined(typeof(RegimeTributario), emitente.RegimeTributario.Value))
                erros.Add("Regime tributario do emitente obrigatorio ou invalido.");
            if (string.IsNullOrWhiteSpace(emitente.Logradouro))
                erros.Add("Logradouro do emitente nao informado.");
            if (string.IsNullOrWhiteSpace(emitente.Numero))
                erros.Add("Numero do endereco do emitente nao informado.");
            if (string.IsNullOrWhiteSpace(emitente.Bairro))
                erros.Add("Bairro do emitente nao informado.");
            if (SomenteDigitos(emitente.CodigoMunicipio).Length != 7)
                erros.Add("Codigo IBGE do municipio do emitente deve conter 7 digitos.");
            if (string.IsNullOrWhiteSpace(emitente.NomeMunicipio))
                erros.Add("Nome do municipio do emitente nao informado.");
            if (!CodigoUf.SiglaValida(emitente.Uf))
                erros.Add("UF do emitente invalida.");
            if (SomenteDigitos(emitente.Cep).Length != 8)
                erros.Add("CEP do emitente deve conter 8 digitos.");
        }

        // A numeracao e controlada pelo app: o numero (nNF) e obrigatorio e deve
        // ser positivo. A API nao gera mais numero automaticamente.
        if (req.Numero <= 0)
            erros.Add("Numero da nota (nNF) nao informado. A numeracao e controlada pelo app; envie um numero positivo.");
        if (req.Serie < 0)
            erros.Add("Serie invalida.");

        if (string.IsNullOrWhiteSpace(req.NaturezaOperacao))
            erros.Add("Natureza da operacao (natOp) nao informada.");

        if (req.Destinatario is null)
        {
            erros.Add("Destinatario nao informado.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Destinatario.Nome))
                erros.Add("Nome/razao social do destinatario nao informado.");
            if (string.IsNullOrWhiteSpace(req.Destinatario.Documento))
                erros.Add("Documento (CPF/CNPJ) do destinatario nao informado.");
        }

        if (req.Itens is null || req.Itens.Count == 0)
        {
            erros.Add("A nota precisa de ao menos um item.");
            return erros;
        }

        for (var i = 0; i < req.Itens.Count; i++)
        {
            var item = req.Itens[i];
            var n = i + 1; // numero do item exibido ao usuario (1-based)

            if (string.IsNullOrWhiteSpace(item.Codigo))
                erros.Add($"Item {n}: codigo do produto (cProd) nao informado.");
            if (string.IsNullOrWhiteSpace(item.Descricao))
                erros.Add($"Item {n}: descricao do produto (xProd) nao informada.");
            if (string.IsNullOrWhiteSpace(item.Ncm))
                erros.Add($"Item {n}: NCM nao informado.");
            if (string.IsNullOrWhiteSpace(item.Cfop))
                erros.Add($"Item {n}: CFOP nao informado.");
            if (!string.IsNullOrWhiteSpace(item.CBenef)
                && item.CBenef.Trim().Length is not (8 or 10))
                erros.Add($"Item {n}: cBenef deve ter 8 ou 10 caracteres (ou SEM CBENEF).");
            if (string.IsNullOrWhiteSpace(item.UnidadeComercial))
                erros.Add($"Item {n}: unidade comercial (uCom) nao informada.");
            if (item.Quantidade <= 0)
                erros.Add($"Item {n}: quantidade deve ser maior que zero.");
            if (item.ValorUnitario <= 0 && item.ValorBruto <= 0)
                erros.Add($"Item {n}: valor unitario ou valor bruto deve ser maior que zero.");
            if (ProblemaCstPisCofins(item.CstPis) is { } pis)
                erros.Add($"Item {n} ({item.Codigo}): CST de PIS {pis}");
            if (ProblemaCstPisCofins(item.CstCofins) is { } cofins)
                erros.Add($"Item {n} ({item.Codigo}): CST de COFINS {cofins}");
        }

        return erros;
    }

    /// <summary>
    /// CST de PIS/COFINS da tabela oficial: 01-09, 49, 50-56, 60-67, 70-75, 98 e 99.
    /// Devolve o problema (para a mensagem) ou null quando o CST e aceito.
    /// </summary>
    internal static string? ProblemaCstPisCofins(string? cst)
    {
        var c = (cst ?? "").Trim();
        if (c.Length == 0) return "nao informado.";
        if (c.Length != 2 || !int.TryParse(c, out var n)
            || !(n is >= 1 and <= 9 or 49 or >= 50 and <= 56 or >= 60 and <= 67 or >= 70 and <= 75 or 98 or 99))
            return $"'{c}' nao existe na tabela oficial. Use, por exemplo, 01 (aliquota basica), " +
                   "04 (monofasica), 06 (aliquota zero), 07 (isenta), 08 (sem incidencia), " +
                   "49 (outras saidas) ou 99 (outras operacoes); validos: 01-09, 49, 50-56, 60-67, 70-75, 98, 99.";
        if (n == 3)
            return "'03' (aliquota por unidade de medida) ainda nao e suportado por esta API.";
        return null;
    }
}

/// <summary>
/// Lancada quando o request de emissao nao passa na validacao previa de campos
/// obrigatorios. Carrega a lista de problemas para o endpoint devolver 400.
/// </summary>
public sealed class RequisicaoInvalidaException : Exception
{
    public IReadOnlyList<string> Problemas { get; }

    public RequisicaoInvalidaException(IReadOnlyList<string> problemas)
        : base("Requisicao de emissao invalida: " + string.Join(" ", problemas))
    {
        Problemas = problemas;
    }
}
