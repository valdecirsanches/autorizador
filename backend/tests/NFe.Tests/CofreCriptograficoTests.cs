using NFe.Infrastructure.Security;
using Xunit;

namespace NFe.Tests;

public class CofreCriptograficoTests
{
    private static CofreCriptografico Cofre() =>
        CofreCriptografico.DeBase64(CofreCriptografico.GerarChaveMestraBase64());

    [Fact]
    public void CifrarEDecifrar_PreservaConteudo()
    {
        var cofre = Cofre();
        const string ctx = "nfe:cert:teste";
        var original = "senha-do-pfx-!@#$%-com-acento-ção";

        var cifrado = cofre.CifrarTexto(original, ctx);
        var recuperado = cofre.DecifrarTexto(cifrado, ctx);

        Assert.Equal(original, recuperado);
    }

    [Fact]
    public void Cifrar_NaoVazaPlaintextNoCriptograma()
    {
        var cofre = Cofre();
        var segredo = "MinhaSenhaSecreta123";
        var cifrado = cofre.CifrarTexto(segredo, "ctx");

        var comoTexto = System.Text.Encoding.UTF8.GetString(cifrado.Conteudo);
        Assert.DoesNotContain("MinhaSenha", comoTexto);
    }

    [Fact]
    public void Cifrar_GeraNonceDiferenteACadaChamada()
    {
        // Reutilizar nonce com a mesma chave quebra a seguranca do AES-GCM.
        var cofre = Cofre();
        var a = cofre.CifrarTexto("mesmo-texto", "ctx");
        var b = cofre.CifrarTexto("mesmo-texto", "ctx");

        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Conteudo, b.Conteudo); // criptograma tambem difere
    }

    [Fact]
    public void Decifrar_ComContextoDiferente_Falha()
    {
        // Garante que um blob de uma empresa nao seja utilizavel em outra.
        var cofre = Cofre();
        var empresaA = CofreCriptografico.ContextoCertificado(Guid.NewGuid(), "pfx");
        var empresaB = CofreCriptografico.ContextoCertificado(Guid.NewGuid(), "pfx");

        var cifrado = cofre.CifrarTexto("conteudo", empresaA);

        Assert.Throws<CofreException>(() => cofre.DecifrarTexto(cifrado, empresaB));
    }

    [Fact]
    public void Decifrar_ComChaveDiferente_Falha()
    {
        var cofreA = Cofre();
        var cofreB = Cofre();
        var cifrado = cofreA.CifrarTexto("conteudo", "ctx");

        Assert.Throws<CofreException>(() => cofreB.DecifrarTexto(cifrado, "ctx"));
    }

    [Fact]
    public void Decifrar_ComCriptogramaAlterado_Falha()
    {
        // Detecta manipulacao: e o motivo de usar GCM em vez de CBC.
        var cofre = Cofre();
        var cifrado = cofre.CifrarTexto("conteudo-importante", "ctx");

        cifrado.Conteudo[0] ^= 0xFF;

        Assert.Throws<CofreException>(() => cofre.DecifrarTexto(cifrado, "ctx"));
    }

    [Fact]
    public void Decifrar_ComTagAlterada_Falha()
    {
        var cofre = Cofre();
        var cifrado = cofre.CifrarTexto("conteudo", "ctx");

        cifrado.Tag[0] ^= 0xFF;

        Assert.Throws<CofreException>(() => cofre.DecifrarTexto(cifrado, "ctx"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeBase64_ComChaveAusente_Falha(string? chave)
    {
        var ex = Assert.Throws<CofreException>(() => CofreCriptografico.DeBase64(chave));
        Assert.Contains("NFE_CERT_MASTER_KEY", ex.Message);
    }

    [Fact]
    public void DeBase64_ComBase64Invalido_Falha()
    {
        var ex = Assert.Throws<CofreException>(() => CofreCriptografico.DeBase64("nao!!!base64"));
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void DeBase64_ComChaveCurta_Falha()
    {
        // Foi exatamente o caso da chave de 8 caracteres enviada inicialmente.
        var curta = Convert.ToBase64String(new byte[8]);
        var ex = Assert.Throws<CofreException>(() => CofreCriptografico.DeBase64(curta));
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void GerarChaveMestra_ProduzChaveDe32Bytes()
    {
        var chave = CofreCriptografico.GerarChaveMestraBase64();
        Assert.Equal(32, Convert.FromBase64String(chave).Length);
        CofreCriptografico.DeBase64(chave); // nao deve lancar
    }

    [Fact]
    public void CifrarBinarioGrande_FuncionaComoPfx()
    {
        // Simula o tamanho tipico de um arquivo A1.
        var cofre = Cofre();
        var pfxFalso = new byte[12 * 1024];
        Random.Shared.NextBytes(pfxFalso);

        var cifrado = cofre.Cifrar(pfxFalso, "ctx");
        var recuperado = cofre.Decifrar(cifrado, "ctx");

        Assert.Equal(pfxFalso, recuperado);
    }
}
