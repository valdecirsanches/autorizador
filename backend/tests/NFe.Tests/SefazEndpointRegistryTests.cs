using NFe.Domain;
using NFe.Sefaz.Endpoints;

namespace NFe.Tests;

/// <summary>
/// Valida a resolucao multi-UF: delegacoes de autorizador, contingencia e
/// codigos IBGE. Erros aqui gerariam rejeicao 236 (chave com cUF divergente)
/// ou envio ao autorizador errado.
/// </summary>
public class SefazEndpointRegistryTests
{
    private readonly SefazEndpointRegistry _reg = SefazEndpointRegistry.LoadDefault();

    [Fact]
    public void Catalogo_Carrega_ComVersao400()
    {
        Assert.Equal("4.00", _reg.VersaoLayout);
        Assert.NotEmpty(_reg.UfsSuportadas);
    }

    [Fact]
    public void TodasAs27Ufs_TemAutorizador()
    {
        // 26 estados + DF. Uma UF faltando significaria empresa impossibilitada de emitir.
        string[] ufs =
        [
            "AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG",
            "PA","PB","PR","PE","PI","RJ","RN","RS","RO","RR","SC","SP","SE","TO"
        ];

        Assert.Equal(27, ufs.Length);

        foreach (var uf in ufs)
        {
            var autorizador = _reg.ResolverAutorizador(uf);
            Assert.False(string.IsNullOrWhiteSpace(autorizador), $"UF {uf} sem autorizador.");
        }
    }

    [Theory]
    // UFs com autorizador proprio.
    [InlineData("SP", "SP")]
    [InlineData("MG", "MG")]
    [InlineData("PR", "PR")]
    [InlineData("RS", "RS")]
    [InlineData("BA", "BA")]
    [InlineData("GO", "GO")]
    [InlineData("MT", "MT")]
    [InlineData("MS", "MS")]
    [InlineData("PE", "PE")]
    [InlineData("AM", "AM")]
    // Delegadas ao SVRS. Conforme portal oficial, CE, PA e PI estao aqui,
    // e nao no SVAN nem com autorizador proprio.
    [InlineData("CE", "SVRS")]
    [InlineData("PA", "SVRS")]
    [InlineData("PI", "SVRS")]
    [InlineData("SC", "SVRS")]
    [InlineData("RJ", "SVRS")]
    [InlineData("DF", "SVRS")]
    [InlineData("ES", "SVRS")]
    [InlineData("AC", "SVRS")]
    [InlineData("RO", "SVRS")]
    [InlineData("TO", "SVRS")]
    // O SVAN atende exclusivamente o Maranhao.
    [InlineData("MA", "SVAN")]
    public void Delegacao_DeAutorizador_EstaCorreta(string uf, string esperado)
    {
        Assert.Equal(esperado, _reg.ResolverAutorizador(uf));
    }

    [Theory]
    [InlineData("RS", 43)]
    [InlineData("SP", 35)]
    [InlineData("MG", 31)]
    [InlineData("RJ", 33)]
    [InlineData("DF", 53)]
    [InlineData("AC", 12)]
    [InlineData("BA", 29)]
    [InlineData("SC", 42)]
    [InlineData("PR", 41)]
    [InlineData("AM", 13)]
    public void CodigoIbge_DaUf_EstaCorreto(string uf, int esperado)
    {
        // O cUF errado invalida a chave de acesso e gera rejeicao pela SEFAZ.
        Assert.Equal(esperado, _reg.ObterCodigoUf(uf));
    }

    /// <summary>
    /// Trava o mapeamento completo publicado em
    /// nfe.fazenda.gov.br/portal/webServices.aspx. Se a Receita alterar uma
    /// delegacao, este teste falha e sinaliza a atualizacao do catalogo, em vez
    /// de a divergencia so aparecer como rejeicao em producao.
    /// </summary>
    [Fact]
    public void MapeamentoCompleto_ConfereComOPortalOficial()
    {
        // SVAN atende exclusivamente MA.
        string[] svan = ["MA"];

        // SVRS atende as demais UFs sem autorizador proprio.
        string[] svrs =
        [
            "AC","AL","AP","CE","DF","ES","PA","PB","PI",
            "RJ","RN","RO","RR","SC","SE","TO"
        ];

        // UFs com autorizador proprio.
        string[] proprios = ["AM","BA","GO","MG","MS","MT","PE","PR","RS","SP"];

        Assert.Equal(27, svan.Length + svrs.Length + proprios.Length);

        foreach (var uf in svan)
            Assert.Equal("SVAN", _reg.ResolverAutorizador(uf));

        foreach (var uf in svrs)
            Assert.Equal("SVRS", _reg.ResolverAutorizador(uf));

        foreach (var uf in proprios)
            Assert.Equal(uf, _reg.ResolverAutorizador(uf));
    }

