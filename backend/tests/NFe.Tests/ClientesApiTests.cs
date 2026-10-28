using Microsoft.AspNetCore.Routing;
using NFe.Api.ClientesApi;

namespace NFe.Tests;

public class ClientesApiTests
{
    private static readonly Guid EmpresaA = Guid.Parse("a19c9920-6951-4008-bbbd-acd805838094");
    private static readonly Guid EmpresaB = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    private static RegraAcesso Classificar(
        string metodo, string caminho, object? rotas = null, Dictionary<string, string?>? query = null) =>
        PoliticaAcessoApi.Classificar(
            metodo, caminho, new RouteValueDictionary(rotas),
            query ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    // ------------------------------------------------------------- ChaveApi

    [Fact]
    public void Gerar_ProduzChaveComPrefixoIdentificadorEHash()
    {
        var (chave, identificador, hash) = ChaveApi.Gerar();

        Assert.StartsWith("nfeapp_", chave);
        Assert.Equal(7 + 43, chave.Length);
        Assert.Equal(chave[..13], identificador);
        Assert.Equal(64, hash.Length);
        Assert.Equal(ChaveApi.Hash(chave), hash);
        Assert.True(ChaveApi.TemFormatoDeChave(chave));
        Assert.DoesNotContain('+', chave);
        Assert.DoesNotContain('/', chave);
    }

    [Fact]
    public void Gerar_NuncaRepete()
    {
        var chaves = Enumerable.Range(0, 200).Select(_ => ChaveApi.Gerar().Chave).ToHashSet();
        Assert.Equal(200, chaves.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token-mestre-qualquer")]
    [InlineData("nfeapp_curta")]
    public void TemFormatoDeChave_RecusaValoresQueNaoSaoChaveDeApp(string? valor) =>
        Assert.False(ChaveApi.TemFormatoDeChave(valor));

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer   abc  ", "abc")]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer ", null)]
    [InlineData("", null)]
    public void ExtrairBearer(string header, string? esperado) =>
        Assert.Equal(esperado, AutenticacaoApi.ExtrairBearer(header));

    // ------------------------------------------------------------- Chamador

    [Fact]
    public void Chamador_AppSoAcessaEmpresasLiberadas()
    {
        var app = Chamador.DeApp(Cliente(podeCadastrar: false, EmpresaA));

        Assert.False(app.EhMestre);
        Assert.True(app.PodeAcessar(EmpresaA));
        Assert.False(app.PodeAcessar(EmpresaB));
        Assert.False(app.PodeCadastrarEmpresas);
        Assert.Equal("App: ERP Teste", app.NomeAuditoria);
    }

    [Fact]
    public void Chamador_MestreAcessaTudo()
    {
        Assert.True(Chamador.Mestre.PodeAcessar(EmpresaB));
        Assert.True(Chamador.Mestre.PodeCadastrarEmpresas);
        Assert.Null(Chamador.Mestre.NomeAuditoria);
    }

    [Fact]
    public void Chamador_SemRegistroNoContextoEhMestre()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.Null(Chamador.Registrado(ctx));
        Assert.True(Chamador.De(ctx).EhMestre);

        Chamador.DeApp(Cliente(false, EmpresaA)).Gravar(ctx);
        Assert.False(Chamador.De(ctx).EhMestre);
    }

    // ------------------------------------------------------ Politica: mestre

    [Theory]
    [InlineData("GET", "/api/clientes")]
    [InlineData("POST", "/api/clientes")]
    [InlineData("POST", "/api/clientes/0f0e0d0c-0b0a-0908-0706-050403020100/nova-chave")]
    [InlineData("GET", "/api/monitor/logs")]
    [InlineData("GET", "/diag/db")]
    [InlineData("GET", "/api/certificados")]
    [InlineData("POST", "/api/certificados/")]
    [InlineData("PUT", "/api/empresas/a19c9920-6951-4008-bbbd-acd805838094/pasta-xml")]
    [InlineData("GET", "/api/empresas/a19c9920-6951-4008-bbbd-acd805838094/pasta-xml/diagnostico")]
    [InlineData("DELETE", "/api/empresas/a19c9920-6951-4008-bbbd-acd805838094")]
    public void Politica_RotasAdministrativasSaoSoDoMestre(string metodo, string caminho) =>
        Assert.Equal(TipoRegra.SomenteMestre, Classificar(metodo, caminho, new { id = EmpresaA.ToString() }).Tipo);

    [Fact]
    public void Politica_QuemSouEuEhLivre() =>
        Assert.Equal(TipoRegra.Livre, Classificar("GET", "/api/clientes/eu").Tipo);

    [Theory]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/api/nfse/municipios")]
    [InlineData("GET", "/api/nfse/motivos-cancelamento")]
    [InlineData("GET", "/openapi/v1.json")]
    [InlineData("GET", "/swagger/v1/swagger.json")]
    public void Politica_RotasSemEmpresaSaoLivres(string metodo, string caminho) =>
        Assert.Equal(TipoRegra.Livre, Classificar(metodo, caminho).Tipo);

    [Fact]
    public void Politica_ListaDeEmpresasEhFiltrada() =>
        Assert.Equal(TipoRegra.ListaEmpresas, Classificar("GET", "/api/empresas/").Tipo);

