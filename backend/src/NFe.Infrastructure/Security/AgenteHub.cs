using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NFe.Sefaz.Seguranca;

namespace NFe.Infrastructure.Security;

/// <summary>Situacao da conexao de um agente, para o painel.</summary>
public sealed record StatusConexaoAgente(
    Guid AgenteId, bool Conectado, DateTime? ConectadoEm, string? Versao);

/// <summary>
/// Ponto de encontro entre a API e os agentes A3. Cada agente abre uma conexao
/// WebSocket de SAIDA (o cliente nao abre porta). A API envia comandos
/// (assinar hash, POST mTLS) e recebe a resposta pelo mesmo canal.
///
/// O token A3 so executa uma operacao por vez, entao os comandos de cada
/// agente sao serializados.
/// </summary>
public sealed class AgenteHub : IProvedorAgenteCertificado
{
    private const int TamanhoMaximoMensagem = 8 * 1024 * 1024;
    private static readonly TimeSpan IntervaloPing = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan TimeoutAssinatura = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TimeoutPost = TimeSpan.FromSeconds(75);

    private readonly ConcurrentDictionary<Guid, ConexaoAgente> _porCertificado = new();
    private readonly ILogger<AgenteHub> _log;

    public AgenteHub(ILogger<AgenteHub> log) => _log = log;

    public IAgenteCertificado ObterAgente(Guid empresaId, Guid certificadoId)
    {
        if (_porCertificado.TryGetValue(certificadoId, out var conexao)
            && conexao.EmpresaId == empresaId
            && conexao.Aberta)
            return new CanalAgente(conexao);

        throw new AgenteCertificadoException(
            "Agente do certificado A3 desconectado. Verifique se a maquina com o token " +
            "esta ligada, com o token plugado e o agente em execucao.");
    }

    public StatusConexaoAgente? Status(Guid certificadoId) =>
        _porCertificado.TryGetValue(certificadoId, out var c) && c.Aberta
            ? new StatusConexaoAgente(c.AgenteId, true, c.ConectadoEm, c.Versao)
            : null;

    /// <summary>Derruba a conexao (revogacao ou novo pareamento).</summary>
    public void Desconectar(Guid certificadoId)
    {
        if (_porCertificado.TryRemove(certificadoId, out var c))
            c.Encerrar("Agente revogado.");
    }

    /// <summary>
    /// Atende um agente ja autenticado ate a conexao cair. Retorna so quando o
    /// WebSocket fecha. <paramref name="aoContato"/> e chamado a cada sinal de vida
    /// e devolve false se o agente nao deve mais ficar conectado (revogado).
    /// </summary>
    public async Task AtenderAsync(
        WebSocket socket,
        Guid agenteId,
        Guid empresaId,
        Guid certificadoId,
        string? versao,
        Func<CancellationToken, Task<bool>> aoContato,
        CancellationToken ct)
    {
        var conexao = new ConexaoAgente(socket, agenteId, empresaId, certificadoId, versao);

        // Uma nova conexao do mesmo certificado substitui a anterior.
        if (_porCertificado.TryGetValue(certificadoId, out var antiga))
            antiga.Encerrar("Substituido por uma nova conexao do agente.");
        _porCertificado[certificadoId] = conexao;

        _log.LogInformation("Agente A3 {Agente} conectado (certificado {Cert}, v{Versao}).",
            agenteId, certificadoId, versao);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = ExecutarPingAsync(conexao, aoContato, cts);
        try
        {
            await conexao.LerAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogInformation("Agente A3 {Agente} desconectou: {Msg}", agenteId, ex.Message);
        }
        finally
        {
            cts.Cancel();
            conexao.Encerrar("Conexao com o agente encerrada.");
            // So remove se ainda for esta conexao (pode ja ter sido substituida).
            _porCertificado.TryRemove(
                new KeyValuePair<Guid, ConexaoAgente>(certificadoId, conexao));
            try { await ping; } catch { /* encerrando */ }
            _log.LogInformation("Agente A3 {Agente} desconectado.", agenteId);
        }
    }

