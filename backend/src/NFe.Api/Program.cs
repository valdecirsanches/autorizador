using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NFe.Api;
using NFe.Api.ClientesApi;
using NFe.Api.Endpoints;
using NFe.Infrastructure.Configuracao;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Infrastructure.Servicos;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;

// O comando de migration pode receber o mesmo EnvironmentFile usado pelo
// systemd; carrega-o antes do .env do checkout, sem sobrescrever env real.
string? envServico = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--env-file")
    {
        envServico = args[i + 1];
        break;
    }
}

if (envServico is not null && DotEnv.LoadFile(envServico) is null)
{
    Console.Error.WriteLine($"[env] EnvironmentFile não encontrado: {envServico}");
    return 1;
}

// Carrega o .env.local (ou .env) da raiz do projeto ANTES de ler qualquer
// variavel. Variaveis do systemd/shell e o EnvironmentFile explicito tem
// prioridade sobre o arquivo do checkout.
var envCarregado = DotEnv.Load(Directory.GetCurrentDirectory());
Console.WriteLine(envCarregado is null
    ? $"[env] Nenhum .env.local/.env encontrado a partir de "
      + $"'{Directory.GetCurrentDirectory()}'. Usando apenas variaveis do "
      + "ambiente (shell export ou systemd EnvironmentFile)."
    : $"[env] Variaveis carregadas de: {envCarregado}");

