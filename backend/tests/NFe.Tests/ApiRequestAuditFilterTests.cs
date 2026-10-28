using NFe.Api;

namespace NFe.Tests;

public class ApiRequestAuditFilterTests
{
    [Fact]
    public void BuildSafeRoute_IncluiOrigemCaminhoEQuery()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("api.exemplo.com");
        context.Request.Path = "/v1/nfse";
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?ambiente=1");

        Assert.Equal("https://api.exemplo.com/v1/nfse?ambiente=1", ApiRequestAuditMiddleware.BuildSafeRoute(context.Request));
    }

    [Fact]
    public void BuildSafeRoute_NaoTruncaUrlMaiorQueSetecentosCaracteres()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("api.exemplo.com");
        context.Request.Path = "/api/nfse/" + new string('a', 900);
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?ambiente=1&ambiente=2");

        var url = ApiRequestAuditMiddleware.BuildSafeRoute(context.Request);

        Assert.True(url.Length > 700);
        Assert.EndsWith("?ambiente=1&ambiente=2", url);
    }

    [Fact]
    public void BuildSafeRoute_RedigeParametrosSensiveis()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("api.exemplo.com");
        context.Request.Path = "/api/nfse";
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?token=secreto&ambiente=2");

        var url = ApiRequestAuditMiddleware.BuildSafeRoute(context.Request);

        Assert.Contains("https://api.exemplo.com/api/nfse", url);
        Assert.Contains("token=%5Bredigido%5D", url);
        Assert.Contains("ambiente=2", url);
        Assert.DoesNotContain("secreto", url);
    }

    [Theory]
    [InlineData("GET", "/api/empresas", 200, 120, false)]
    [InlineData("GET", "/api/empresas", 500, 120, true)]
    [InlineData("GET", "/api/empresas", 200, 2_000, true)]
    [InlineData("GET", "/api/empresas", 200, 1_999, false)]
    [InlineData("POST", "/api/nfse/emitir", 200, 120, true)]
    [InlineData("PATCH", "/api/config", 204, 120, true)]
    [InlineData("DELETE", "/api/documentos/1", 204, 120, true)]
    [InlineData("GET", "/api/monitor/logs", 500, 2_000, false)]
    public void ShouldPersistLog_FiltersOnlyRelevantEvents(
        string method,
        string route,
        int status,
        int durationMs,
        bool expected)
    {
        Assert.Equal(expected, ApiRequestAuditMiddleware.ShouldPersistLog(method, route, status, durationMs));
    }
}
