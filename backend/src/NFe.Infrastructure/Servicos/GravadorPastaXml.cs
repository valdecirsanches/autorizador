using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;

namespace NFe.Infrastructure.Servicos;

/// <summary>Resultado da gravacao de uma nota na pasta configurada da empresa.</summary>
public enum ResultadoGravacaoPasta
{
    Gravado,
    JaExistia,
    /// <summary>Sem pasta configurada, homologacao, nao autorizada ou sem XML.</summary>
    Ignorado,
    Erro
}

/// <summary>
/// Grava o XML autorizado (nfeProc) das notas emitidas pela API na "Pasta de
/// XMLs" configurada na empresa (Empresa.PastaXmlBackup), na mesma estrutura do
/// backup do emissor (EDS Monitor): {pasta}/{periodo}/NFe/{chave}-nfe.xml.
/// So producao: nota de homologacao nao tem valor fiscal e nao vai para o backup.
/// Nunca sobrescreve um arquivo existente e nunca derruba a emissao: falhas sao
/// registradas no log.
/// </summary>
public static class GravadorPastaXml
{
    public static ResultadoGravacaoPasta Gravar(
        Empresa empresa, DocumentoFiscal doc, ILogger? log = null) =>
        Gravar(empresa.PastaXmlBackup, empresa.Cnpj, doc, log, out _);

    public static ResultadoGravacaoPasta Gravar(
        string? pasta, string cnpj, DocumentoFiscal doc, ILogger? log, out string? caminho)
    {
        caminho = null;
        if (string.IsNullOrWhiteSpace(pasta)
            || doc.Ambiente != TipoAmbiente.Producao
            || doc.Status is not (StatusDocumento.Autorizado or StatusDocumento.Cancelado)
            || string.IsNullOrWhiteSpace(doc.XmlProtocolado)
            || doc.ChaveAcesso is not { Length: 44 } chave)
            return ResultadoGravacaoPasta.Ignorado;

        try
        {
            // O backup organiza por periodo de autorizacao (cai no mes seguinte
            // quando a nota e autorizada depois da virada).
            var data = (doc.DataAutorizacao ?? doc.DataEmissao).ToLocalTime();
            var dir = LocalizadorXmlPasta.PastaParaGravar(pasta, cnpj, data);
            if (dir is null)
            {
                log?.LogWarning("[XML] Pasta configurada invalida para gravar {Chave}: {Pasta}", chave, pasta);
                return ResultadoGravacaoPasta.Erro;
            }

            caminho = Path.Combine(dir, chave + "-nfe.xml");
            if (File.Exists(caminho)) return ResultadoGravacaoPasta.JaExistia;

            Directory.CreateDirectory(dir);
            var tmp = caminho + ".tmp";
            File.WriteAllText(tmp, ComDeclaracao(doc.XmlProtocolado!), new System.Text.UTF8Encoding(false));
            File.Move(tmp, caminho, overwrite: false);
            log?.LogInformation("[XML] {Chave} gravado na pasta configurada: {Caminho}", chave, caminho);
            return ResultadoGravacaoPasta.Gravado;
        }
        catch (IOException) when (caminho is not null && File.Exists(caminho))
        {
            return ResultadoGravacaoPasta.JaExistia;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log?.LogWarning(ex, "[XML] Nao foi possivel gravar {Chave} na pasta configurada {Pasta}: {Erro}",
                chave, pasta, ex.Message);
            return ResultadoGravacaoPasta.Erro;
        }
    }

    private static string ComDeclaracao(string xml) =>
        xml.TrimStart().StartsWith("<?xml", StringComparison.Ordinal)
            ? xml
            : "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml;
}
