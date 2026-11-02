using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Nfce;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Xml;

namespace NFe.EmissorLocal;

/// <summary>Resultado da emissao local: a nota (nova ou ja existente) ou os problemas do pedido.</summary>
public sealed record ResultadoEmissaoLocal(ItemFila? Item, IReadOnlyList<string> Problemas, bool JaExistia = false)
{
    public bool Ok => Item is not null && Problemas.Count == 0;
}

/// <summary>
/// Gera, assina e aplica o QR Code v3 assinado de uma NFC-e em contingencia
/// off-line (tpEmis 9), com o mesmo gerador da API central, e guarda na fila.
/// </summary>
public sealed class EmissaoContingencia
{
    private readonly OpcoesEmissorLocal _opcoes;
    private readonly SefazEndpointRegistry _registry;
    private readonly CertificadoEmissor _certificado;
    private readonly FilaContingencia _fila;
    private readonly ILogger<EmissaoContingencia> _log;
    private readonly GeradorXmlNFe _gerador = new();
    private readonly AssinadorXml _assinador = new();
    private readonly SemaphoreSlim _emissao = new(1, 1);

    public EmissaoContingencia(
        OpcoesEmissorLocal opcoes, SefazEndpointRegistry registry, CertificadoEmissor certificado,
        FilaContingencia fila, ILogger<EmissaoContingencia> log)
    {
        _opcoes = opcoes;
        _registry = registry;
        _certificado = certificado;
        _fila = fila;
        _log = log;
    }

    public async Task<ResultadoEmissaoLocal> EmitirAsync(Guid empresaId, NotaFiscalRequest req, CancellationToken ct)
    {
        // Uma emissao por vez: evita duas notas para o mesmo numero em pedidos repetidos.
        await _emissao.WaitAsync(ct);
        try
        {
            return Emitir(empresaId, req);
        }
        finally
        {
            _emissao.Release();
        }
    }

    private ResultadoEmissaoLocal Emitir(Guid empresaId, NotaFiscalRequest req)
    {
        if (req.Modelo != ModeloDocumento.NFCe)
            return Falha("O emissor local so emite NFC-e (modelo 65).");

        req.Configuracao ??= new ConfiguracaoNFeRequest();
        req.Configuracao.ContingenciaOffline = true;
        if (string.IsNullOrWhiteSpace(req.Configuracao.JustificativaContingencia))
            req.Configuracao.JustificativaContingencia = _opcoes.JustificativaContingencia;
        req.Configuracao.DataContingencia ??= req.DataEmissao;

        ValidadorNotaFiscal.Normalizar(req);
        var problemas = ValidadorNotaFiscal.Validar(req);
        if (problemas.Count > 0) return new ResultadoEmissaoLocal(null, problemas);

        var cnpj = SoDigitos(req.Emitente!.Cnpj);
        if (_certificado.Cnpj is { } cnpjCert && cnpjCert != cnpj)
            return Falha($"O CNPJ do emitente ({cnpj}) nao e o do certificado instalado ({cnpjCert}).");

        var ambiente = req.Configuracao.Ambiente!.Value;

        // Idempotencia: o mesmo numero devolve a MESMA nota (a chave tem um
        // codigo aleatorio; gerar de novo criaria outra nota para o numero).
        var existente = _fila.BuscarPorNumero(cnpj, (int)ambiente, req.Serie, req.Numero);
        if (existente is not null && existente.Situacao != SituacaoFila.Rejeitada)
            return new ResultadoEmissaoLocal(existente, [], JaExistia: true);

        var certificado = _certificado.ParaAssinar();
        var emit = MapearEmitente(empresaId, req.Emitente);

        string xml, qr, urlChave;
        NFeGerada gerada;
        try
        {
            gerada = _gerador.Gerar(emit, req, ambiente);
            var assinado = _assinador.Assinar(gerada.Xml, "NFe" + gerada.ChaveAcesso, certificado);
            (xml, qr, urlChave) = QrCodeNfce.Aplicar(
                assinado, _registry.ObterUrlsNfce(emit.Uf, ambiente),
                req.Configuracao.Csc, req.Configuracao.IdCscEfetivo(), req.Configuracao.VersaoQrCode, certificado);
        }
        catch (Exception ex) when (ex is ArgumentException or SefazEndpointException)
        {
            return Falha(ex.Message);
        }

        // Rejeitada antes: guarda a antiga no historico e segue com a nova.
        if (existente is not null) _fila.Arquivar(existente);

        var dados = QrCodeNfce.Ler(xml);
        var item = new ItemFila
        {
            Chave = gerada.ChaveAcesso,
            EmpresaId = empresaId,
            Cnpj = cnpj,
            Ambiente = (int)ambiente,
            Serie = req.Serie,
            Numero = req.Numero,
            ValorTotal = dados.VNF,
            DataEmissao = req.DataEmissao,
            PrazoTransmissao = req.DataEmissao.AddHours(24),
            Xml = xml,
            QrCode = qr,
            UrlChave = urlChave,
            Situacao = SituacaoFila.Pendente
        };
        _fila.Salvar(item);
        _log.LogInformation("[EmissorLocal] NFC-e {Numero}/{Serie} emitida em contingencia: {Chave}.",
            item.Numero, item.Serie, item.Chave);
        return new ResultadoEmissaoLocal(item, []);
    }

    private static ResultadoEmissaoLocal Falha(string motivo) => new(null, [motivo]);

    private static Empresa MapearEmitente(Guid empresaId, EmitenteNFeRequest r) => new()
    {
        Id = empresaId,
        Cnpj = SoDigitos(r.Cnpj),
        RazaoSocial = r.RazaoSocial.Trim(),
        NomeFantasia = r.NomeFantasia?.Trim(),
        InscricaoEstadual = r.InscricaoEstadual.Trim(),
        InscricaoMunicipal = r.InscricaoMunicipal?.Trim(),
        CnaeFiscal = r.CnaeFiscal?.Trim(),
        RegimeTributario = r.RegimeTributario!.Value,
        Logradouro = r.Logradouro.Trim(),
        Numero = r.Numero.Trim(),
        Complemento = r.Complemento?.Trim(),
        Bairro = r.Bairro.Trim(),
        CodigoMunicipio = SoDigitos(r.CodigoMunicipio),
        NomeMunicipio = r.NomeMunicipio.Trim(),
        Uf = r.Uf.Trim().ToUpperInvariant(),
        Cep = SoDigitos(r.Cep),
        CodigoPais = string.IsNullOrWhiteSpace(r.CodigoPais) ? "1058" : r.CodigoPais.Trim(),
        NomePais = string.IsNullOrWhiteSpace(r.NomePais) ? "BRASIL" : r.NomePais.Trim(),
        Telefone = r.Telefone?.Trim()
    };

    private static string SoDigitos(string? v) => new((v ?? "").Where(char.IsDigit).ToArray());
}
