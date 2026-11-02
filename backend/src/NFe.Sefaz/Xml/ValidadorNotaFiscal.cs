using NFe.Domain;
using NFe.Domain.Modelos;

namespace NFe.Sefaz.Xml;

/// <summary>
/// Validacao previa do pedido de emissao (NF-e e NFC-e), sem banco: devolve a
/// lista do que corrigir. Usada pela API e pelo emissor local da NFC-e.
/// </summary>
public static class ValidadorNotaFiscal
{
    /// <summary>
    /// Ajustes antes de validar/gerar. NFC-e: consumidor ausente vira "sem
    /// consumidor", CSC enviado na raiz do JSON passa para a configuracao e a
    /// contingencia off-line define tpEmis = 9.
    /// </summary>
    public static void Normalizar(NotaFiscalRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Modelo != ModeloDocumento.NFCe) return;

        req.Destinatario ??= new DestinatarioRequest();
        if (req.Configuracao is { } cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.Csc) && !string.IsNullOrWhiteSpace(req.Csc))
                cfg.Csc = req.Csc;
            if (string.IsNullOrWhiteSpace(cfg.IdCsc) && string.IsNullOrWhiteSpace(cfg.CscId)
                && !string.IsNullOrWhiteSpace(req.IdCsc))
                cfg.IdCsc = req.IdCsc;
            if (cfg.ContingenciaOffline)
                req.TipoEmissao = TipoEmissao.ContingenciaOffline;
        }
    }

    public static List<string> Validar(NotaFiscalRequest req)
    {
        var erros = new List<string>();
        var nfce = req.Modelo == ModeloDocumento.NFCe;

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

        if (nfce)
        {
            // NFC-e: consumidor opcional; se identificado, CPF (11) ou CNPJ (14).
            var doc = SomenteDigitos(req.Destinatario?.Documento);
            if (doc.Length > 0 && doc.Length is not (11 or 14))
                erros.Add("NFC-e: o documento do consumidor deve ser CPF (11 digitos) ou CNPJ (14 digitos).");
            if (!req.ConsumidorFinal)
                erros.Add("NFC-e: consumidorFinal deve ser true (venda a consumidor final).");
            if (req.Configuracao is { } c)
            {
                var offline = req.TipoEmissao == TipoEmissao.ContingenciaOffline;
                if (c.VersaoQrCode is not null and not (2 or 3))
                    erros.Add("NFC-e: versaoQrCode deve ser 2 ou 3.");
                else if (Nfce.QrCodeNfce.VersaoEfetiva(c.VersaoQrCode, offline, !string.IsNullOrWhiteSpace(c.Csc)) == 2
                         && (string.IsNullOrWhiteSpace(c.Csc) || c.IdCscEfetivo() is null))
                    erros.Add("NFC-e: o QR Code versao 2 exige configuracao.csc e configuracao.idCsc " +
                              "(ou use versaoQrCode 3, que dispensa o CSC).");
                if (req.TipoEmissao is not (TipoEmissao.Normal or TipoEmissao.ContingenciaOffline))
                    erros.Add("NFC-e: tipoEmissao deve ser 1 (normal) ou 9 (contingencia off-line).");
                if (!string.IsNullOrWhiteSpace(c.JustificativaContingencia)
                    && c.JustificativaContingencia.Trim().Length is < 15 or > 256)
                    erros.Add("justificativaContingencia deve ter de 15 a 256 caracteres.");
            }
        }
        else if (req.Destinatario is null)
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

        if (!nfce && req.Configuracao?.ContingenciaOffline == true)
            erros.Add("contingenciaOffline e so da NFC-e (modelo 65).");

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

        erros.AddRange(GeradorXmlNFe.ValidarCobranca(req));
        return erros;
    }

    /// <summary>
    /// CST de PIS/COFINS da tabela oficial: 01-09, 49, 50-56, 60-67, 70-75, 98 e 99.
    /// Devolve o problema (para a mensagem) ou null quando o CST e aceito.
    /// </summary>
    public static string? ProblemaCstPisCofins(string? cst)
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

    private static string SomenteDigitos(string? valor) =>
        new((valor ?? string.Empty).Where(char.IsDigit).ToArray());
}
