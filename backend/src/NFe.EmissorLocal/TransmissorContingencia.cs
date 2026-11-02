using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NFe.EmissorLocal;

/// <summary>Resumo de um ciclo de transmissao.</summary>
public sealed record ResumoTransmissao(int Enviadas, int Autorizadas, int Rejeitadas, int SemConexao, string? Aviso);

/// <summary>
/// Envia as NFC-e pendentes a API central (POST /api/empresas/{id}/nfce/contingencia),
/// que transmite a SEFAZ e guarda a nota. Roda a cada IntervaloTransmissaoSegundos
/// e sob demanda (POST /api/contingencia/transmitir). Falha de rede ou de
/// autenticacao deixa a nota pendente para a proxima rodada; rejeicao e
/// denegacao sao finais.
/// </summary>
public sealed class TransmissorContingencia : BackgroundService
{
    private readonly OpcoesEmissorLocal _opcoes;
    private readonly FilaContingencia _fila;
    private readonly ILogger<TransmissorContingencia> _log;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _ciclo = new(1, 1);

    public TransmissorContingencia(OpcoesEmissorLocal opcoes, FilaContingencia fila, ILogger<TransmissorContingencia> log)
    {
        _opcoes = opcoes;
        _fila = fila;
        _log = log;
        if (!string.IsNullOrWhiteSpace(opcoes.ApiCentralUrl))
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(opcoes.ApiCentralUrl!.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(90)
            };
            if (!string.IsNullOrWhiteSpace(opcoes.ApiCentralToken))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", opcoes.ApiCentralToken);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("NFeEmissorLocal/1.0");
        }
    }

    public DateTimeOffset? UltimoCiclo { get; private set; }
    public string? UltimoAviso { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_http is null)
            _log.LogWarning("[EmissorLocal] ApiCentralUrl nao configurada: as notas ficam na fila sem transmissao.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TransmitirAgoraAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "[EmissorLocal] Falha no ciclo de transmissao."); }

            try { await Task.Delay(TimeSpan.FromSeconds(_opcoes.IntervaloTransmissaoSegundos), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<ResumoTransmissao> TransmitirAgoraAsync(CancellationToken ct)
    {
        if (_http is null)
            return new ResumoTransmissao(0, 0, 0, 0, "ApiCentralUrl nao configurada.");

        await _ciclo.WaitAsync(ct);
        try
        {
            int enviadas = 0, autorizadas = 0, rejeitadas = 0, semConexao = 0;
            foreach (var item in _fila.Listar().Where(i => i.Situacao == SituacaoFila.Pendente))
            {
                enviadas++;
                await TransmitirAsync(item, ct);
                _fila.Salvar(item);
                switch (item.Situacao)
                {
                    case SituacaoFila.Autorizada: autorizadas++; break;
                    case SituacaoFila.Rejeitada or SituacaoFila.Denegada: rejeitadas++; break;
                    default: semConexao++; break;
                }
            }
            UltimoCiclo = DateTimeOffset.Now;
            UltimoAviso = semConexao > 0 ? $"{semConexao} nota(s) sem resposta da API central; nova tentativa em breve." : null;
            return new ResumoTransmissao(enviadas, autorizadas, rejeitadas, semConexao, UltimoAviso);
        }
        finally
        {
            _ciclo.Release();
        }
    }

    private async Task TransmitirAsync(ItemFila item, CancellationToken ct)
    {
        item.Tentativas++;
        item.UltimaTentativa = DateTimeOffset.Now;
        HttpResponseMessage resp;
        string corpo;
        try
        {
            // Corpo com Content-Length (StringContent), aceito por qualquer proxy no caminho.
            var envio = JsonSerializer.Serialize(new { xmlBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Xml)) });
            using var conteudo = new StringContent(envio, Encoding.UTF8, "application/json");
            resp = await _http!.PostAsync($"api/empresas/{item.EmpresaId}/nfce/contingencia", conteudo, ct);
            corpo = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            item.UltimoErro = $"Sem conexao com a API central: {ex.Message}";
            return;
        }

        JsonElement? json = null;
        try { json = JsonDocument.Parse(corpo).RootElement.Clone(); }
        catch (JsonException) { /* resposta nao-JSON (proxy, pagina de erro) */ }

        string? Txt(string nome) =>
            json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.ToString() : null;

        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity:
            {
                item.DocumentoId = Guid.TryParse(Txt("id"), out var id) ? id : item.DocumentoId;
                item.Protocolo = Txt("protocolo") ?? item.Protocolo;
                item.CodigoStatus = Txt("codigoStatusRetorno");
                item.Motivo = Txt("motivoRetorno");
                var status = int.TryParse(Txt("status"), out var s) ? s : 0;
                var autorizado = json is { ValueKind: JsonValueKind.Object } jo
                    && jo.TryGetProperty("autorizado", out var a) && a.ValueKind == JsonValueKind.True;
                if (resp.StatusCode == HttpStatusCode.OK && autorizado)
                {
                    item.Situacao = SituacaoFila.Autorizada;
                    item.UltimoErro = null;
                    if (Txt("xmlBase64") is { } b64)
                        item.XmlAutorizado = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    _log.LogInformation("[EmissorLocal] NFC-e {Chave} autorizada (protocolo {Prot}).", item.Chave, item.Protocolo);
                }
                else if (status == 200)
                {
                    item.Situacao = SituacaoFila.Rejeitada;
                    item.UltimoErro = $"Rejeitada: {item.CodigoStatus} {item.Motivo}".Trim();
                    _log.LogWarning("[EmissorLocal] NFC-e {Chave} rejeitada: {Motivo}", item.Chave, item.UltimoErro);
                }
                else if (status == 300)
                {
                    item.Situacao = SituacaoFila.Denegada;
                    item.UltimoErro = $"Denegada: {item.CodigoStatus} {item.Motivo}".Trim();
                }
                else
                {
                    // ErroTransmissao (SEFAZ fora) ou outro estado nao final: tenta de novo.
                    item.UltimoErro = Txt("ultimoErro") ?? Txt("erro") ?? $"API central respondeu HTTP {(int)resp.StatusCode}.";
                }
                break;
            }
            case HttpStatusCode.BadRequest:
                // XML recusado pela API central: nao adianta repetir.
                item.Situacao = SituacaoFila.Rejeitada;
                item.UltimoErro = "API central recusou o XML: " + (Txt("problemas") ?? Txt("erro") ?? corpo);
                break;
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                item.UltimoErro = $"API central negou acesso (HTTP {(int)resp.StatusCode}): confira EmissorLocal:ApiCentralToken.";
                break;
            default:
                item.UltimoErro = $"API central respondeu HTTP {(int)resp.StatusCode}: " + Truncar(Txt("erro") ?? corpo, 300);
                break;
        }
    }

    private static string Truncar(string s, int max) => s.Length <= max ? s : s[..max];

    public override void Dispose()
    {
        _http?.Dispose();
        base.Dispose();
    }
}