    [Theory]
    [InlineData("/api/empresas")]
    [InlineData("/api/empresas/onboarding")]
    public void Politica_CadastroDeEmpresa(string caminho) =>
        Assert.Equal(TipoRegra.CadastroEmpresa, Classificar("POST", caminho).Tipo);

    // ---------------------------------------------- Politica: por recurso

    [Fact]
    public void Politica_EmissaoExigeAEmpresaDaRota()
    {
        var regra = Classificar("POST", $"/api/empresas/{EmpresaA}/nfe", new { empresaId = EmpresaA.ToString() });

        Assert.Equal(TipoRegra.Restrita, regra.Tipo);
        Assert.Equal(new[] { new Requisito(TipoRecurso.Empresa, EmpresaA.ToString()) }, regra.Requisitos);
    }

    [Theory]
    [InlineData("GET", "/api/empresas/{0}", "id")]
    [InlineData("PUT", "/api/empresas/{0}/ambiente", "id")]
    [InlineData("GET", "/api/empresas/{0}/series", "id")]
    [InlineData("POST", "/api/empresas/{0}/inutilizacoes", "id")]
    [InlineData("POST", "/api/empresas/{0}/logo", "empresa")]
    [InlineData("POST", "/api/status/empresa/{0}/consultar", "empresaId")]
    [InlineData("POST", "/api/certificados/empresa/{0}", "empresaId")]
    public void Politica_RotasDeEmpresaExigemAEmpresa(string metodo, string modelo, string nomeRota)
    {
        var rotas = new RouteValueDictionary { [nomeRota] = EmpresaA.ToString() };
        var regra = PoliticaAcessoApi.Classificar(
            metodo, string.Format(modelo, EmpresaA), rotas, new Dictionary<string, string?>());

        Assert.Equal(TipoRegra.Restrita, regra.Tipo);
        Assert.Contains(new Requisito(TipoRecurso.Empresa, EmpresaA.ToString()), regra.Requisitos);
    }

    [Fact]
    public void Politica_PorCnpjExigeAEmpresaDoCnpj()
    {
        var regra = Classificar("GET", "/api/empresas/por-cnpj/67570036000181", new { cnpj = "67570036000181" });
        Assert.Equal(new[] { new Requisito(TipoRecurso.Empresa, "67570036000181") }, regra.Requisitos);
    }

    [Fact]
    public void Politica_CancelamentoNfseConfereEmpresaENota()
    {
        var nota = Guid.NewGuid().ToString();
        var regra = Classificar("POST", $"/api/empresas/{EmpresaA}/nfse/{nota}/cancelar",
            new { empresaId = EmpresaA.ToString(), id = nota });

        Assert.Equal(TipoRegra.Restrita, regra.Tipo);
        Assert.Contains(new Requisito(TipoRecurso.Empresa, EmpresaA.ToString()), regra.Requisitos);
        Assert.Contains(new Requisito(TipoRecurso.NotaServico, nota), regra.Requisitos);
    }

    [Theory]
    [InlineData("/api/nfe/{0}", TipoRecurso.DocumentoNfe)]
    [InlineData("/api/nfe/{0}/danfe.pdf", TipoRecurso.DocumentoNfe)]
    [InlineData("/api/nfe/{0}/cancelar", TipoRecurso.DocumentoNfe)]
    [InlineData("/api/nfse/{0}/xml", TipoRecurso.NotaServico)]
    [InlineData("/api/tentativas/nfe/{0}/json-recebido", TipoRecurso.TentativaNfe)]
    [InlineData("/api/tentativas/nfse/{0}/json-recebido", TipoRecurso.TentativaNfse)]
    public void Politica_RotasDeDocumentoConferemODono(string modelo, TipoRecurso tipo)
    {
        var regra = Classificar("GET", string.Format(modelo, "123"), new { id = "123" });
        Assert.Equal(new[] { new Requisito(tipo, "123") }, regra.Requisitos);
    }

    [Fact]
    public void Politica_AgenteA3ConfereOCertificado()
    {
        var cert = Guid.NewGuid().ToString();
        var regra = Classificar("GET", $"/api/certificados/{cert}/agente", new { certificadoId = cert });
        Assert.Equal(new[] { new Requisito(TipoRecurso.Certificado, cert) }, regra.Requisitos);
    }

    [Fact]
    public void Politica_TentativasNfseExigemEmpresaNaQuery()
    {
        Assert.Equal(TipoRegra.Negada, Classificar("GET", "/api/nfse/tentativas").Tipo);

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["empresaid"] = EmpresaA.ToString() };
        var regra = Classificar("GET", "/api/nfse/tentativas", query: query);
        Assert.Equal(new[] { new Requisito(TipoRecurso.Empresa, EmpresaA.ToString()) }, regra.Requisitos);
    }

    [Theory]
    [InlineData("GET", "/api/rota-nova-que-ninguem-classificou")]
    [InlineData("POST", "/api/nfse/municipios")]
    [InlineData("GET", "/")]
    public void Politica_RotaDesconhecidaEhNegada(string metodo, string caminho) =>
        Assert.Equal(TipoRegra.Negada, Classificar(metodo, caminho).Tipo);

    private static ClienteApi Cliente(bool podeCadastrar, params Guid[] empresas) => new(
        Guid.NewGuid(), "ERP Teste", "nfeapp_abcdef", podeCadastrar, true,
        empresas.ToHashSet(), DateTime.UtcNow, null, null);
}