    [Fact]
    public void Contingencia_ConfereComOPortalOficial()
    {
        string[] svcAn =
        [
            "AC","AL","AP","CE","DF","ES","MG","PA","PB","PI",
            "RJ","RN","RO","RR","RS","SC","SE","SP","TO"
        ];

        string[] svcRs = ["AM","BA","GO","MA","MS","MT","PE","PR"];

        Assert.Equal(27, svcAn.Length + svcRs.Length);

        foreach (var uf in svcAn)
            Assert.Equal("SVC-AN", _reg.ResolverAutorizadorContingencia(uf));

        foreach (var uf in svcRs)
            Assert.Equal("SVC-RS", _reg.ResolverAutorizadorContingencia(uf));
    }

    /// <summary>
    /// O SVC-AN nao possui host proprio: compartilha exatamente as URLs do SVAN.
    /// O host www.svc.fazenda.gov.br aparece em muito material desatualizado e
    /// nao consta na relacao oficial.
    /// </summary>
    [Theory]
    [InlineData(TipoAmbiente.Producao, "www.sefazvirtual.fazenda.gov.br")]
    [InlineData(TipoAmbiente.Homologacao, "hom.sefazvirtual.fazenda.gov.br")]
    public void SvcAn_UsaHostDoSvan_NaoSvcFazenda(TipoAmbiente ambiente, string hostEsperado)
    {
        // SP em contingencia cai no SVC-AN.
        var ep = _reg.Resolver("SP", ambiente, ServicoSefaz.Autorizacao, contingencia: true);

        Assert.Equal("SVC-AN", ep.Autorizador);
        Assert.Equal(hostEsperado, new Uri(ep.Url).Host);
        Assert.DoesNotContain("svc.fazenda.gov.br", ep.Url);
    }

    [Fact]
    public void Mg_RecepcaoEvento_UsaPrefixoNFe()
    {
        var ep = _reg.Resolver("MG", TipoAmbiente.Producao, ServicoSefaz.RecepcaoEvento);

        // O caminho oficial e /nfe2/services/NFeRecepcaoEvento4, com prefixo NFe.
        Assert.EndsWith("/nfe2/services/NFeRecepcaoEvento4", ep.Url);
    }

    /// <summary>
    /// ConsultaCadastro tem delegacao propria: apenas AC, ES, RN, PB e SC usam o
    /// SVRS. As demais UFs delegadas ao SVRS nao oferecem o servico e devem
    /// falhar de forma explicita, em vez de chamar um endpoint inexistente.
    /// </summary>
    [Theory]
    [InlineData("AC")]
    [InlineData("ES")]
    [InlineData("RN")]
    [InlineData("PB")]
    [InlineData("SC")]
    public void ConsultaCadastro_UfsDelegadasAoSvrs(string uf)
    {
        Assert.Equal(
            "SVRS", _reg.ResolverAutorizador(uf, servico: ServicoSefaz.ConsultaCadastro));
    }

    [Theory]
    [InlineData("AL")]
    [InlineData("AP")]
    [InlineData("CE")]
    [InlineData("DF")]
    [InlineData("PA")]
    [InlineData("PI")]
    [InlineData("RJ")]
    [InlineData("RO")]
    [InlineData("RR")]
    [InlineData("SE")]
    [InlineData("TO")]
    [InlineData("MA")]
    public void ConsultaCadastro_UfsSemOServico_LancamErro(string uf)
    {
        // A UF e delegada (SVRS ou SVAN) para os demais servicos, mas nao oferece
        // ConsultaCadastro. Sem a regra propria, cairia em endpoint inexistente.
        Assert.NotEqual(uf, _reg.ResolverAutorizador(uf));

        var ex = Assert.Throws<SefazEndpointException>(
            () => _reg.ResolverAutorizador(uf, servico: ServicoSefaz.ConsultaCadastro));

        Assert.Contains("ConsultaCadastro", ex.Message);
    }

    [Fact]
    public void Resolver_StatusServico_RetornaUrlHttps()
    {
        var ep = _reg.Resolver("RS", TipoAmbiente.Homologacao, ServicoSefaz.StatusServico);

        Assert.Equal("RS", ep.Uf);
        Assert.StartsWith("https://", ep.Url);
        Assert.Equal(43, ep.CodigoUf);
        Assert.Equal(TipoAmbiente.Homologacao, ep.Ambiente);
    }

