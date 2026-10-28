using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;

namespace NFe.Sefaz.Xml;

/// <summary>Erro estrutural quando os schemas oficiais nao estao disponiveis.</summary>
public sealed class XsdIndisponivelException : Exception
{
    public XsdIndisponivelException(string message) : base(message) { }
}

/// <summary>Falha de validacao de um documento contra o XSD.</summary>
public sealed class ValidacaoXsdException : Exception
{
    public IReadOnlyList<string> Erros { get; }

    public ValidacaoXsdException(IReadOnlyList<string> erros)
        : base("O documento nao e valido segundo o XSD: " + string.Join(" | ", erros))
    {
        Erros = erros;
    }
}

/// <summary>
/// Valida documentos da NF-e contra os schemas oficiais (pacote PL_009_V4 ou
/// posterior), lidos de um diretorio configuravel.
///
/// Decisao deliberada: se o diretorio ou o schema pedido nao existir, a
/// validacao FALHA de forma explicita (XsdIndisponivelException) em vez de
/// passar silenciosamente. Um autorizador que "valida" sem schema daria uma
/// falsa sensacao de seguranca e so revelaria o erro como rejeicao na SEFAZ.
/// </summary>
public sealed class ValidadorXsd
{
    private readonly string _diretorioSchemas;
    private readonly ConcurrentDictionary<string, XmlSchemaSet> _cache = new();

    /// <param name="diretorioSchemas">
    /// Caminho para a pasta com os .xsd oficiais. Normalmente vem de
    /// configuracao (ex.: NFE_SCHEMAS_DIR).
    /// </param>
    public ValidadorXsd(string diretorioSchemas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diretorioSchemas);
        _diretorioSchemas = diretorioSchemas;
    }

    /// <summary>Indica se o diretorio de schemas existe e contem ao menos um XSD.</summary>
    public bool SchemasDisponiveis =>
        Directory.Exists(_diretorioSchemas) &&
        Directory.EnumerateFiles(_diretorioSchemas, "*.xsd").Any();

    /// <summary>
    /// Valida o XML contra o schema informado. Lanca XsdIndisponivelException se
    /// o schema nao existir e ValidacaoXsdException se o documento for invalido.
    /// </summary>
    /// <param name="xml">Documento a validar.</param>
    /// <param name="nomeArquivoXsd">Nome do XSD raiz, ex.: "nfe_v4.00.xsd".</param>
    public void Validar(string xml, string nomeArquivoXsd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeArquivoXsd);

        var schemaSet = ObterSchemaSet(nomeArquivoXsd);

        var erros = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemaSet
        };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) =>
        {
            // Warnings de schema faltante sao promovidos a erro: indicam import
            // nao resolvido, o que tornaria a validacao incompleta.
            var prefixo = e.Severity == XmlSeverityType.Warning ? "AVISO" : "ERRO";
            var linha = e.Exception is not null
                ? $" (linha {e.Exception.LineNumber}, col {e.Exception.LinePosition})"
                : string.Empty;
            erros.Add($"{prefixo}: {e.Message}{linha}");
        };

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        try
        {
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            erros.Add($"XML malformado: {ex.Message} (linha {ex.LineNumber}, col {ex.LinePosition})");
        }

        if (erros.Count > 0)
            throw new ValidacaoXsdException(erros);
    }

    /// <summary>
    /// Versao que nao lanca: retorna a lista de erros (vazia quando valido).
    /// Util para o painel exibir todos os problemas de uma vez.
    /// </summary>
    public IReadOnlyList<string> Conferir(string xml, string nomeArquivoXsd)
    {
        try
        {
            Validar(xml, nomeArquivoXsd);
            return [];
        }
        catch (ValidacaoXsdException ex)
        {
            return ex.Erros;
        }
    }

    private XmlSchemaSet ObterSchemaSet(string nomeArquivoXsd)
    {
        return _cache.GetOrAdd(nomeArquivoXsd, nome =>
        {
            if (!Directory.Exists(_diretorioSchemas))
                throw new XsdIndisponivelException(
                    $"Diretorio de schemas nao encontrado: '{_diretorioSchemas}'. " +
                    "Baixe o pacote oficial (PL_009_V4) do Portal Nacional e configure o caminho.");

            var caminho = Path.Combine(_diretorioSchemas, nome);
            if (!File.Exists(caminho))
                throw new XsdIndisponivelException(
                    $"Schema '{nome}' nao encontrado em '{_diretorioSchemas}'.");

            var set = new XmlSchemaSet
            {
                // Resolve imports/includes relativos ao proprio diretorio.
                XmlResolver = new XmlUrlResolver()
            };

            using var reader = XmlReader.Create(caminho);
            set.Add(null, reader);
            set.Compile();
            return set;
        });
    }
}
