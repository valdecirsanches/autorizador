using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Orquestra a inutilizacao de faixas de numeracao (servico NfeInutilizacao4).
/// Inutilizar declara a SEFAZ que uma faixa [inicial, final] de uma serie NAO
/// sera usada — usada quando ha quebra de sequencia (numeros "queimados" por
/// falha de sistema). E IRREVERSIVEL. O resultado (homologado ou rejeitado) e
/// sempre persistido, com o XML bruto, para auditoria e guarda fiscal.
/// </summary>
public sealed class ServicoInutilizacao
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly AssinadorXml _assinador;
    private readonly InutilizacaoClient _cliente;
    private readonly SefazEndpointRegistry _registry;
    private readonly ILogger<ServicoInutilizacao>? _log;

    public ServicoInutilizacao(
        NFeDbContext db,
        ServicoCertificado certificados,
        AssinadorXml assinador,
        InutilizacaoClient cliente,
        SefazEndpointRegistry registry,
        ILogger<ServicoInutilizacao>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _assinador = assinador;
        _cliente = cliente;
        _registry = registry;
        _log = log;
    }

    /// <summary>
    /// Inutiliza a faixa [numeroInicial, numeroFinal] de uma serie. Idempotente:
    /// se a mesma faixa ja tiver sido inutilizada com sucesso, devolve o registro
    /// existente sem reenviar.
    /// </summary>
    public async Task<InutilizacaoNumeracao> InutilizarAsync(
        Guid empresaId,
        string uf,
        string cnpj,
        string? ufAutorizadorOverride,
        ModeloDocumento modelo,
        int serie,
        TipoAmbiente ambiente,
        int ano,
        long numeroInicial,
        long numeroFinal,
        string justificativa,
        CancellationToken ct = default)
    {
        // ---- Validacoes de dominio (antes de qualquer efeito colateral) ----
        if (!CodigoUf.SiglaValida(uf))
            throw new InutilizacaoInvalidaException("UF do emitente invalida.");
        uf = uf.Trim().ToUpperInvariant();

        var cnpjDigitos = new string((cnpj ?? string.Empty).Where(char.IsDigit).ToArray());
        if (cnpjDigitos.Length != 14)
            throw new InutilizacaoInvalidaException("CNPJ do emitente deve ter 14 digitos.");

        if (!string.IsNullOrWhiteSpace(ufAutorizadorOverride) &&
            !CodigoUf.SiglaValida(ufAutorizadorOverride))
            throw new InutilizacaoInvalidaException("UF do autorizador invalida.");
        ufAutorizadorOverride = string.IsNullOrWhiteSpace(ufAutorizadorOverride)
            ? null : ufAutorizadorOverride.Trim().ToUpperInvariant();

        if (serie is < 0 or > 999)
            throw new InutilizacaoInvalidaException("A serie deve estar entre 0 e 999.");
        if (numeroInicial < 1 || numeroFinal < 1)
            throw new InutilizacaoInvalidaException("Os numeros da faixa devem ser maiores que zero.");
        if (numeroInicial > numeroFinal)
            throw new InutilizacaoInvalidaException("O numero inicial nao pode ser maior que o final.");
        if (numeroFinal > 999_999_999)
            throw new InutilizacaoInvalidaException("Os numeros nao podem exceder 9 digitos.");

        var anoNorm = ano <= 0 ? DateTime.UtcNow.Year : ano;

        var just = (justificativa ?? string.Empty).Trim();
        if (just.Length is < 15 or > 255)
            throw new InutilizacaoInvalidaException(
                "A justificativa da inutilizacao deve ter de 15 a 255 caracteres.");

        // ---- Idempotencia: mesma faixa ja inutilizada com sucesso ----------
        var existente = await _db.InutilizacoesNumeracao
            .Where(i => i.EmpresaId == empresaId && i.Modelo == modelo && i.Serie == serie
                        && i.Ambiente == ambiente && i.NumeroInicial == numeroInicial
                        && i.NumeroFinal == numeroFinal && i.Homologado)
            .OrderByDescending(i => i.CriadoEm)
            .FirstOrDefaultAsync(ct);
        if (existente is not null) return existente;

        // ---- Seguranca: nao inutilizar numero de nota realmente usada ------
        // Numeros AUTORIZADOS ou CANCELADOS ja existem na SEFAZ; inutiliza-los
        // seria rejeitado (e conceitualmente errado). Barramos cedo com mensagem
        // clara em vez de deixar a SEFAZ recusar.
        var conflitante = await _db.DocumentosFiscais
            .Where(d => d.EmpresaId == empresaId && d.Modelo == modelo && d.Serie == serie
                        && d.Ambiente == ambiente
                        && d.Numero >= numeroInicial && d.Numero <= numeroFinal
                        && (d.Status == StatusDocumento.Autorizado
                            || d.Status == StatusDocumento.Cancelado))
            .Select(d => (long?)d.Numero)
            .FirstOrDefaultAsync(ct);
        if (conflitante is not null)
            throw new InutilizacaoInvalidaException(
                $"A faixa contem a NF-e numero {conflitante}, que ja foi autorizada/cancelada e " +
                "nao pode ser inutilizada. Ajuste a faixa para conter apenas numeros nao utilizados.");

        // A empresa salva e apenas o vinculo com o certificado; o CNPJ do JSON
        // precisa ser o dela para nao assinar com o certificado errado.
        var empresa = await _db.Empresas
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        var cnpjCadastro = new string((empresa.Cnpj ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!string.Equals(cnpjCadastro, cnpjDigitos, StringComparison.Ordinal))
            throw new InutilizacaoInvalidaException(
                "O CNPJ informado nao corresponde ao CNPJ da empresa vinculada ao certificado.");

        var codigoUf = _registry.ObterCodigoUf(uf);

        var registro = new InutilizacaoNumeracao
        {
            EmpresaId = empresaId,
            Modelo = modelo,
            Serie = serie,
            Ambiente = ambiente,
            Ano = anoNorm,
            NumeroInicial = numeroInicial,
            NumeroFinal = numeroFinal,
            Justificativa = just,
            CriadoEm = DateTime.UtcNow
        };

        var dados = new InutilizacaoClient.DadosInutilizacao(
            Uf: uf,
            Ambiente: ambiente,
            CodigoUf: codigoUf,
            Ano: anoNorm,
            Cnpj: cnpjDigitos,
            Modelo: modelo,
            Serie: serie,
            NumeroInicial: numeroInicial,
            NumeroFinal: numeroFinal,
            Justificativa: just);

        try
        {
            using var certificado = await _certificados.CarregarAtivoSefazAsync(empresaId, ct);

            var (xml, idRef) = InutilizacaoClient.Montar(dados);

            // A Signature da inutilizacao e IRMA de infInut, filha de <inutNFe>.
            var xmlAssinado = _assinador.Assinar(xml, idRef, certificado, nomeTagAssinatura: "inutNFe");

            var resultado = await _cliente.EnviarAsync(
                dados, xmlAssinado, certificado,
                autorizadorOverride: ufAutorizadorOverride, ct: ct);

            registro.IdInutilizacao = resultado.IdInutilizacao;
            registro.CodigoStatus = resultado.CodigoStatus;
            registro.Motivo = string.IsNullOrWhiteSpace(resultado.Motivo)
                ? $"Inutilizacao cStat {resultado.CodigoStatus}." : resultado.Motivo;
            registro.Protocolo = resultado.Protocolo;
            registro.DataRegistro = resultado.DataRegistro;
            registro.Homologado = resultado.Homologado;
            registro.XmlEnviado = resultado.XmlEnviado;
            registro.XmlRetorno = resultado.XmlRecebido;
            registro.XmlInutilizacao = resultado.XmlDistribuicao;

            _db.InutilizacoesNumeracao.Add(registro);
            await _db.SaveChangesAsync(ct);

            if (resultado.Homologado)
                _log?.LogInformation(
                    "Inutilizacao homologada: empresa {Empresa} serie {Serie} {Ini}-{Fin} ({Amb}), protocolo {Prot}.",
                    empresaId, serie, numeroInicial, numeroFinal, ambiente, resultado.Protocolo);

            return registro;
        }
        catch (SefazSoapException ex)
        {
            // Rejeicao COM resposta da SEFAZ: persiste como nao homologado com o
            // XML bruto e devolve. Sem resposta (rede): falha transitoria, rethrow.
            if (string.IsNullOrWhiteSpace(ex.EnvelopeRecebido))
                throw;

            registro.Homologado = false;
            registro.Motivo = ex.Message;
            registro.XmlRetorno = ex.EnvelopeRecebido;
            _db.InutilizacoesNumeracao.Add(registro);
            await _db.SaveChangesAsync(ct);
            return registro;
        }
    }

    /// <summary>Lista as inutilizacoes de uma empresa, mais recentes primeiro.</summary>
    public async Task<IReadOnlyList<InutilizacaoNumeracao>> ListarAsync(
        Guid empresaId, CancellationToken ct = default) =>
        await _db.InutilizacoesNumeracao
            .Where(i => i.EmpresaId == empresaId)
            .OrderByDescending(i => i.CriadoEm)
            .ToListAsync(ct);
}

/// <summary>Erro de regra de negocio ao inutilizar uma faixa de numeracao.</summary>
public sealed class InutilizacaoInvalidaException : Exception
{
    public InutilizacaoInvalidaException(string message) : base(message) { }
}