if (args.Contains("--migrate-database", StringComparer.Ordinal))
{
    try
    {
        await using var db = new DesignTimeDbContextFactory().CreateDbContext(args);
        var pendentes = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        Console.WriteLine(pendentes.Length == 0
            ? "[migration] Nenhuma migration pendente."
            : $"[migration] Aplicando: {string.Join(", ", pendentes)}");

        await IdempotentMigrationApplier.ApplyPendingAsync(db, pendentes);

        var aplicadas = await db.Database.GetAppliedMigrationsAsync();
        if (!aplicadas.Contains("20260930000001_ChaveAutenticacaoOsasco", StringComparer.Ordinal))
            throw new InvalidOperationException(
                "A migration ChaveAutenticacaoOsasco não consta no histórico aplicado.");

        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'empresa_nfse_config'
              AND COLUMN_NAME = 'osasco_chave_autenticacao';
            """;
        var colunaExiste = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        if (!colunaExiste)
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE `empresa_nfse_config` "
                + "ADD COLUMN `osasco_chave_autenticacao` varchar(500) NULL;");
            Console.WriteLine(
                "[migration] Coluna Osasco ausente no schema; reparo aditivo aplicado.");
        }

        if (await EmpresaSchema.EnsurePastaXmlBackupAsync(db))
            Console.WriteLine(
                "[migration] Coluna empresa.pasta_xml_backup ausente; reparo aditivo aplicado.");

        Console.WriteLine(
            "[migration] Histórico confirmado e coluna Osasco verificada no banco.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[migration] Deploy cancelado: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

if (args.Contains("--ensure-api-request-audit-schema", StringComparer.Ordinal))
{
    try
    {
        await using var db = new DesignTimeDbContextFactory().CreateDbContext(args);
        await ApiRequestAuditMiddleware.EnsureSchemaAsync(db);
        Console.WriteLine("[schema] Tabela api_request_log verificada.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[schema] Falha ao preparar api_request_log: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

// Licenca do QuestPDF (geracao do DANFE em PDF). A licenca Community e gratuita
// para empresas/faturamento ate US$1M/ano; deve ser declarada no startup.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

if (args.Contains("--ensure-json-recebido-schema", StringComparer.Ordinal))
{
    var schemaConnectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(schemaConnectionString))
    {
        Console.Error.WriteLine(
            "[schema] MYSQL_CONNECTION_STRING não foi carregada pelo ambiente ou .env.local.");
        return 1;
    }

    try
    {
        await JsonRecebidoSchema.EnsureAsync(schemaConnectionString);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[schema] Falha ao preparar json_recebido: {ex.Message}");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuracao. Segredos vem do ambiente (ou do .env.local), nunca de arquivo
// versionado.
// ---------------------------------------------------------------------------
var connectionString =
    Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("MySql")
    ?? throw new InvalidOperationException(
        "MYSQL_CONNECTION_STRING nao definida. A API precisa do banco para operar.");

var masterKey = Environment.GetEnvironmentVariable("NFE_CERT_MASTER_KEY");

// Token de acesso a API. TODA requisicao (exceto /health e preflight CORS)
// precisa mandar "Authorization: Bearer <token>". Fail-fast: sem token, a API
// NAO sobe — assim nunca fica acidentalmente aberta ao mundo.
var apiToken = Environment.GetEnvironmentVariable("NFE_API_TOKEN");
if (string.IsNullOrWhiteSpace(apiToken))
{
    Console.Error.WriteLine(
        "[FATAL] NFE_API_TOKEN nao definida. A API exige um token de acesso e "
        + "se recusa a iniciar sem ele. Gere um valor forte (ex.: "
        + "`openssl rand -base64 48`) e defina NFE_API_TOKEN no ambiente.");
    return 1;
}
// Normaliza o token: remove espacos/quebras de linha nas pontas e aspas
// externas. Isso evita o 401 silencioso quando o .env foi salvo como
// NFE_API_TOKEN="valor" ou com uma quebra de linha ao final — casos em que os
// bytes deixam de bater com o token enviado pelo painel mesmo "parecendo" igual.
apiToken = NormalizarToken(apiToken);
// Bytes do token, calculados uma vez, para comparacao em tempo constante.
var apiTokenBytes = System.Text.Encoding.UTF8.GetBytes(apiToken);
// Fingerprint: SHA-256 -> 8 hex. NAO expoe o segredo, mas permite comparar, a
// olho nu nos logs, se painel e API carregaram exatamente o mesmo token.
Console.WriteLine(
    $"[auth] Token da API carregado. Comprimento={apiToken.Length}. "
    + $"Fingerprint={FingerprintToken(apiToken)}. O painel loga o mesmo "
    + "fingerprint ao chamar a API; se forem diferentes, os tokens divergem.");

static string NormalizarToken(string bruto)
{
    var t = bruto.Trim();
    if (t.Length >= 2 &&
        ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
    {
        t = t[1..^1].Trim();
    }
    return t;
}

static string FingerprintToken(string valor)
{
    var hash = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(valor));
    return Convert.ToHexString(hash)[..8].ToLowerInvariant();
}

// Origem do painel para o CORS. Em desenvolvimento liberamos o dev server local.
var corsOrigins = (Environment.GetEnvironmentVariable("NFE_CORS_ORIGINS")
        ?? "http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// ---------------------------------------------------------------------------
// Servicos.
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<NFeDbContext>(opt =>
    opt.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// O cofre e singleton: a chave mestra e imutavel durante a vida do processo.
// Construimos AQUI (nao lazy) para imprimir a "impressao digital" da chave
// ativa no startup. Assim voce confirma, a olho nu e sem expor o segredo, qual
// chave este processo carregou — e se ha mais de um processo com chaves
// diferentes, cada log mostrara um fingerprint distinto.
var cofre = CofreCriptografico.DeBase64(masterKey);
Console.WriteLine(
    $"[cofre] Chave mestra ativa. Fingerprint: {cofre.Fingerprint}. "
    + "Um certificado so decifra se foi salvo com ESTA mesma chave (mesmo "
    + "fingerprint). Se a emissao falhar por autenticidade, reenvie o A1.");
builder.Services.AddSingleton(cofre);

// Registry de endpoints e imutavel apos carregado: singleton.
builder.Services.AddSingleton(_ => SefazEndpointRegistry.LoadDefault());

// Cliente SOAP compartilha um HttpClient interno; singleton evita esgotar
// sockets. Em producao PermitirCadeiaNaoConfiavel fica falso.
// Leitura TOLERANTE: um valor mal digitado (ex.: "falsel") NAO pode derrubar a
// API. Interpretamos apenas variantes claras de "verdadeiro"; qualquer outra
// coisa (inclusive lixo) vira false, e logamos um aviso.
var flagBruta = builder.Configuration["Sefaz:PermitirCadeiaNaoConfiavel"];
var permitirCadeiaNaoConfiavel =
    flagBruta?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "sim";

if (!string.IsNullOrWhiteSpace(flagBruta) &&
    !bool.TryParse(flagBruta.Trim(), out _))
{
    Console.WriteLine(
        $"[SEFAZ] AVISO: Sefaz__PermitirCadeiaNaoConfiavel='{flagBruta}' nao e " +
        $"um booleano valido; assumindo {permitirCadeiaNaoConfiavel}. " +
        "Use 'true' ou 'false'.");
}

builder.Services.AddSingleton(sp => new SefazSoapClient(
    new SefazSoapOptions
    {
        PermitirCadeiaNaoConfiavel = permitirCadeiaNaoConfiavel
    },
    sp.GetService<ILogger<SefazSoapClient>>()));

// ServicoCertificado recebe NFeDbContext (scoped) e CofreCriptografico
// (singleton) diretamente do container.
builder.Services.AddSingleton<AgenteHub>();
builder.Services.AddSingleton<IProvedorAgenteCertificado>(sp => sp.GetRequiredService<AgenteHub>());
builder.Services.AddScoped<ServicoCertificado>();
builder.Services.AddScoped(sp => new StatusServicoClient(
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetRequiredService<SefazSoapClient>()));
builder.Services.AddScoped<MonitorStatusServico>();

// ---------------------------------------------------------------------------
// Pipeline de emissao de NF-e. Gerador e assinador nao tem estado: singletons.
// AutorizacaoClient depende de dois singletons. O reservador é usado pelos
// endpoints de configuração; ServicoAutorizacao usa o DbContext: scoped.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<GeradorXmlNFe>();
builder.Services.AddSingleton<AssinadorXml>();
builder.Services.AddSingleton(sp => new AutorizacaoClient(
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetRequiredService<SefazSoapClient>()));

// Validador XSD e OPCIONAL: so ativa quando NFE_SCHEMAS_DIR aponta para uma
// pasta existente com os schemas oficiais da NF-e. Sem ele, a emissao funciona;
// apenas nao ha a checagem local contra o XSD antes de transmitir.
var schemasDir = Environment.GetEnvironmentVariable("NFE_SCHEMAS_DIR");
if (!string.IsNullOrWhiteSpace(schemasDir) && Directory.Exists(schemasDir))
    builder.Services.AddSingleton(_ => new ValidadorXsd(schemasDir));

// Arquivador de XML em disco: espelha cada XML gerado numa PASTA POR EMPRESA
// (CNPJ). Base configuravel por NFE_XML_DIR; padrao "xmls" na raiz de execucao.
// Singleton: nao tem estado por requisicao, so o diretorio base.
var xmlDir = Environment.GetEnvironmentVariable("NFE_XML_DIR");
var xmlBaseDir = string.IsNullOrWhiteSpace(xmlDir)
    ? Path.Combine(Directory.GetCurrentDirectory(), "xmls")
    : xmlDir;
builder.Services.AddSingleton(sp => new ArquivadorXml(
    xmlBaseDir, sp.GetService<ILogger<ArquivadorXml>>()));

builder.Services.AddScoped<ReservadorNumeracao>();
builder.Services.AddScoped(sp => new ServicoAutorizacao(
    sp.GetRequiredService<NFeDbContext>(),
    sp.GetRequiredService<ServicoCertificado>(),
    sp.GetRequiredService<GeradorXmlNFe>(),
    sp.GetRequiredService<AssinadorXml>(),
    sp.GetRequiredService<AutorizacaoClient>(),
    sp.GetService<ValidadorXsd>(),
    sp.GetRequiredService<ArquivadorXml>(),
    sp.GetService<ILogger<ServicoAutorizacao>>()));

// Consulta de situacao (consSitNFe): mesmo padrao do status — client sem estado
// mais um servico scoped que carrega o certificado da empresa.
builder.Services.AddScoped(sp => new ConsultaProtocoloClient(
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetRequiredService<SefazSoapClient>()));
builder.Services.AddScoped<ServicoConsultaProtocolo>();

// Eventos fiscais (cancelamento, futuramente CC-e). O EventoClient nao tem
// estado (depende de dois singletons); o ServicoEventos usa o DbContext: scoped.
builder.Services.AddSingleton(sp => new EventoClient(
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetRequiredService<SefazSoapClient>()));
builder.Services.AddScoped(sp => new ServicoEventos(
    sp.GetRequiredService<NFeDbContext>(),
    sp.GetRequiredService<ServicoCertificado>(),
    sp.GetRequiredService<AssinadorXml>(),
    sp.GetRequiredService<EventoClient>(),
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetService<ILogger<ServicoEventos>>()));

// Inutilizacao de faixa de numeracao (NfeInutilizacao4). Mesmo padrao: client
// singleton sem estado + servico scoped que usa o DbContext.
builder.Services.AddSingleton(sp => new InutilizacaoClient(
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetRequiredService<SefazSoapClient>()));
builder.Services.AddScoped(sp => new ServicoInutilizacao(
    sp.GetRequiredService<NFeDbContext>(),
    sp.GetRequiredService<ServicoCertificado>(),
    sp.GetRequiredService<AssinadorXml>(),
    sp.GetRequiredService<InutilizacaoClient>(),
    sp.GetRequiredService<SefazEndpointRegistry>(),
    sp.GetService<ILogger<ServicoInutilizacao>>()));

// Auditoria de tentativas de emissao: grava em um DbContext isolado obtido via
// IServiceScopeFactory, entao pode ser singleton com seguranca.
builder.Services.AddSingleton<RegistradorTentativa>();

// ---------------------------------------------------------------------------
// Modulo NFS-e (padrao nacional + ABRASF). Registro TOTALMENTE separado da
// NF-e: nenhum servico acima e alterado; o modulo apenas reutiliza o
// ServicoCertificado (mesmo A1) e o AssinadorXml (mesma XML-DSig). Se algo aqui
// falhar, a emissao de NF-e continua intacta.
// ---------------------------------------------------------------------------
var nfseHttpOptions = new NFe.Nfse.Nacional.NfseHttpOptions
{
    // Reaproveita a mesma flag da SEFAZ para ambientes de teste com proxy/TLS
    // interceptado; em producao deve ficar false.
    PermitirCadeiaNaoConfiavel = permitirCadeiaNaoConfiavel,
    LogarCorpos = string.Equals(
        Environment.GetEnvironmentVariable("NFSE_LOG_CORPOS"), "true",
        StringComparison.OrdinalIgnoreCase)
};
var nfseTimeout = Environment.GetEnvironmentVariable("NFSE_TIMEOUT_SEGUNDOS");
if (int.TryParse(nfseTimeout, out var nfseTimeoutSeg) && nfseTimeoutSeg > 0)
    nfseHttpOptions.Timeout = TimeSpan.FromSeconds(nfseTimeoutSeg);

builder.Services.AddSingleton(sp => new NFe.Nfse.Nacional.HttpMtlsClientFactory(
    nfseHttpOptions, sp.GetService<ILogger<NFe.Nfse.Nacional.HttpMtlsClientFactory>>()));
builder.Services.AddSingleton<NFe.Nfse.Nacional.GeradorXmlDps>();
builder.Services.AddSingleton<NFe.Nfse.Nacional.GeradorXmlEventoNfse>();
builder.Services.AddSingleton(sp => new NFe.Nfse.Nacional.ProvedorNacional(
    sp.GetRequiredService<NFe.Nfse.Nacional.HttpMtlsClientFactory>(),
    sp.GetRequiredService<NFe.Nfse.Nacional.GeradorXmlDps>(),
    sp.GetRequiredService<NFe.Nfse.Nacional.GeradorXmlEventoNfse>(),
    sp.GetRequiredService<AssinadorXml>(),
    sp.GetService<ILogger<NFe.Nfse.Nacional.ProvedorNacional>>()));
  builder.Services.AddSingleton(sp => new NFe.Nfse.Abrasf.ProvedorAbrasfGenerico(
      sp.GetRequiredService<NFe.Nfse.Nacional.HttpMtlsClientFactory>(),
      sp.GetRequiredService<AssinadorXml>(),
      sp.GetService<ILogger<NFe.Nfse.Abrasf.ProvedorAbrasfGenerico>>()));
  builder.Services.AddSingleton<NFe.Nfse.ProvedorOsasco>();


// Arquivos da NFS-e vao para {NFE_XML_DIR}/{cnpj}/nfse/ — mesma raiz, subpasta
// propria, sem colidir com os arquivos da NF-e.
builder.Services.AddSingleton(sp => new NFe.Infrastructure.ServicosNfse.ArquivadorXmlNfse(
    xmlBaseDir, sp.GetService<ILogger<NFe.Infrastructure.ServicosNfse.ArquivadorXmlNfse>>()));
builder.Services.AddSingleton<NFe.Infrastructure.ServicosNfse.RegistradorTentativaNfse>();
builder.Services.AddScoped<NFe.Infrastructure.ServicosNfse.ReservadorNumeracaoDps>();
builder.Services.AddScoped<NFe.Infrastructure.ServicosNfse.ServicoNfse>();

// Chaves de acesso por app (clientes da API). Singleton: guarda o cache das
// chaves e abre conexao propria a cada consulta.
builder.Services.AddSingleton<RepositorioClientesApi>();

// "Liberar tudo": se NFE_CORS_ORIGINS for exatamente "*", qualquer origem e
// aceita. Util em teste. NAO recomendado em producao (qualquer site poderia
// chamar esta API pelo navegador do usuario). Como nao usamos cookies/credenciais
// entre origens, AllowAnyOrigin e valido aqui.
var liberarTudo = corsOrigins.Any(o => o.Trim() == "*");

// Suporte a curinga de subdominio em NFE_CORS_ORIGINS
// (ex.: "https://*.magicpatterns.app"). "*" casa um nivel de subdominio.
var padroesCuringa = corsOrigins
    .Where(o => o.Contains('*') && o.Trim() != "*")
    .Select(o => new Regex(
        "^" + Regex.Escape(o).Replace("\\*", "[^.]*") + "$",
        RegexOptions.IgnoreCase))
    .ToArray();
var origensExatas = corsOrigins.Where(o => !o.Contains('*')).ToArray();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (liberarTudo)
        p.SetIsOriginAllowed(_ => true);
    else
        p.SetIsOriginAllowed(origin =>
            origensExatas.Contains(origin, StringComparer.OrdinalIgnoreCase)
            || padroesCuringa.Any(rx => rx.IsMatch(origin)));

    p.AllowAnyHeader().AllowAnyMethod();
}));

if (liberarTudo)
    Console.WriteLine(
        "[CORS] ATENCAO: NFE_CORS_ORIGINS='*' — TODAS as origens estao liberadas. " +
        "Use apenas em teste; restrinja antes de producao.");

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Informe a chave do seu app (nfeapp_...) ou o token mestre. "
                + "O prefixo Bearer e adicionado automaticamente.",
        };
        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            }] = Array.Empty<string>(),
        });
        LogoOpenApiDocumentTransformer.AddLogoUploadSchema(document);
        return Task.CompletedTask;
    });
});

var app = builder.Build();

// A coluna empresa.pasta_xml_backup e lida em toda listagem de empresas. Se a
// migration ainda nao rodou, cria a coluna aqui (aditivo e idempotente) em vez
// de deixar o painel sem empresas.
try
{
    await using var escopoSchema = app.Services.CreateAsyncScope();
    var dbSchema = escopoSchema.ServiceProvider.GetRequiredService<NFeDbContext>();
    if (await EmpresaSchema.EnsurePastaXmlBackupAsync(dbSchema))
        app.Logger.LogWarning(
            "Coluna empresa.pasta_xml_backup ausente; criada no startup. "
            + "Rode --migrate-database no deploy para manter o historico em dia.");
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Nao foi possivel garantir a coluna empresa.pasta_xml_backup. "
        + "Aplique a migration 20261027000000_PastaXmlBackupEmpresa.");
}

// Prepara e ativa o monitor antes dos handlers, para registrar também respostas
// 4xx/5xx e falhas de autenticação com o status final.
await app.UseApiRequestAuditAsync();
app.UseApiRequestAudit();

// Tabelas das chaves por app (aditivo e idempotente). Se falhar, o token
// mestre continua funcionando; apenas as chaves de app ficam indisponiveis.
try
{
    await using var escopoClientes = app.Services.CreateAsyncScope();
    await RepositorioClientesApi.EnsureSchemaAsync(
        escopoClientes.ServiceProvider.GetRequiredService<NFeDbContext>());
    app.Logger.LogInformation("Tabelas de chaves por app verificadas.");
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Nao foi possivel preparar as tabelas api_cliente/api_cliente_empresa. "
        + "Chaves de app ficam indisponiveis; o token mestre segue funcionando.");
}

// Log do valor EFETIVO da flag de validacao TLS. Lembrando que uma variavel
// exportada no shell vence o .env.local: se este valor nao for o que voce
// espera, verifique um `export Sefaz__PermitirCadeiaNaoConfiavel=...` ativo no
// terminal (rode `unset Sefaz__PermitirCadeiaNaoConfiavel`).
app.Logger.LogInformation(
    "[SEFAZ] PermitirCadeiaNaoConfiavel (validacao TLS relaxada) = {Valor}",
    permitirCadeiaNaoConfiavel);

// Onde os XMLs gerados sao espelhados em disco (uma subpasta por CNPJ).
app.Logger.LogInformation(
    "[XML] XMLs das notas serao salvos em: {Dir} (uma pasta por empresa/CNPJ). "
    + "Ajuste com NFE_XML_DIR.", xmlBaseDir);

// ---------------------------------------------------------------------------
// Migracoes automaticas no startup: garante que as tabelas existam sem o
// operador precisar rodar `dotnet ef database update` a mao. Se o banco estiver
// inacessivel, logamos e seguimos (o health check e o handler de erro cuidam
// de reportar). Controle com NFE_AUTO_MIGRATE=false para desligar.
// ---------------------------------------------------------------------------
var autoMigrar = !string.Equals(
    Environment.GetEnvironmentVariable("NFE_AUTO_MIGRATE"), "false",
    StringComparison.OrdinalIgnoreCase);

if (autoMigrar)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();
    try
    {
        var pendentes = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pendentes.Count > 0)
        {
            app.Logger.LogInformation(
                "Aplicando {Qtd} migracao(oes) pendente(s): {Migracoes}",
                pendentes.Count, string.Join(", ", pendentes));
            await IdempotentMigrationApplier.ApplyPendingAsync(db, pendentes);
            app.Logger.LogInformation("Migracoes aplicadas. Banco pronto.");
        }
        else
        {
            app.Logger.LogInformation("Banco ja esta na ultima migracao.");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Falha ao aplicar migracoes no startup. Verifique a "
            + "MYSQL_CONNECTION_STRING e se o MySQL esta acessivel.");
    }
}

// ---------------------------------------------------------------------------
// Tratamento global de erros: SEMPRE responde JSON (nunca a pagina HTML de
// erro do ASP.NET), para o painel conseguir exibir a causa em vez de quebrar
// no JSON.parse. Detecta o erro classico de tabela inexistente e sugere rodar
// as migracoes.
// ---------------------------------------------------------------------------
app.UseExceptionHandler(branch => branch.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var msg = ex?.Message ?? "Erro interno.";

    if (ex is NFe.Sefaz.Seguranca.AgenteCertificadoException)
    {
        app.Logger.LogWarning(ex, "Agente A3 indisponivel em {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            erro = "Agente do certificado A3 desconectado.",
            detalhe = msg,
            tipo = nameof(NFe.Sefaz.Seguranca.AgenteCertificadoException)
        });
        return;
    }

    // MySQL 1146 / SQLSTATE 42S02: "Table '...' doesn't exist".
    var tabelaFaltando = msg.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("42S02", StringComparison.OrdinalIgnoreCase);

    var erro = tabelaFaltando
        ? "Banco sem as tabelas do sistema. Rode as migracoes: "
          + "dotnet ef database update --project src/NFe.Infrastructure "
          + "--startup-project src/NFe.Api"
        : "Falha ao processar a requisicao.";

    app.Logger.LogError(ex, "Erro nao tratado em {Path}", ctx.Request.Path);

    ctx.Response.StatusCode = tabelaFaltando ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";

    // Devolvemos SEMPRE o detalhe tecnico (dev e producao). A API e protegida
    // por token Bearer — quem recebe a resposta ja e um operador autenticado —
    // e sem o detalhe fica impossivel diagnosticar falhas no servidor (ex.: EC2),
    // onde so aparecia "Falha ao processar a requisicao." Incluimos tambem o
    // tipo da excecao, que costuma apontar a causa (ex.: erro ao abrir o PFX no
    // Linux, falha de conexao com o banco, timeout de rede com a SEFAZ).
    var payload = new { erro, detalhe = msg, tipo = ex?.GetType().Name };
    await ctx.Response.WriteAsJsonAsync(payload);
}));

// Schema OpenAPI em /openapi/v1.json em todos os ambientes: os apps integrados
// leem os campos aceitos por aqui. Fica atras do token Bearer (middleware abaixo).
app.MapOpenApi();
// Alias no caminho "swagger" que muitos clientes consultam por padrao.
app.MapOpenApi("/swagger/v1/swagger.json");

app.UseCors();
// Interface interativa para testar endpoints. O documento OpenAPI e protegido
// pelo Bearer e declara o campo binario do upload de logo para exibir o seletor.
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Autorizador NF-e API v1");
    options.RoutePrefix = "swagger";
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// ---------------------------------------------------------------------------
// Autenticacao (Bearer). Roda DEPOIS do CORS para que o preflight (OPTIONS)
// seja respondido normalmente pelo navegador, e ANTES dos endpoints.
// Liberados sem token: preflight CORS, /health, /diag/auth e o WebSocket do
// agente A3. O resto aceita:
//   - o token mestre NFE_API_TOKEN (acesso total; painel e integracoes antigas);
//   - uma chave de app "nfeapp_..." (so as empresas liberadas para o app).
// Detalhes e regras de acesso: ClientesApi/AutenticacaoApi.cs e
// ClientesApi/PoliticaAcessoApi.cs.
// ---------------------------------------------------------------------------
app.Use((HttpContext ctx, Func<Task> next) =>
    AutenticacaoApi.ProcessarAsync(ctx, next, apiTokenBytes));

// Health check simples para o painel confirmar que a API esta no ar.
// Tambem informa a versao e a data de compilacao, para o painel mostrar qual
// build esta no ar (e o operador confirmar que o deploy pegou).
var assemblyApi = typeof(Program).Assembly;
var versaoInformada = assemblyApi
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
// O SDK anexa "+<hash do commit>" quando compila dentro de um repositorio git.
var versaoApi = versaoInformada.Split('+')[0];
var commitApi = versaoInformada.Contains('+') ? versaoInformada.Split('+')[1] : null;
var compiladaEm = string.IsNullOrEmpty(assemblyApi.Location)
    ? (DateTime?)null
    : File.GetLastWriteTimeUtc(assemblyApi.Location);
var iniciadaEm = DateTime.UtcNow;
app.Logger.LogInformation("[versao] NFe.Api {Versao} (commit {Commit}), compilada em {Data:u}.",
    versaoApi, commitApi ?? "-", compiladaEm);

app.MapGet("/health", () => Results.Ok(new
   {
       status = "ok",
       hora = DateTime.UtcNow,
       versao = versaoApi,
       commit = commitApi is { Length: > 7 } ? commitApi[..7] : commitApi,
       compiladaEm,
       iniciadaEm,
       recursos = new[] { "chaves-por-app" },
   }))
   .WithName("Health");

// Diagnostico de autenticacao: devolve APENAS o comprimento e o fingerprint do
// token que ESTA API carregou. Nao revela o token. O painel chama isto e
// compara com o proprio token — se os fingerprints diferirem, os .env estao
// com valores distintos (causa classica do 401 "Nao autorizado.").
app.MapGet("/diag/auth", () => Results.Ok(new
{
    tokenLength = apiToken.Length,
    tokenFingerprint = FingerprintToken(apiToken),
})).WithName("DiagAuth");

// Diagnostico do BANCO: mostra a QUAL MySQL/base ESTA API esta conectada e
// quantos documentos existem — para resolver na hora o caso "o banco tem X
// notas, mas o painel mostra Y" (quase sempre a API aponta para outro MySQL ou
// outra base). Exige token (nao esta na whitelist), entao so o painel
// autenticado ve o host. NAO expoe usuario/senha da connection string.
app.MapGet("/diag/db", async (NFeDbContext db, Guid? empresaId, CancellationToken ct) =>
{
    var csb = new MySqlConnector.MySqlConnectionStringBuilder(connectionString);
    var total = await db.DocumentosFiscais.AsNoTracking().CountAsync(ct);
    int? daEmpresa = empresaId is Guid e
        ? await db.DocumentosFiscais.AsNoTracking().CountAsync(d => d.EmpresaId == e, ct)
        : null;
    return Results.Ok(new
    {
        host = csb.Server,
        port = csb.Port,
        database = csb.Database,
        totalDocumentos = total,
        documentosEmpresa = daEmpresa,
    });
}).WithName("DiagDb");

// Grupos de endpoints.
app.MapEmpresas();
app.MapCertificados();
app.MapAgentes();
app.MapStatusServico();
app.MapEmissao();
app.MapNfse();
app.MapApiMonitor();
app.MapClientesApi();

app.Run();

// Como ha um `return 1;` no fail-fast do token la em cima, o compilador passa a
// exigir que TODOS os caminhos retornem um int. app.Run() bloqueia ate a API
// parar; ao encerrar normalmente, retornamos 0 (sucesso).
return 0;
