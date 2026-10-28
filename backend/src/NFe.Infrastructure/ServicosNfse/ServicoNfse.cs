using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Nfse;
using NFe.Nfse.Abrasf;
using NFe.Nfse.Contratos;
using NFe.Nfse.Municipios;
using NFe.Nfse.Nacional;

namespace NFe.Infrastructure.ServicosNfse;

/// <summary>Erro de negocio na NFS-e (422 na API): configuracao, validacao, estado.</summary>
public sealed class NfseNegocioException : Exception
{
    public IReadOnlyList<string> Erros { get; }
    public NfseNegocioException(string message, IReadOnlyList<string>? erros = null) : base(message)
        => Erros = erros ?? Array.Empty<string>();
}

    /// <summary>
    /// Provedor inacessivel (5xx, timeout, rede). A nota fica em ErroTransmissao e deve ser
    /// reenviada com a mesma Idempotency-Key, sem consumir novo numero de DPS.
    /// </summary>
    public sealed class NfseProvedorIndisponivelException : Exception
    {
        public NfseProvedorIndisponivelException(string message) : base(message) { }
    }

    /// <summary>Resultado da emissao para o endpoint montar a resposta.</summary>
public sealed record EmissaoNfseResultado(NotaServico Nota, bool Reaproveitada, long DuracaoMs);

/// <summary>Resultado do cancelamento para o endpoint.</summary>
public sealed record CancelamentoNfseResultado(NotaServico Nota, EventoNotaServico Evento);

