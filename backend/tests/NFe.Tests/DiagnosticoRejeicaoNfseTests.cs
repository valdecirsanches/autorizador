using NFe.Infrastructure.ServicosNfse;
using Xunit;

namespace NFe.Tests;

public class DiagnosticoRejeicaoNfseTests
{
    [Fact]
    public void E0314_ComCTribMun_InformaCodigoEnviado()
    {
        var d = ServicoNfse.DiagnosticoRejeicao("E0314", "x", "<DPS><cServ><cTribMun>101</cTribMun></cServ></DPS>");
        Assert.Contains("cTribMun=101", d);
    }

    [Fact]
    public void E0314_SemCTribMun_InformaAusencia()
    {
        var d = ServicoNfse.DiagnosticoRejeicao("E0314", "x", "<DPS><cServ><cTribNac>010101</cTribNac></cServ></DPS>");
        Assert.Contains("NAO enviou cTribMun", d);
    }

    [Fact]
    public void OutrosCodigos_SemDiagnostico()
        => Assert.Null(ServicoNfse.DiagnosticoRejeicao("E0001", "x", "<DPS/>"));
}
