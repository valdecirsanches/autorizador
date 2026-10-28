using System.Text;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Espelha em disco os XMLs gerados na emissao, organizados em UMA PASTA POR
/// EMPRESA (identificada pelo CNPJ). O banco continua sendo a fonte da verdade
/// (colunas XmlAssinado / XmlProtocolado); este arquivador e um espelho em
/// arquivo para backup, auditoria e exportacao/entrega ao contador.
///
/// Estrutura de pastas:
///
///   {base}/
///     {cnpj}/                        &lt;- uma pasta por empresa
///       {ambiente}/                  producao | homologacao
///         {chaveAcesso}-nfe.xml      XML assinado (sempre que gerado)
///         {chaveAcesso}-procNFe.xml  XML autorizado (nfeProc, quando houver)
///         {chaveAcesso}-ret.xml      Retorno bruto da SEFAZ (SEMPRE que houver
///                                    resposta: autorizacao, rejeicao ou erro)
///
/// A base e configuravel por NFE_XML_DIR; o padrao e a pasta "xmls" na raiz de
/// execucao da API. Falhas de I/O NUNCA derrubam a emissao — a nota ja pode
/// estar autorizada na SEFAZ; apenas registramos um aviso no log.
/// </summary>
public sealed class ArquivadorXml
{
    private readonly string _baseDir;
    private readonly ILogger<ArquivadorXml>? _log;

    public ArquivadorXml(string baseDir, ILogger<ArquivadorXml>? log = null)
    {
        _baseDir = string.IsNullOrWhiteSpace(baseDir)
            ? Path.Combine(Directory.GetCurrentDirectory(), "xmls")
            : baseDir;
        _log = log;
    }

    /// <summary>Diretorio base onde as pastas por empresa sao criadas.</summary>
    public string BaseDir => _baseDir;

    /// <summary>Le a base do ambiente (NFE_XML_DIR) ou usa "xmls" na raiz.</summary>
    public static ArquivadorXml DoAmbiente(ILogger<ArquivadorXml>? log = null)
    {
        var dir = Environment.GetEnvironmentVariable("NFE_XML_DIR");
        return new ArquivadorXml(
            string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(Directory.GetCurrentDirectory(), "xmls")
                : dir,
            log);
    }

    /// <summary>Caminho absoluto da pasta de uma empresa (criada sob demanda).</summary>
    public string PastaDaEmpresa(Empresa empresa)
        => Path.Combine(_baseDir, SanitizarCnpj(empresa.Cnpj));

    /// <summary>
    /// Grava o XML ASSINADO da nota (arquivo {chave}-nfe.xml). Chamado logo apos
    /// a assinatura, antes mesmo da transmissao, para nunca perdermos o que foi
    /// efetivamente enviado a SEFAZ.
    /// </summary>
    public void SalvarAssinado(Empresa empresa, DocumentoFiscal doc)
        => Salvar(empresa, doc, doc.XmlAssinado, "nfe");

    /// <summary>
    /// Grava o XML AUTORIZADO (nfeProc, arquivo {chave}-procNFe.xml). Chamado
    /// apos o retorno da SEFAZ, quando ha protocolo de autorizacao.
    /// </summary>
    public void SalvarAutorizado(Empresa empresa, DocumentoFiscal doc)
        => Salvar(empresa, doc, doc.XmlProtocolado, "procNFe");

    /// <summary>
    /// Grava o RETORNO BRUTO da SEFAZ (arquivo {chave}-ret.xml) — o envelope
    /// recebido, seja autorizacao, rejeicao ou erro de transmissao. E o XML que
    /// permite "avaliar o que a SEFAZ respondeu" mesmo quando a nota nao foi
    /// autorizada. Salvo em TODOS os desfechos, nao so no sucesso.
    /// </summary>
    public void SalvarRetornoSefaz(Empresa empresa, DocumentoFiscal doc)
        => Salvar(empresa, doc, doc.XmlRetornoSefaz, "ret");

    private void Salvar(Empresa empresa, DocumentoFiscal doc, string? xml, string sufixo)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return;

        try
        {
            var ambiente = doc.Ambiente == TipoAmbiente.Producao ? "producao" : "homologacao";
            var pasta = Path.Combine(PastaDaEmpresa(empresa), ambiente);
            Directory.CreateDirectory(pasta);

            // Nome base: a chave de acesso quando existe; senao o Id do documento.
            // Assim um erro que ocorreu antes de haver chave ainda arquiva o retorno
            // (ex.: rejeicao de lote sem protocolo, SOAP fault) para diagnostico.
            var nomeBase = string.IsNullOrWhiteSpace(doc.ChaveAcesso)
                ? doc.Id.ToString()
                : doc.ChaveAcesso;

            var caminho = Path.Combine(pasta, $"{nomeBase}-{sufixo}.xml");
            // Sobrescreve deliberadamente: um reenvio que finalmente autoriza deve
            // atualizar o arquivo com a versao definitiva.
            File.WriteAllText(caminho, xml, new UTF8Encoding(false));

            _log?.LogInformation(
                "[XML] Documento {Chave} ({Sufixo}) salvo em {Caminho}",
                nomeBase, sufixo, caminho);
        }
        catch (Exception ex)
        {
            // Nunca propaga: a emissao/autorizacao ja aconteceu; o disco e espelho.
            _log?.LogWarning(ex,
                "[XML] Falha ao salvar o XML {Sufixo} do documento {Chave} em disco.",
                sufixo, doc.ChaveAcesso);
        }
    }

    /// <summary>
    /// Reduz o CNPJ a 14 digitos para nome de pasta. Se vier vazio/invalido,
    /// usamos "sem-cnpj" para nao criar caminhos estranhos.
    /// </summary>
    private static string SanitizarCnpj(string? cnpj)
    {
        var digitos = new string((cnpj ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digitos) ? "sem-cnpj" : digitos;
    }
}