/// <summary>
/// Orquestra a NFS-e de ponta a ponta: configuracao -> numeracao -> DPS -> assinatura
/// -> envio -> persistencia -> tentativa. Independente do <c>ServicoAutorizacao</c>
/// da NF-e; compartilha apenas o certificado A1 da empresa (mesmo cofre).
/// </summary>
public sealed class ServicoNfse
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly ReservadorNumeracaoDps _numeracao;
    private readonly ProvedorNacional _nacional;
    private readonly ProvedorAbrasfGenerico _abrasf;
    private readonly ProvedorOsasco _osasco;
    private readonly ArquivadorXmlNfse _arquivos;
    private readonly RegistradorTentativaNfse _tentativas;
    private readonly ILogger<ServicoNfse>? _log;

    public ServicoNfse(
        NFeDbContext db,
        ServicoCertificado certificados,
        ReservadorNumeracaoDps numeracao,
        ProvedorNacional nacional,
        ProvedorAbrasfGenerico abrasf,
        ProvedorOsasco osasco,
        ArquivadorXmlNfse arquivos,
        RegistradorTentativaNfse tentativas,
        ILogger<ServicoNfse>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _numeracao = numeracao;
        _nacional = nacional;
        _abrasf = abrasf;
        _osasco = osasco;
        _arquivos = arquivos;
        _tentativas = tentativas;
        _log = log;
    }

    private INfseProvedor Provedor(PadraoNfse padrao) => padrao switch
    {
        PadraoNfse.Nacional => _nacional,
        PadraoNfse.Abrasf => _abrasf,
        PadraoNfse.Osasco => _osasco,
        _ => throw new NfseNegocioException($"Padrao NFS-e nao suportado: {padrao}.")
    };

    // =================================================================== emissao

    public async Task<EmissaoNfseResultado> EmitirAsync(
        Guid empresaId,
        NfseRequest request,
        string? chaveIdempotencia,
        string jsonRecebido,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var empresa = await _db.Empresas.FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new NfseNegocioException($"Empresa {empresaId} nao encontrada neste servidor.");
        if (!empresa.Ativa)
            throw new NfseNegocioException($"Empresa {empresaId} esta inativa.");

        var ambiente = ResolverAmbiente(request);

        // A configuracao, inclusive as credenciais do provedor, vem SOMENTE do JSON.
        var (prestador, cfgEfetiva, errosConfig) = MontarConfiguracao(empresa, request.Configuracao);
        if (errosConfig.Count > 0)
        {
            await Falha(empresa, null, ambiente, chaveIdempotencia, request, jsonRecebido, "Configuracao", null,
                "Configuracao NFS-e do JSON invalida.", string.Join("\n", errosConfig));
            throw new NfseNegocioException("Configuracao NFS-e do JSON invalida.", errosConfig);
        }
        var provedor = Provedor(cfgEfetiva.Padrao);

        // Idempotencia: mesma chave -> mesma nota, sem re-emitir.
        if (!string.IsNullOrWhiteSpace(chaveIdempotencia))
        {
            var existente = await _db.NotasServico
                .FirstOrDefaultAsync(n => n.EmpresaId == empresaId && n.ChaveIdempotencia == chaveIdempotencia, ct);
            if (existente is not null && existente.Status is (StatusNfse.Autorizada or StatusNfse.Cancelada))
                return new EmissaoNfseResultado(existente, true, sw.ElapsedMilliseconds);
            if (existente is not null && existente.Status == StatusNfse.ErroTransmissao)
            {
                if (existente.Padrao != cfgEfetiva.Padrao)
                    throw new NfseNegocioException(
                        $"A nota a reenviar foi gerada com o padrao {(int)existente.Padrao}; " +
                        $"o JSON informa configuracao.padrao={(int)cfgEfetiva.Padrao}.");
                if (existente.Ambiente != ambiente)
                    throw new NfseNegocioException(
                        $"A nota a reenviar foi gerada no ambiente {(int)existente.Ambiente}; " +
                        $"o JSON informa configuracao.ambiente={(int)ambiente}.");
                return await ReenviarAsync(empresa, cfgEfetiva, existente, jsonRecebido, ct);
            }
            // Uma nota Rejeitada nunca virou documento fiscal: reenviar para
            // aprovacao com a MESMA chave deve funcionar. O indice unico e por
            // (empresa, chave), entao liberamos a chave do registro rejeitado e
            // seguimos emitindo uma nova. So Autorizada/Cancelada (nota valida ja
            // existente) continuam bloqueando a re-emissao.
            if (existente is not null && existente.Status == StatusNfse.Rejeitada
                && request.NumeroDps is long numeroSolicitado)
            {
                if (existente.NumeroDps == numeroSolicitado)
                    return new EmissaoNfseResultado(existente, true, sw.ElapsedMilliseconds);

                throw new NfseNegocioException(
                    "Esta Idempotency-Key ja identifica uma DPS rejeitada. " +
                    "Para emitir com outro numero, envie uma nova Idempotency-Key.");
            }
            if (existente is not null && existente.Status == StatusNfse.Rejeitada)
            {
                existente.ChaveIdempotencia = null;
                await _db.SaveChangesAsync(ct);
            }
            else if (existente is not null)
                throw new NfseNegocioException(
                    $"Ja existe uma NFS-e com esta chave de idempotencia em estado {existente.Status}. " +
                    "Use outra chave ou consulte a nota existente.");
        }

        // Validacao de negocio antes de gastar numeracao.
        var erros = GeradorXmlDps.ValidarRequest(request, prestador, cfgEfetiva).ToList();
        if (request.NumeroDps is null)
            erros.Insert(0, "numeroDps: obrigatorio. O numero da DPS/RPS vem do JSON; a API nao controla sequencia.");
        if (erros.Count > 0)
        {
            await Falha(empresa, null, ambiente, chaveIdempotencia, request, jsonRecebido, "Validacao", null,
                "Dados da NFS-e invalidos.", string.Join("\n", erros));
            throw new NfseNegocioException("Dados da NFS-e invalidos.", erros);
        }

        X509Certificate2 cert;
        try
        {
            cert = await _certificados.CarregarAtivoAsync(empresaId, ct);
        }
        catch (CertificadoException ex)
        {
            await Falha(empresa, null, ambiente, chaveIdempotencia, request, jsonRecebido, "Certificado", null, ex.Message);
            throw new NfseNegocioException(ex.Message);
        }

        using (cert)
        {
            var dataEmissao = request.DataEmissao ?? HorarioBrasilia();
            var dataCompet = (request.DataCompetencia ?? dataEmissao.Date).Date;
            var serie = ReservadorNumeracaoDps.NormalizarSerie(cfgEfetiva.SerieDps);

            NotaServico nota;

            // 1) Reserva numero + persiste DPS assinada na MESMA transacao.
            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                long numero;
                try
                {
                    numero = await _numeracao.ReservarProximoAsync(
                        empresaId, serie, ambiente, ct, request.NumeroDps);
                }
                catch (NumeracaoDpsInvalidaException ex)
                {
                    throw new NfseNegocioException(ex.Message);
                }

                var dados = new DpsDados(request, prestador, cfgEfetiva, ambiente, serie, numero, dataEmissao, dataCompet);
                var (xml, id) = provedor.GerarXml(dados);
                var assinado = provedor.Assinar(xml, id, cert);

                nota = new NotaServico
                {
                    EmpresaId = empresaId,
                    Padrao = cfgEfetiva.Padrao,
                    Ambiente = ambiente,
                    Serie = serie,
                    NumeroDps = numero,
                    IdDps = id,
                    Status = StatusNfse.Assinada,
                    DataEmissao = dataEmissao.UtcDateTime,
                    DataCompetencia = dataCompet,
                    TomadorDocumento = SoDigitos(request.Tomador?.Documento) ?? request.Tomador?.Nif,
                    TomadorNome = request.Tomador?.Nome,
                    TomadorMunicipio = request.Tomador?.CodigoMunicipio,
                    CodigoTributacaoNacional = request.Servico.CodigoTributacaoNacional,
                    DescricaoServico = request.Servico.Descricao,
                    ValorServico = request.Valores.ValorServico,
                    ValorDescontos = request.Valores.DescontoIncondicionado + request.Valores.DescontoCondicionado,
                    AliquotaIss = request.Valores.AliquotaIss,
                    ValorIss = request.Valores.ValorIss,
                    IssRetido = request.Valores.RetencaoIssqn != TipoRetencaoIssqn.NaoRetido,
                    XmlDps = xml,
                    XmlDpsAssinado = assinado,
                    ChaveIdempotencia = string.IsNullOrWhiteSpace(chaveIdempotencia) ? null : chaveIdempotencia
                };
                _db.NotasServico.Add(nota);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            _arquivos.SalvarDpsAssinada(empresa, nota);

            // 2) Transmite (fora da transacao: rede nao segura lock de banco).
            return await TransmitirAsync(empresa, cfgEfetiva, provedor, nota, cert, sw, jsonRecebido, ct);
        }
    }

    /// <summary>Reenvio idempotente de uma nota que ficou em ErroTransmissao.</summary>
    private async Task<EmissaoNfseResultado> ReenviarAsync(
        Empresa empresa, EmpresaNfseConfig cfg, NotaServico nota, string jsonRecebido, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var provedor = Provedor(nota.Padrao);
        using var cert = await _certificados.CarregarAtivoAsync(empresa.Id, ct);
        return await TransmitirAsync(empresa, cfg, provedor, nota, cert, sw, jsonRecebido, ct);
    }

    private async Task<EmissaoNfseResultado> TransmitirAsync(
        Empresa empresa, EmpresaNfseConfig cfg, INfseProvedor provedor, NotaServico nota,
        X509Certificate2 cert, Stopwatch sw, string jsonRecebido, CancellationToken ct)
    {
        nota.TentativasEnvio += 1;
        nota.Status = StatusNfse.Enviada;
        nota.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        ResultadoEmissaoNfse r;
        try
        {
            r = await provedor.EmitirAsync(nota.XmlDpsAssinado!, cfg, nota.Ambiente, cert, ct);
        }
        catch (Exception ex) when (ex is NfseProvedorException or HttpRequestException or TaskCanceledException)
        {
            nota.Status = StatusNfse.ErroTransmissao;
            nota.UltimoErro = Limitar(ex.Message, 2000);
            nota.RetornoBruto = (ex as NfseProvedorException)?.CorpoRecebido;
            nota.AtualizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _arquivos.SalvarRetorno(empresa, nota);

            await Registrar(empresa, nota, "Transmissao", null, false,
                $"Falha ao transmitir a DPS: {ex.Message}", (ex as NfseProvedorException)?.CorpoRecebido,
                jsonRecebido);

            throw new NfseProvedorIndisponivelException(
                $"Falha de comunicacao com o provedor de NFS-e: {ex.Message} " +
                $"A nota nDPS {nota.NumeroDps} NAO foi rejeitada: repita a chamada com a mesma " +
                "Idempotency-Key e o mesmo numeroDps para reenviar.");
        }

        nota.RetornoBruto = r.RetornoBruto;
        nota.CodigoRetorno = r.Codigo;
        nota.MotivoRetorno = Limitar(r.Motivo, 1000);
        nota.AtualizadoEm = DateTime.UtcNow;

        if (r.Sucesso)
        {
            nota.Status = StatusNfse.Autorizada;
            nota.ChaveAcesso = r.ChaveAcesso;
            nota.NumeroNfse = r.NumeroNfse;
            nota.CodigoVerificacao = r.CodigoVerificacao;
            nota.XmlNfse = r.XmlNfse;
            nota.DataAutorizacao = r.DataAutorizacao ?? DateTime.UtcNow;
            nota.UltimoErro = null;
            await _db.SaveChangesAsync(ct);

            _arquivos.SalvarNfse(empresa, nota);
            _arquivos.SalvarRetorno(empresa, nota);
            await Registrar(empresa, nota, "Autorizada", r.Codigo, true,
                $"NFS-e {nota.NumeroNfse} autorizada. Chave {nota.ChaveAcesso}.", null, jsonRecebido);

            _log?.LogInformation("[nfse] NFS-e autorizada: empresa {Empresa} nDPS {N} chave {Chave} ({Ms} ms)",
                empresa.Cnpj, nota.NumeroDps, nota.ChaveAcesso, sw.ElapsedMilliseconds);
            return new EmissaoNfseResultado(nota, false, sw.ElapsedMilliseconds);
        }

        nota.Status = StatusNfse.Rejeitada;
        var diagnostico = DiagnosticoRejeicao(r.Codigo, r.Motivo, nota.XmlDpsAssinado);
        if (diagnostico is not null) r = r with { Motivo = $"{r.Motivo} {diagnostico}" };
        nota.UltimoErro = Limitar($"{r.Codigo} - {r.Motivo}", 2000);
        await _db.SaveChangesAsync(ct);
        _arquivos.SalvarRetorno(empresa, nota);
        await Registrar(empresa, nota, "Rejeicao", r.Codigo, false,
            $"DPS rejeitada pelo provedor: {r.Codigo} - {r.Motivo}", r.RetornoBruto, jsonRecebido);

        throw new NfseNegocioException($"DPS rejeitada: {r.Codigo} - {r.Motivo}");
    }

    public static string? DiagnosticoRejeicao(string? codigo, string? motivo, string? xmlDps)
    {
        var ehE0314 = (codigo?.Contains("E0314") ?? false) || (motivo?.Contains("E0314") ?? false);
        if (!ehE0314 || string.IsNullOrWhiteSpace(xmlDps)) return null;

        var m = System.Text.RegularExpressions.Regex.Match(xmlDps, "<(?:\\w+:)?cTribMun>([^<]*)</");
        return m.Success
            ? $"[Diagnostico: a DPS enviou cTribMun={m.Groups[1].Value}. Esse codigo nao esta cadastrado para o municipio; " +
              "envie servico.codigoTributacaoMunicipal=null ou o codigo de 3 digitos exato do cadastro municipal.]"
            : "[Diagnostico: a DPS NAO enviou cTribMun. O municipio exige um codigo de tributacao municipal cadastrado; " +
              "informe servico.codigoTributacaoMunicipal com o codigo de 3 digitos fornecido pela prefeitura.]";
    }

    // ================================================================== consulta

    /// <summary>Consulta a NFS-e no provedor pela chave e sincroniza a copia local, se houver.</summary>
    public async Task<(ResultadoConsultaNfse Resultado, NotaServico? Local)> ConsultarPorChaveAsync(
        Guid empresaId, string chaveAcesso, PadraoNfse? padraoInformado = null,
        TipoAmbiente? ambienteInformado = null, ConfiguracaoNfseRequest? configuracao = null,
        CancellationToken ct = default)
    {
        chaveAcesso = SoDigitos(chaveAcesso) ?? "";
        if (chaveAcesso.Length != 50)
            throw new NfseNegocioException("Chave de acesso da NFS-e deve ter 50 digitos.");

        _ = await _db.Empresas.FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new NfseNegocioException($"Empresa {empresaId} nao encontrada neste servidor.");

        var local = await _db.NotasServico
            .FirstOrDefaultAsync(n => n.EmpresaId == empresaId && n.ChaveAcesso == chaveAcesso, ct);

        // Padrao e ambiente vem da propria nota (gravados a partir do JSON da
        // emissao) ou, se a nota nao e local, dos parametros da consulta.
        var padrao = local?.Padrao ?? padraoInformado
            ?? throw new NfseNegocioException(
                "Nota nao encontrada localmente: informe o parametro padrao (1=Nacional, 2=ABRASF, 3=Osasco).");
        var ambiente = local?.Ambiente ?? ambienteInformado
            ?? throw new NfseNegocioException(
                "Nota nao encontrada localmente: informe o parametro ambiente (1=producao, 2=homologacao).");
        if (!Enum.IsDefined(typeof(PadraoNfse), padrao) || !Enum.IsDefined(typeof(TipoAmbiente), ambiente))
            throw new NfseNegocioException("Padrao ou ambiente invalido na consulta.");

        var cfg = ConfiguracaoParaNota(empresaId, padrao, local?.Serie, configuracao, ambiente);

        using var cert = await _certificados.CarregarAtivoAsync(empresaId, ct);
        var chaveConsulta = padrao == PadraoNfse.Osasco && !string.IsNullOrWhiteSpace(local?.NumeroNfse)
            ? local.NumeroNfse
            : chaveAcesso;
        var r = await Provedor(padrao).ConsultarAsync(chaveConsulta, cfg, ambiente, cert, ct);

        if (local is not null && r.Encontrada)
        {
            if (r.Cancelada && local.Status != StatusNfse.Cancelada) local.Status = StatusNfse.Cancelada;
            if (string.IsNullOrWhiteSpace(local.XmlNfse) && !string.IsNullOrWhiteSpace(r.XmlNfse))
                local.XmlNfse = r.XmlNfse;
            if (string.IsNullOrWhiteSpace(local.NumeroNfse) && !string.IsNullOrWhiteSpace(r.NumeroNfse))
                local.NumeroNfse = r.NumeroNfse;
            local.AtualizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return (r, local);
    }

    // ============================================================== cancelamento

    public async Task<CancelamentoNfseResultado> CancelarAsync(
        Guid empresaId, Guid notaId, string codigoMotivo, string justificativa,
        ConfiguracaoNfseRequest? configuracao = null, CancellationToken ct = default)
    {
        var nota = await _db.NotasServico.Include(n => n.Eventos)
            .FirstOrDefaultAsync(n => n.Id == notaId && n.EmpresaId == empresaId, ct)
            ?? throw new NfseNegocioException("NFS-e nao encontrada para esta empresa.");

        if (nota.Status == StatusNfse.Cancelada)
        {
            // Ja cancelada: nao reenvia a prefeitura; devolve os dados do cancelamento existente.
            var existente = nota.Eventos
                .Where(e => e.TipoEvento == GeradorXmlEventoNfse.TipoCancelamento && e.Homologado)
                .OrderByDescending(e => e.Sequencia)
                .FirstOrDefault()
                ?? new EventoNotaServico
                {
                    NotaServicoId = nota.Id,
                    ChaveAcesso = nota.ChaveAcesso ?? "",
                    TipoEvento = GeradorXmlEventoNfse.TipoCancelamento,
                    Sequencia = 1,
                    CodigoMotivo = codigoMotivo,
                    Justificativa = (justificativa ?? "").Trim(),
                    Homologado = true,
                    DataRegistro = nota.AtualizadoEm
                };
            return new CancelamentoNfseResultado(nota, existente);
        }
        if (nota.Status != StatusNfse.Autorizada || string.IsNullOrWhiteSpace(nota.ChaveAcesso))
            throw new NfseNegocioException("Somente NFS-e autorizadas (com chave) podem ser canceladas.");

        justificativa = (justificativa ?? "").Trim();
        if (justificativa.Length < 15 || justificativa.Length > 255)
            throw new NfseNegocioException("A justificativa deve ter entre 15 e 255 caracteres.");
        if (nota.Padrao == PadraoNfse.Nacional && !GeradorXmlEventoNfse.MotivosCancelamento.ContainsKey(codigoMotivo ?? ""))
            throw new NfseNegocioException(
                "Codigo de motivo invalido. Use: " +
                string.Join(", ", GeradorXmlEventoNfse.MotivosCancelamento.Select(k => $"{k.Key}={k.Value}")));

        var empresa = await _db.Empresas.FirstAsync(e => e.Id == empresaId, ct);
        // Padrao e ambiente sao os da propria nota (vieram do JSON da emissao).
        // Credenciais: somente as do JSON do cancelamento.
        var cfg = ConfiguracaoParaNota(empresaId, nota.Padrao, nota.Serie, configuracao, nota.Ambiente);

        var sequencia = nota.Eventos.Count(e => e.TipoEvento == GeradorXmlEventoNfse.TipoCancelamento) + 1;

        using var cert = await _certificados.CarregarAtivoAsync(empresaId, ct);

        ResultadoEventoNfse r;
        try
        {
            r = await Provedor(nota.Padrao).CancelarAsync(nota, codigoMotivo!, justificativa, sequencia, cfg, cert, ct);
        }
        catch (Exception ex) when (ex is NfseProvedorException or HttpRequestException or TaskCanceledException)
        {
            throw new NfseNegocioException($"Falha de comunicacao ao cancelar: {ex.Message}");
        }

        var ev = new EventoNotaServico
        {
            NotaServicoId = nota.Id,
            ChaveAcesso = nota.ChaveAcesso!,
            TipoEvento = GeradorXmlEventoNfse.TipoCancelamento,
            Sequencia = sequencia,
            CodigoMotivo = codigoMotivo,
            Justificativa = justificativa,
            Protocolo = r.Protocolo,
            CodigoStatus = r.Codigo,
            Motivo = Limitar(r.Motivo, 1000),
            Homologado = r.Homologado,
            DataRegistro = r.DataRegistro,
            XmlEnviado = r.XmlEnviado,
            XmlRetorno = r.RetornoBruto
        };
        _db.EventosNotaServico.Add(ev);

        if (r.Homologado)
        {
            nota.Status = StatusNfse.Cancelada;
            nota.AtualizadoEm = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        _arquivos.SalvarEvento(empresa, nota, ev);

        if (!r.Homologado)
            throw new NfseNegocioException($"Cancelamento nao homologado: {r.Codigo} - {r.Motivo}");

        return new CancelamentoNfseResultado(nota, ev);
    }

    // ================================================================ auxiliares

    /// <summary>
    /// Aplica os overrides enviados pelo app sobre COPIAS desacopladas de Empresa
    /// e EmpresaNfseConfig. As entidades rastreadas pelo EF nunca sao tocadas, de
    /// modo que os valores do app valem so para esta emissao e nada e persistido.
    /// Sem overrides, devolve as instancias originais (custo zero).
    /// </summary>
    internal static TipoAmbiente ResolverAmbiente(NfseRequest request)
        => request.Configuracao?.Ambiente
           ?? throw new NfseNegocioException(
               "Informe configuracao.ambiente no JSON: 1=producao ou 2=homologacao.");

    /// <summary>
    /// Monta o prestador e a configuracao da emissao exclusivamente a partir do
    /// JSON. Nenhum valor tributario ou de emissao cai em default nem vem do
    /// cadastro: campo ausente vira erro. As credenciais do provedor (chave
    /// Osasco, URL/usuario/senha ABRASF) tambem vem so do JSON. Trabalha em
    /// copias; nada e persistido.
    /// </summary>
    internal static (Empresa Prestador, EmpresaNfseConfig Config, List<string> Erros) MontarConfiguracao(
        Empresa empresa, ConfiguracaoNfseRequest? o)
    {
        var erros = new List<string>();
        if (o is null)
        {
            erros.Add("O objeto 'configuracao' e obrigatorio no JSON.");
            return (empresa, new EmpresaNfseConfig { EmpresaId = empresa.Id }, erros);
        }

        var municipio = SoDigitos(o.CodigoMunicipioEmissao);
        if (municipio is not { Length: 7 })
            erros.Add("configuracao.codigoMunicipioEmissao: obrigatorio, codigo IBGE de 7 digitos.");

        // O padrao so e obrigatorio quando o municipio nao consta no catalogo ACBr.
        var catalogo = CatalogoMunicipiosNfse.Instancia.Buscar(municipio);
        PadraoNfse? padraoResolvido = o.Padrao is { } informado && Enum.IsDefined(typeof(PadraoNfse), informado)
            ? informado
            : catalogo?.Padrao;
        if (o.Padrao is { } p && !Enum.IsDefined(typeof(PadraoNfse), p))
            erros.Add("configuracao.padrao: invalido. Use 1=Nacional, 2=ABRASF ou 3=Osasco, ou omita para usar o catalogo.");
        else if (padraoResolvido is null && municipio is { Length: 7 })
            erros.Add(
                $"configuracao.padrao: o municipio {municipio} nao tem provedor no catalogo ACBrNFSeXServicos.ini. " +
                "Informe configuracao.padrao (1=Nacional, 2=ABRASF com abrasfUrl, 3=Osasco).");
        if (o.Ambiente is not { } amb || !Enum.IsDefined(typeof(TipoAmbiente), amb))
            erros.Add("configuracao.ambiente: obrigatorio. Use 1=producao ou 2=homologacao.");

        var serie = string.IsNullOrWhiteSpace(o.Serie) ? null : o.Serie.Trim();
        if (serie is null) erros.Add("configuracao.serie: obrigatoria.");
        else
        {
            try { serie = ReservadorNumeracaoDps.NormalizarSerie(serie); }
            catch (NumeracaoDpsInvalidaException ex) { erros.Add($"configuracao.serie: {ex.Message}"); }
        }

        if (o.OpSimpNac is not { } op || !Enum.IsDefined(typeof(OpcaoSimplesNacional), op))
            erros.Add("configuracao.opSimpNac: obrigatorio. Use 1=nao optante, 2=MEI ou 3=ME/EPP.");
        else if (op == OpcaoSimplesNacional.OptanteMeEpp && o.RegApTribSN is not (>= 1 and <= 3))
            erros.Add("configuracao.regApTribSN: obrigatorio (1 a 3) quando opSimpNac=3.");

        if (o.RegEspTrib is not { } re || !Enum.IsDefined(typeof(RegimeEspecialTributacao), re))
            erros.Add("configuracao.regEspTrib: obrigatorio. Use 0 quando nao houver regime especial.");

        if (string.IsNullOrWhiteSpace(o.InscricaoMunicipal))
            erros.Add("configuracao.inscricaoMunicipal: obrigatoria.");

        var c = new EmpresaNfseConfig
        {
            Id = Guid.NewGuid(),
            EmpresaId = empresa.Id,
            Padrao = padraoResolvido ?? PadraoNfse.Nacional,
            SerieDps = serie ?? "",
            OpSimpNac = o.OpSimpNac ?? OpcaoSimplesNacional.NaoOptante,
            RegApTribSN = o.RegApTribSN ?? 1,
            RegEspTrib = o.RegEspTrib ?? RegimeEspecialTributacao.Nenhum,
            CodigoMunicipioEmissao = municipio,
            Ativa = true
        };
        if (!string.IsNullOrWhiteSpace(o.VersaoAplicativo)) c.VersaoAplicativo = o.VersaoAplicativo.Trim();
        AplicarCredenciais(c, o, o.Ambiente);

        if (erros.Count == 0) erros.AddRange(ErrosCredenciais(c));

        var prest = ClonarEmpresa(empresa);
        prest.InscricaoMunicipal = o.InscricaoMunicipal?.Trim();
        if (municipio is not null) prest.CodigoMunicipio = municipio;
        return (prest, c, erros);
    }

    /// <summary>
    /// Configuracao minima para consultar/cancelar uma nota ja emitida: padrao
    /// e serie vem da propria nota; as credenciais vem exclusivamente do JSON da
    /// requisicao (consulta ou cancelamento) e sao obrigatorias para Osasco e ABRASF.
    /// </summary>
    internal static EmpresaNfseConfig ConfiguracaoParaNota(
        Guid empresaId, PadraoNfse padrao, string? serie, ConfiguracaoNfseRequest? o,
        TipoAmbiente? ambiente = null)
    {
        var c = new EmpresaNfseConfig
        {
            EmpresaId = empresaId,
            Padrao = padrao,
            SerieDps = serie ?? "",
            CodigoMunicipioEmissao = SoDigitos(o?.CodigoMunicipioEmissao),
            Ativa = true
        };
        if (!string.IsNullOrWhiteSpace(o?.VersaoAplicativo)) c.VersaoAplicativo = o.VersaoAplicativo.Trim();
        AplicarCredenciais(c, o, ambiente ?? o?.Ambiente);

        var erros = ErrosCredenciais(c);
        if (erros.Count > 0)
            throw new NfseNegocioException("Credenciais do provedor ausentes no JSON.", erros);
        return c;
    }

    private static void AplicarCredenciais(EmpresaNfseConfig c, ConfiguracaoNfseRequest? o, TipoAmbiente? ambiente)
    {
        static string? Json(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        c.OsascoChaveAutenticacao = Json(o?.OsascoChaveAutenticacao);
        c.AbrasfUrl = Json(o?.AbrasfUrl);
        c.AbrasfVersao = Json(o?.AbrasfVersao);
        c.AbrasfUsuario = Json(o?.AbrasfUsuario);
        c.AbrasfSenha = Json(o?.AbrasfSenha);

        // URL e versao ABRASF nao informadas no JSON vem do catalogo ACBr do municipio.
        if (c.Padrao == PadraoNfse.Abrasf && ambiente is { } amb
            && (c.AbrasfUrl is null || c.AbrasfVersao is null))
        {
            var m = CatalogoMunicipiosNfse.Instancia.Buscar(c.CodigoMunicipioEmissao);
            c.AbrasfUrl ??= Json(m?.UrlPara(amb));
            c.AbrasfVersao ??= Json(m?.Versao);
        }
    }

    private static List<string> ErrosCredenciais(EmpresaNfseConfig c)
    {
        var erros = new List<string>();
        if (c.Padrao == PadraoNfse.Osasco && string.IsNullOrWhiteSpace(c.OsascoChaveAutenticacao))
            erros.Add("configuracao.osascoChaveAutenticacao: obrigatoria para o padrao Osasco.");
        if (c.Padrao == PadraoNfse.Abrasf && string.IsNullOrWhiteSpace(c.AbrasfUrl))
            erros.Add(
                "configuracao.abrasfUrl: obrigatoria para o padrao ABRASF quando o municipio " +
                "(codigoMunicipioEmissao) nao tem URL deste ambiente no catalogo ACBrNFSeXServicos.ini.");
        return erros;
    }

    /// <summary>Copia desacoplada do EF com os campos lidos na geracao da NFS-e.</summary>
    private static Empresa ClonarEmpresa(Empresa e) => new()
    {
        Id = e.Id,
        Cnpj = e.Cnpj,
        RazaoSocial = e.RazaoSocial,
        NomeFantasia = e.NomeFantasia,
        InscricaoEstadual = e.InscricaoEstadual,
        InscricaoMunicipal = e.InscricaoMunicipal,
        CnaeFiscal = e.CnaeFiscal,
        RegimeTributario = e.RegimeTributario,
        Logradouro = e.Logradouro,
        Numero = e.Numero,
        Complemento = e.Complemento,
        Bairro = e.Bairro,
        CodigoMunicipio = e.CodigoMunicipio,
        NomeMunicipio = e.NomeMunicipio,
        Uf = e.Uf,
        Cep = e.Cep,
        CodigoPais = e.CodigoPais,
        NomePais = e.NomePais,
        Telefone = e.Telefone,
        AmbientePadrao = e.AmbientePadrao,
        UfAutorizadorOverride = e.UfAutorizadorOverride,
        Ativa = e.Ativa
    };

    private Task Falha(Empresa empresa, NotaServico? nota, TipoAmbiente ambiente, string? idem,
        NfseRequest req, string jsonRecebido, string fase, string? codigo, string mensagem, string? detalhe = null)
        => _tentativas.RegistrarAsync(new TentativaEmissaoNfse
        {
            EmpresaId = empresa.Id,
            NotaServicoId = nota?.Id,
            Ambiente = (int)ambiente,
            Serie = nota?.Serie,
            NumeroDps = nota?.NumeroDps,
            ChaveIdempotencia = idem,
            Sucesso = false,
            Fase = fase,
            CodigoStatus = codigo,
            Mensagem = mensagem,
            Detalhe = detalhe,
            TomadorDocumento = SoDigitos(req.Tomador?.Documento),
            TomadorNome = req.Tomador?.Nome,
            ValorServico = req.Valores.ValorServico,
            JsonRecebido = jsonRecebido
        });

    private Task Registrar(Empresa empresa, NotaServico nota, string fase, string? codigo, bool sucesso,
        string mensagem, string? detalhe, string jsonRecebido)
        => _tentativas.RegistrarAsync(new TentativaEmissaoNfse
        {
            EmpresaId = empresa.Id,
            NotaServicoId = nota.Id,
            Ambiente = (int)nota.Ambiente,
            Serie = nota.Serie,
            NumeroDps = nota.NumeroDps,
            ChaveIdempotencia = nota.ChaveIdempotencia,
            Sucesso = sucesso,
            Fase = fase,
            CodigoStatus = codigo,
            Mensagem = mensagem,
            Detalhe = detalhe,
            ChaveAcesso = nota.ChaveAcesso,
            TomadorDocumento = nota.TomadorDocumento,
            TomadorNome = nota.TomadorNome,
            ValorServico = nota.ValorServico,
            JsonRecebido = jsonRecebido
        });

    private static DateTimeOffset HorarioBrasilia()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        }
    }

    private static string? SoDigitos(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }

    private static string? Limitar(string? v, int max)
        => v is null || v.Length <= max ? v : v[..max];
}
