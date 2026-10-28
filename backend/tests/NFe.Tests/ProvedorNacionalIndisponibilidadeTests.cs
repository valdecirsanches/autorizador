using System.Net;
using NFe.Nfse.Nacional;
using Xunit;

namespace NFe.Tests;

public class ProvedorNacionalIndisponibilidadeTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void Status_transitorio_e_indisponibilidade(HttpStatusCode status)
        => Assert.True(ProvedorNacional.EhIndisponibilidade(status));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.OK)]
    public void Rejeicao_de_negocio_nao_e_indisponibilidade(HttpStatusCode status)
        => Assert.False(ProvedorNacional.EhIndisponibilidade(status));
}
