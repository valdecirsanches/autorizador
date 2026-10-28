using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NFe.AgenteA3;

// Comandos:
//   NFeAgenteA3 configurar   pareia com a API e guarda o PIN (cifrado)
//   NFeAgenteA3 instalar     inicia o agente sozinho ao fazer logon no Windows
//   NFeAgenteA3 desinstalar  remove a inicializacao automatica
//   NFeAgenteA3              executa o agente
var comando = args.FirstOrDefault()?.ToLowerInvariant();

return comando switch
{
    "configurar" => Configurar(),
    "instalar" => Instalar(),
    "desinstalar" => Agendador("/Delete", "/TN", "NFeAgenteA3", "/F"),
    null or "executar" => await Executar(),
    _ => Uso(),
};

static int Uso()
{
    Console.WriteLine("Uso: NFeAgenteA3 [configurar | instalar | desinstalar | executar]");
    return 1;
}

static async Task<int> Executar()
{
    var config = ConfiguracaoAgente.Carregar();
    if (config is null || string.IsNullOrEmpty(config.Token))
    {
        Console.WriteLine("Agente nao configurado. Execute: NFeAgenteA3 configurar");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var agente = new AgenteWorker(config);
    Console.WriteLine($"NFe Agente A3 v{AgenteWorker.Versao} - API {config.ApiUrl}");
    await agente.ExecutarAsync(cts.Token);
    return 0;
}

static int Configurar()
{
    Console.WriteLine("Configuracao do agente do certificado A3\n");

    Console.Write("URL da API (ex.: https://api.suaempresa.com.br): ");
    var url = Console.ReadLine()?.Trim() ?? string.Empty;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
    {
        Console.WriteLine("URL invalida.");
        return 1;
    }

    Console.Write("Token de pareamento (gerado ao cadastrar o certificado A3): ");
    var token = LerSegredo();
    if (token.Length < 20)
    {
        Console.WriteLine("Token invalido.");
        return 1;
    }

    var certificados = TokenCertificado.Listar();
    if (certificados.Count == 0)
    {
        Console.WriteLine("Nenhum certificado com chave privada encontrado. Plugue o token e confira o driver.");
        return 1;
    }

    Console.WriteLine("\nCertificados disponiveis:");
    for (var i = 0; i < certificados.Count; i++)
        Console.WriteLine($"  [{i + 1}] {certificados[i].GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}" +
                          $"  (vence {certificados[i].NotAfter:dd/MM/yyyy})  {certificados[i].Thumbprint}");

    Console.Write("Numero do certificado: ");
    if (!int.TryParse(Console.ReadLine(), out var n) || n < 1 || n > certificados.Count)
    {
        Console.WriteLine("Opcao invalida.");
        return 1;
    }
    var escolhido = certificados[n - 1];

    Console.Write("PIN do token: ");
    var pin = LerSegredo();

    var config = new ConfiguracaoAgente
    {
        ApiUrl = url,
        Token = token,
        Thumbprint = escolhido.Thumbprint,
    };
    config.DefinirPin(pin);

    // Um unico teste: PIN errado repetido bloqueia o token.
    Console.WriteLine("\nTestando o PIN com uma assinatura (tentativa unica)...");
    try
    {
        using var teste = new TokenCertificado(config.Thumbprint, pin);
        teste.AssinarHash(SHA256.HashData("teste"u8.ToArray()), HashAlgorithmName.SHA256);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Falha: {ex.Message}\nNada foi salvo.");
        return 1;
    }

    config.Salvar();
    Console.WriteLine($"Configuracao salva em {ConfiguracaoAgente.Caminho}");
    Console.WriteLine("Proximo passo: NFeAgenteA3 instalar (ou execute NFeAgenteA3 para testar agora).");
    return 0;
}

static int Instalar()
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executavel nao localizado.");
    return Agendador("/Create", "/TN", "NFeAgenteA3", "/SC", "ONLOGON", "/RL", "LIMITED", "/F",
        "/TR", $"\"{exe}\"");
}

static int Agendador(params string[] argumentos)
{
    var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false };
    foreach (var a in argumentos) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

static string LerSegredo()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString().Trim(); }
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
}