    private static async Task ExecutarPingAsync(
        ConexaoAgente conexao,
        Func<CancellationToken, Task<bool>> aoContato,
        CancellationTokenSource cts)
    {
        try
        {
            using var timer = new PeriodicTimer(IntervaloPing);
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                if (!await aoContato(cts.Token))
                {
                    conexao.Encerrar("Agente revogado.");
                    cts.Cancel();
                    return;
                }

                await conexao.EnviarAsync(new { tipo = "ping" }, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            cts.Cancel();
        }
    }

    // ------------------------------------------------------------------

    private sealed class CanalAgente : IAgenteCertificado
    {
        private readonly ConexaoAgente _conexao;
        public CanalAgente(ConexaoAgente conexao) => _conexao = conexao;

        public async Task<byte[]> AssinarHashAsync(
            byte[] hash, HashAlgorithmName algoritmo, CancellationToken ct = default)
        {
            var r = await _conexao.ExecutarAsync(
                new
                {
                    op = "assinar",
                    hash = Convert.ToBase64String(hash),
                    algoritmo = algoritmo.Name,
                },
                TimeoutAssinatura, ct);

            var b64 = r.GetProperty("assinatura").GetString()
                ?? throw new AgenteCertificadoException("O agente nao devolveu a assinatura.");
            return Convert.FromBase64String(b64);
        }

        public async Task<RespostaTransporteSoap> PostSoapAsync(
            string url, string envelope, CancellationToken ct = default)
        {
            var r = await _conexao.ExecutarAsync(
                new { op = "post", url, envelope }, TimeoutPost, ct);

            var status = (HttpStatusCode)r.GetProperty("status").GetInt32();
            var corpo = r.TryGetProperty("corpo", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            return new RespostaTransporteSoap(status, corpo);
        }
    }

    private sealed class ConexaoAgente
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _fila = new(1, 1);
        private readonly SemaphoreSlim _envio = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendentes = new();
        private volatile bool _encerrada;

        public Guid AgenteId { get; }
        public Guid EmpresaId { get; }
        public Guid CertificadoId { get; }
        public string? Versao { get; }
        public DateTime ConectadoEm { get; } = DateTime.UtcNow;

        public bool Aberta => !_encerrada && _socket.State == WebSocketState.Open;

        public ConexaoAgente(
            WebSocket socket, Guid agenteId, Guid empresaId, Guid certificadoId, string? versao)
        {
            _socket = socket;
            AgenteId = agenteId;
            EmpresaId = empresaId;
            CertificadoId = certificadoId;
            Versao = versao;
        }

        public async Task<JsonElement> ExecutarAsync(
            object comando, TimeSpan timeout, CancellationToken ct)
        {
            if (!Aberta)
                throw new AgenteCertificadoException("Agente do certificado A3 desconectado.");

            // O token executa uma operacao por vez: quem chega depois espera.
            if (!await _fila.WaitAsync(timeout, ct))
                throw new AgenteCertificadoException(
                    "Agente do certificado A3 ocupado. Tente novamente em instantes.");

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendentes[id] = tcs;

            try
            {
                await EnviarBytesAsync(MontarComando(id, comando), ct);

                using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limite.CancelAfter(timeout);
                try
                {
                    return await tcs.Task.WaitAsync(limite.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new AgenteCertificadoException(
                        $"O agente do certificado A3 nao respondeu em {timeout.TotalSeconds:0}s.");
                }
            }
            catch (WebSocketException ex)
            {
                throw new AgenteCertificadoException(
                    "Falha de comunicacao com o agente do certificado A3.", ex);
            }
            finally
            {
                _pendentes.TryRemove(id, out _);
                _fila.Release();
            }
        }

        public Task EnviarAsync(object mensagem, CancellationToken ct) =>
            EnviarBytesAsync(JsonSerializer.SerializeToUtf8Bytes(mensagem), ct);

        /// <summary>Une o id de correlacao aos campos do comando num unico objeto plano.</summary>
        private static byte[] MontarComando(string id, object comando)
        {
            var campos = JsonSerializer.SerializeToElement(comando);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                foreach (var p in campos.EnumerateObject()) p.WriteTo(w);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }

        private async Task EnviarBytesAsync(byte[] bytes, CancellationToken ct)
        {
            await _envio.WaitAsync(ct);
            try
            {
                if (_socket.State != WebSocketState.Open)
                    throw new AgenteCertificadoException("Agente do certificado A3 desconectado.");

                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _envio.Release();
            }
        }

        public async Task LerAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var acumulado = new MemoryStream();

            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                acumulado.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;

                    acumulado.Write(buffer, 0, r.Count);
                    if (acumulado.Length > TamanhoMaximoMensagem)
                    {
                        await _socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig, "Mensagem grande demais.", CancellationToken.None);
                        return;
                    }
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;

                TratarMensagem(acumulado.ToArray());
            }
        }

        private void TratarMensagem(byte[] bytes)
        {
            JsonElement raiz;
            try { raiz = JsonDocument.Parse(bytes).RootElement.Clone(); }
            catch (JsonException) { return; }

            if (!raiz.TryGetProperty("id", out var idProp) || idProp.GetString() is not { } id)
                return; // pong e outros sinais de vida nao precisam de tratamento

            if (!_pendentes.TryGetValue(id, out var tcs)) return;

            var ok = raiz.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (ok)
            {
                tcs.TrySetResult(raiz);
                return;
            }

            var erro = raiz.TryGetProperty("erro", out var e) ? e.GetString() : null;
            tcs.TrySetException(new AgenteCertificadoException(
                string.IsNullOrWhiteSpace(erro) ? "O agente recusou a operacao." : erro));
        }

        public void Encerrar(string motivo)
        {
            if (_encerrada) return;
            _encerrada = true;

            foreach (var p in _pendentes.Values)
                p.TrySetException(new AgenteCertificadoException(motivo));

            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    _ = _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, motivo, CancellationToken.None);
            }
            catch { /* conexao ja caiu */ }
        }
    }
}
