using NFe.Sefaz.Xml;

namespace NFe.Tests;

public class ValidadorXsdTests : IDisposable
{
    private readonly string _dir;

    public ValidadorXsdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nfe-xsd-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // XSD minimo que exige um elemento <nota> com atributo versao="4.00" e um
    // filho <valor> decimal. Suficiente para exercitar a mecanica de validacao
    // sem depender do pacote oficial de schemas.
    private const string XsdSimples =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="nota">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="valor" type="xs:decimal"/>
              </xs:sequence>
              <xs:attribute name="versao" type="xs:string" use="required"/>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private ValidadorXsd ComSchema()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "nota.xsd"), XsdSimples);
        return new ValidadorXsd(_dir);
    }

    [Fact]
    public void SchemasDisponiveis_FalseQuandoDiretorioNaoExiste()
    {
        var validador = new ValidadorXsd(_dir);
        Assert.False(validador.SchemasDisponiveis);
    }

    [Fact]
    public void Validar_SemDiretorioDeSchemas_LancaXsdIndisponivel()
    {
        var validador = new ValidadorXsd(_dir);

        var ex = Assert.Throws<XsdIndisponivelException>(
            () => validador.Validar("<nota versao=\"4.00\"><valor>10.00</valor></nota>", "nota.xsd"));

        // A mensagem precisa orientar o operador a baixar o pacote oficial.
        Assert.Contains("PL_009", ex.Message);
    }

    [Fact]
    public void Validar_SchemaInexistenteNoDiretorio_LancaXsdIndisponivel()
    {
        var validador = ComSchema();

        Assert.Throws<XsdIndisponivelException>(
            () => validador.Validar("<nota versao=\"4.00\"><valor>1</valor></nota>", "inexistente.xsd"));
    }

    [Fact]
    public void Validar_DocumentoConforme_NaoLanca()
    {
        var validador = ComSchema();

        // Nao deve lancar.
        validador.Validar("<nota versao=\"4.00\"><valor>10.50</valor></nota>", "nota.xsd");
    }

    [Fact]
    public void Validar_AtributoObrigatorioAusente_LancaValidacao()
    {
        var validador = ComSchema();

        var ex = Assert.Throws<ValidacaoXsdException>(
            () => validador.Validar("<nota><valor>10.50</valor></nota>", "nota.xsd"));

        Assert.NotEmpty(ex.Erros);
    }

    [Fact]
    public void Validar_TipoDeDadoInvalido_LancaValidacao()
    {
        var validador = ComSchema();

        // "abc" nao e decimal valido.
        var ex = Assert.Throws<ValidacaoXsdException>(
            () => validador.Validar("<nota versao=\"4.00\"><valor>abc</valor></nota>", "nota.xsd"));

        Assert.NotEmpty(ex.Erros);
    }

    [Fact]
    public void Validar_XmlMalformado_ReportaErro()
    {
        var validador = ComSchema();

        var ex = Assert.Throws<ValidacaoXsdException>(
            () => validador.Validar("<nota versao=\"4.00\"><valor>10</nota>", "nota.xsd"));

        Assert.Contains(ex.Erros, e => e.Contains("malformado"));
    }

    [Fact]
    public void Conferir_DocumentoValido_RetornaListaVazia()
    {
        var validador = ComSchema();

        var erros = validador.Conferir("<nota versao=\"4.00\"><valor>10.50</valor></nota>", "nota.xsd");

        Assert.Empty(erros);
    }

    [Fact]
    public void Conferir_DocumentoInvalido_RetornaErrosSemLancar()
    {
        var validador = ComSchema();

        var erros = validador.Conferir("<nota><valor>10.50</valor></nota>", "nota.xsd");

        Assert.NotEmpty(erros);
    }
}