    [Fact]
    public void Producao_E_Homologacao_TemUrlsDiferentes()
    {
        // Confundir os ambientes emitiria nota fiscal real em teste.
        var prod = _reg.Resolver("SP", TipoAmbiente.Producao, ServicoSefaz.Autorizacao);
        var hom = _reg.Resolver("SP", TipoAmbiente.Homologacao, ServicoSefaz.Autorizacao);

        Assert.NotEqual(prod.Url, hom.Url);
    }

    [Fact]
    public void TodasAsUrls_SaoHttpsValidas()
    {
        var invalidas = new List<string>();

        foreach (var ep in _reg.EnumerarTodos())
        {
            if (!Uri.TryCreate(ep.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
                invalidas.Add($"{ep.Autorizador}/{ep.Ambiente}/{ep.Servico}: {ep.Url}");
        }

        Assert.Empty(invalidas);
    }

    [Fact]
    public void Contingencia_ResolveParaSvc()
    {
        var ep = _reg.Resolver(
            "SP", TipoAmbiente.Homologacao, ServicoSefaz.StatusServico, contingencia: true);

        // SP entra em contingencia pelo SVC-AN.
        Assert.StartsWith("SVC", ep.Autorizador);
        Assert.NotEqual("SP", ep.Autorizador);
    }

    [Fact]
    public void Contingencia_NuncaUsaOMesmoAutorizadorDaUf()
    {
        // Contingencia apontando para o autorizador que caiu seria inutil.
        foreach (var uf in _reg.UfsSuportadas)
        {
            var normal = _reg.ResolverAutorizador(uf);
            var conting = _reg.ResolverAutorizadorContingencia(uf);

            Assert.NotEqual(normal, conting);
        }
    }

    [Fact]
    public void Override_TemPrecedenciaSobreOCatalogo()
    {
        // Permite reagir a mudanca de delegacao sem recompilar.
        var ep = _reg.Resolver(
            "SC", TipoAmbiente.Homologacao, ServicoSefaz.StatusServico,
            autorizadorOverride: "SVAN");

        Assert.Equal("SVAN", ep.Autorizador);
    }

    [Fact]
    public void Override_Inexistente_Falha()
    {
        var ex = Assert.Throws<SefazEndpointException>(() =>
            _reg.Resolver("SC", TipoAmbiente.Homologacao, ServicoSefaz.StatusServico,
                autorizadorOverride: "NAO_EXISTE"));

        Assert.Contains("nao existe", ex.Message);
    }

    [Fact]
    public void UfDesconhecida_FalhaComMensagemUtil()
    {
        var ex = Assert.Throws<SefazEndpointException>(() => _reg.ResolverAutorizador("XX"));

        Assert.Contains("XX", ex.Message);
        Assert.Contains("disponiveis", ex.Message);
    }

    /// <summary>
    /// Confere cada URL de producao contra a relacao oficial capturada do Portal
    /// Nacional em 2026-07-28 (oficiais_producao.txt, 84 URLs). Divergencia aqui
    /// significa endpoint inventado ou desatualizado, que em producao apareceria
    /// como falha de conexao no momento da emissao.
    /// </summary>
    [Fact]
    public void UrlsDeProducao_ConferemComARelacaoOficial()
    {
        var arquivo = Path.Combine(AppContext.BaseDirectory, "oficiais_producao.txt");
        Assert.True(File.Exists(arquivo), $"Lista oficial nao encontrada: {arquivo}");

        var oficiais = File.ReadAllLines(arquivo)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(NormalizarUrl)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(84, oficiais.Count);

        var divergentes = _reg.EnumerarTodos()
            .Where(ep => ep.Ambiente == TipoAmbiente.Producao)
            .Where(ep => !oficiais.Contains(NormalizarUrl(ep.Url)))
            .Select(ep => $"{ep.Autorizador}/{ep.Servico}: {ep.Url}")
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        Assert.Empty(divergentes);
    }

    // Host e caminho sao case-insensitive na pratica; as SEFAZ publicam a mesma
    // URL com capitalizacao variada entre paginas.
    private static string NormalizarUrl(string url)
    {
        var uri = new Uri(url.Trim());
        return $"{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()}";
    }

    [Fact]
    public void Autorizacao_E_StatusServico_DisponiveisEmTodaUf()
    {
        // Sao os dois servicos minimos para operar.
        var faltando = new List<string>();

        foreach (var uf in _reg.UfsSuportadas)
        {
            foreach (var amb in new[] { TipoAmbiente.Producao, TipoAmbiente.Homologacao })
            {
                foreach (var srv in new[] { ServicoSefaz.StatusServico, ServicoSefaz.Autorizacao })
                {
                    try { _reg.Resolver(uf, amb, srv); }
                    catch (SefazEndpointException) { faltando.Add($"{uf}/{amb}/{srv}"); }
                }
            }
        }

        Assert.Empty(faltando);
    }
}
