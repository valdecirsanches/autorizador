using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NFe.AgenteA3;

/// <summary>
/// Mantem a conexao de saida com a API e executa os dois comandos que ela envia:
/// assinar um hash com o token e fazer o POST SOAP com TLS mutuo.
/// </summary>
public sealed class AgenteWorker : IDisposable
{
    public const string Versao = "1.0.0";

    private static readonly TimeSpan EsperaMinima = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(60);

    private readonly ConfiguracaoAgente _config;
    private readonly TokenCertificado _token;
    private readonly TransporteSoap _soap;
    private readonly SemaphoreSlim _envio = new(1, 1);

    public AgenteWorker(ConfiguracaoAgente config)
    {
        _config = config;
        _token = new TokenCertificado(config.Thumbprint, config.LerPin());
        _soap = new TransporteSoap(_token, config.PermitirCadeiaNaoConfiavel);
    }

    public async Task ExecutarAsync(CancellationToken ct)
    {
        var espera = EsperaMinima;
        while (!ct.IsCancellationRequested)
        {
            var inicio = DateTime.UtcNow;
            try
            {
                await ConectarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log($"Conexao perdida: {ex.Message}");
            }

            // Se a conexao durou, recomecamos do intervalo curto.
            espera = DateTime.UtcNow - inicio > TimeSpan.FromMinutes(1)
                ? EsperaMinima
                : TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, EsperaMaxima.TotalSeconds));

            Log($"Nova tentativa em {espera.TotalSeconds:0}s.");
            try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConectarAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _config.Token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await socket.ConnectAsync(_config.UriWebSocket(), ct);

        await EnviarAsync(socket, new
        {
            tipo = "hello",
            thumbprint = _config.Thumbprint,
            versao = Versao,
        }, ct);

        Log("Conectado a API.");

        var buffer = new byte[16 * 1024];
        using var acumulado = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            acumulado.SetLength(0);
            ValueWebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    Log($"API encerrou a conexao: {socket.CloseStatusDescription}");
                    return;
                }
                acumulado.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            var bytes = acumulado.ToArray();
            _ = Task.Run(() => TratarAsync(socket, bytes, ct), ct);
        }
    }

    private async Task TratarAsync(ClientWebSocket socket, byte[] bytes, CancellationToken ct)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var raiz = doc.RootElement;

            if (raiz.TryGetProperty("tipo", out var tipo))
            {
                if (tipo.GetString() == "ping")
                    await EnviarAsync(socket, new { tipo = "pong" }, ct);
                return;
            }

            id = raiz.GetProperty("id").GetString();
            var op = raiz.GetProperty("op").GetString();

            switch (op)
            {
                case "assinar":
                {
                    var hash = Convert.FromBase64String(raiz.GetProperty("hash").GetString()!);
                    var algoritmo = new HashAlgorithmName(raiz.GetProperty("algoritmo").GetString());
                    var assinatura = _token.AssinarHash(hash, algoritmo);
                    await EnviarAsync(socket,
                        new { id, ok = true, assinatura = Convert.ToBase64String(assinatura) }, ct);
                    Log("Assinatura realizada.");
                    break;
                }
                case "post":
                {
                    var url = raiz.GetProperty("url").GetString()!;
                    var envelope = raiz.GetProperty("envelope").GetString()!;
                    var (status, corpo) = await _soap.PostAsync(url, envelope, ct);
                    await EnviarAsync(socket, new { id, ok = true, status, corpo }, ct);
                    Log($"POST {new Uri(url).Host} -> {status}");
                    break;
                }
                default:
                    await EnviarAsync(socket, new { id, ok = false, erro = "Operacao desconhecida." }, ct);
                    break;
            }
        }
        catch (Exception ex) when (id is not null)
        {
            Log($"Falha na operacao: {ex.Message}");
            try { await EnviarAsync(socket, new { id, ok = false, erro = ex.Message }, ct); }
            catch { /* conexao caiu */ }
        }
        catch (Exception ex)
        {
            Log($"Mensagem ignorada: {ex.Message}");
        }
    }

    private async Task EnviarAsync(ClientWebSocket socket, object mensagem, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mensagem);
        await _envio.WaitAsync(ct);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _envio.Release(); }
    }

    // Nunca registra PIN, hash nem o corpo dos XMLs.
    private static void Log(string mensagem) =>
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {mensagem}");

    public void Dispose()
    {
        _soap.Dispose();
        _token.Dispose();
    }
}
