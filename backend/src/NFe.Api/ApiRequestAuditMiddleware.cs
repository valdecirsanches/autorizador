using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NFe.Infrastructure.Data;

namespace NFe.Api;

public sealed class ApiRequestAuditMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopeFactory,
    ILogger<ApiRequestAuditMiddleware> logger)
{
    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxStoredChars = 32 * 1024;
    private const int SlowReadThresholdMs = 2_000;
    private const int StatusEmProcessamento = 102;

    public async Task InvokeAsync(HttpContext context)
    {
        (string? Body, bool Truncated) requestCapture;
        try
        {
            requestCapture = await ReadRequestBodyAsync(context);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Não foi possível capturar o corpo da requisição {Metodo} {Rota}",
                context.Request.Method, context.Request.Path);
            if (context.Request.Body.CanSeek)
                context.Request.Body.Position = 0;
            requestCapture = ("[Corpo não capturado]", true);
        }

        var safeRoute = BuildSafeRoute(context.Request);
        var requestPath = context.Request.Path.Value ?? "/";
        var method = SafeHeader(context.Request.Method, 10) ?? "UNKNOWN";
        var user = SafeHeader(context.Request.Headers["X-Panel-User"].ToString(), 120)
            ?? (string.IsNullOrWhiteSpace(context.Request.Headers.Authorization)
                ? "Não autenticado"
                : "Cliente da API");
        var ip = SafeHeader(context.Request.Headers["X-Panel-Client-IP"].ToString(), 64)
            ?? context.Connection.RemoteIpAddress?.ToString();

        var started = Stopwatch.GetTimestamp();
        var truncated = requestCapture.Truncated;
        var originalBody = context.Response.Body;
        var capture = new LimitedCaptureStream(originalBody, MaxBodyBytes);
        context.Response.Body = capture;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
            var elapsed = (int)Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue);
            var status = context.Response.StatusCode;
            var responseContentType = context.Response.ContentType ?? "";
            truncated |= capture.WasTruncated;
            var responseBody = SanitizeResponse(capture.CapturedBytes, responseContentType, ref truncated);

            if (ShouldPersistLog(method, requestPath, status, elapsed))
            {
                // Requisicao feita com chave de app: registra o nome do app (que
                // vem do servidor) em vez do X-Panel-User (que o cliente informa).
                var usuarioLog = NFe.Api.ClientesApi.Chamador.Registrado(context)?.NomeAuditoria ?? user;
                try
                {
                    var logId = await StartLogAsync(
                        method, safeRoute, usuarioLog, ip, requestCapture.Body, truncated);
                    await CompleteLogAsync(
                        logId, status, elapsed, responseBody, responseContentType, truncated);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Não foi possível armazenar o log relevante de {Metodo} {Rota} (HTTP {Status}).",
                        method, safeRoute, status);
                }
            }
        }
    }

    internal static bool ShouldPersistLog(string method, string route, int status, int durationMs)
    {
        if (route.StartsWith("/api/monitor/", StringComparison.OrdinalIgnoreCase)) return false;
        if (status >= StatusCodes.Status400BadRequest) return true;
        if (durationMs >= SlowReadThresholdMs) return true;
        return !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method);
    }

    private async Task<long> StartLogAsync(
        string method,
        string route,
        string user,
        string? ip,
        string? requestBody,
        bool truncated)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();
        await db.Database.OpenConnectionAsync(CancellationToken.None);
        var connection = db.Database.GetDbConnection();

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO `api_request_log`
                    (`ocorrido_em`, `usuario`, `ip`, `metodo`, `rota`, `status_http`, `duracao_ms`,
                     `requisicao_json`, `resposta`, `content_type`, `truncado`)
                VALUES
                    (@ocorridoEm, @usuario, @ip, @metodo, @rota, @status, 0,
                     @requestBody, NULL, NULL, @truncado)
                """;
            AddParameter(insert, "@ocorridoEm", DateTime.UtcNow);
            AddParameter(insert, "@usuario", user);
            AddParameter(insert, "@ip", (object?)ip ?? DBNull.Value);
            AddParameter(insert, "@metodo", method);
            AddParameter(insert, "@rota", route);
            AddParameter(insert, "@status", StatusEmProcessamento);
            AddParameter(insert, "@requestBody", (object?)requestBody ?? DBNull.Value);
            AddParameter(insert, "@truncado", truncated ? 1 : 0);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var identity = connection.CreateCommand();
        identity.CommandText = "SELECT LAST_INSERT_ID()";
        var result = await identity.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task CompleteLogAsync(
        long id,
        int status,
        int durationMs,
        string? responseBody,
        string contentType,
        bool truncated)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();
        await db.Database.OpenConnectionAsync(CancellationToken.None);
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE `api_request_log`
            SET `status_http` = @status, `duracao_ms` = @duracaoMs, `resposta` = @responseBody,
                `content_type` = @contentType, `truncado` = @truncado
            WHERE `id` = @id
            """;
        AddParameter(command, "@status", status);
        AddParameter(command, "@duracaoMs", durationMs);
        AddParameter(command, "@responseBody", (object?)responseBody ?? DBNull.Value);
        AddParameter(command, "@contentType", (object?)SafeHeader(contentType, 160) ?? DBNull.Value);
        AddParameter(command, "@truncado", truncated ? 1 : 0);
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    public static async Task EnsureSchemaAsync(NFeDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS `api_request_log` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `ocorrido_em` DATETIME(3) NOT NULL,
                `usuario` VARCHAR(120) NOT NULL,
                `ip` VARCHAR(64) NULL,
                `metodo` VARCHAR(10) NOT NULL,
                `rota` LONGTEXT NOT NULL,
                `status_http` INT NOT NULL,
                `duracao_ms` INT NOT NULL,
                `requisicao_json` LONGTEXT NULL,
                `resposta` LONGTEXT NULL,
                `content_type` VARCHAR(160) NULL,
                `truncado` TINYINT(1) NOT NULL DEFAULT 0,
                PRIMARY KEY (`id`),
                KEY `ix_api_request_log_ocorrido_em` (`ocorrido_em`),
                KEY `ix_api_request_log_status_http` (`status_http`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE `api_request_log` MODIFY COLUMN `rota` LONGTEXT NOT NULL;",
            cancellationToken);
    }

    private static async Task<(string? Body, bool Truncated)> ReadRequestBodyAsync(HttpContext context)
    {
        var request = context.Request;
        var contentType = request.ContentType ?? "";
        if (request.ContentLength == 0 || string.IsNullOrWhiteSpace(contentType))
            return (null, false);

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return ("[Conteúdo multipart omitido por segurança]", false);

        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return ($"[Corpo omitido: {SafeHeader(contentType, 120) ?? "tipo desconhecido"}]", false);

        if (request.ContentLength is null)
            return ("[Corpo omitido: tamanho não informado]", false);
        if (request.ContentLength > MaxBodyBytes)
            return ("[Corpo omitido: excede o limite de captura de 32 KB]", true);

        request.EnableBuffering(bufferThreshold: MaxBodyBytes, bufferLimit: MaxBodyBytes + 1);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), context.RequestAborted);
            if (read == 0)
                break;
            total += read;
        }
        request.Body.Position = 0;

        if (total > MaxBodyBytes)
            return ("[Corpo omitido: excede o limite de captura de 32 KB]", true);

        var raw = Encoding.UTF8.GetString(buffer, 0, total);
        try
        {
            var node = JsonNode.Parse(raw);
            Redact(node);
            var truncated = false;
            var body = Limit(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", ref truncated);
            return (body, truncated);
        }
        catch (JsonException)
        {
            return ("[JSON inválido omitido por segurança]", false);
        }
    }

    private static string? SanitizeResponse(byte[] bytes, string contentType, ref bool truncated)
    {
        if (bytes.Length == 0)
            return null;

        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return $"[Resposta omitida por segurança: {SafeHeader(contentType, 120) ?? "tipo desconhecido"}]";

        var raw = Encoding.UTF8.GetString(bytes);
        try
        {
            var node = JsonNode.Parse(raw);
            Redact(node);
            return Limit(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", ref truncated);
        }
        catch (JsonException)
        {
            truncated = true;
            return "[Resposta JSON incompleta ou inválida]";
        }
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (SensitiveKey(property.Key))
                    obj[property.Key] = "[redigido]";
                else
                    Redact(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                Redact(item);
        }
    }

    private static bool SensitiveKey(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("senha", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("certificado", StringComparison.Ordinal)
            || normalized.Contains("certificate", StringComparison.Ordinal)
            || normalized.Contains("pfx", StringComparison.Ordinal)
            || normalized.Contains("base64", StringComparison.Ordinal)
            || normalized.Contains("cpf", StringComparison.Ordinal)
            || normalized.Contains("cnpj", StringComparison.Ordinal)
            || normalized.Contains("documento", StringComparison.Ordinal)
            || normalized.Contains("chaveautenticacao", StringComparison.Ordinal)
            || normalized == "xml";
    }

    internal static string BuildSafeRoute(HttpRequest request)
    {
        var scheme = string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";
        var host = request.Host.HasValue ? request.Host.ToUriComponent() : "localhost";
        var path = request.PathBase.Add(request.Path).ToUriComponent();
        var absoluteUrl = $"{scheme}://{host}{path}";
        var query = request.QueryString.Value;
        if (string.IsNullOrEmpty(query))
            return absoluteUrl;

        var safeQuery = string.Join("&", query[1..].Split('&').Select(part =>
        {
            var separator = part.IndexOf('=');
            var encodedKey = separator < 0 ? part : part[..separator];
            var key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
            return SensitiveKey(key)
                ? $"{encodedKey}=%5Bredigido%5D"
                : part;
        }));

        return $"{absoluteUrl}?{safeQuery}";
    }

    private static string? SafeHeader(string value, int maxLength)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
            return null;
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string Limit(string value, ref bool truncated)
    {
        if (value.Length <= MaxStoredChars)
            return value;
        truncated = true;
        return value[..MaxStoredChars] + "\n[conteúdo truncado]";
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class LimitedCaptureStream(Stream inner, int maxCaptureBytes) : Stream
    {
        private readonly MemoryStream captured = new();
        private long totalWritten;

        public byte[] CapturedBytes => captured.ToArray();
        public bool WasTruncated => totalWritten > maxCaptureBytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Capture(buffer.AsSpan(offset, count));
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        private void Capture(ReadOnlySpan<byte> buffer)
        {
            totalWritten += buffer.Length;
            var remaining = maxCaptureBytes - (int)captured.Length;
            if (remaining > 0)
                captured.Write(buffer[..Math.Min(remaining, buffer.Length)]);
        }
    }
}

public static class ApiMonitorEndpoints
{
    public static void MapApiMonitor(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/monitor/logs", async (
            NFeDbContext db,
            int? limit,
            long? beforeId,
            CancellationToken cancellationToken) =>
        {
            var pageSize = Math.Clamp(limit ?? 100, 1, 250);
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = beforeId is null
                ? """
                  SELECT `id`, `ocorrido_em`, `usuario`, `ip`, `metodo`, `rota`, `status_http`,
                         `duracao_ms`, `requisicao_json`, `resposta`, `truncado`
                  FROM `api_request_log`
                  WHERE (`status_http` >= 400 OR `metodo` NOT IN ('GET', 'HEAD', 'OPTIONS') OR `duracao_ms` >= 2000)
                    AND `rota` NOT LIKE '/api/monitor/%'
                  ORDER BY `id` DESC
                  LIMIT @limite
                  """
                : """
                  SELECT `id`, `ocorrido_em`, `usuario`, `ip`, `metodo`, `rota`, `status_http`,
                         `duracao_ms`, `requisicao_json`, `resposta`, `truncado`
                  FROM `api_request_log`
                  WHERE (`status_http` >= 400 OR `metodo` NOT IN ('GET', 'HEAD', 'OPTIONS') OR `duracao_ms` >= 2000)
                    AND `rota` NOT LIKE '/api/monitor/%'
                    AND `id` < @beforeId
                  ORDER BY `id` DESC
                  LIMIT @limite
                  """;

            if (beforeId is not null)
            {
                var beforeParameter = command.CreateParameter();
                beforeParameter.ParameterName = "@beforeId";
                beforeParameter.Value = beforeId.Value;
                command.Parameters.Add(beforeParameter);
            }

            var limitParameter = command.CreateParameter();
            limitParameter.ParameterName = "@limite";
            limitParameter.Value = pageSize + 1;
            command.Parameters.Add(limitParameter);

            var logs = new List<object>(pageSize + 1);
            long? lastVisibleId = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt64(0);
                    if (logs.Count < pageSize) lastVisibleId = id;
                    var occurredAt = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                    logs.Add(new
                    {
                        id,
                        ocorridoEm = occurredAt,
                        usuario = reader.GetString(2),
                        ip = reader.IsDBNull(3) ? null : reader.GetString(3),
                        metodo = reader.GetString(4),
                        rota = reader.GetString(5),
                        statusHttp = reader.GetInt32(6),
                        duracaoMs = reader.GetInt32(7),
                        requisicaoJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                        resposta = reader.IsDBNull(9) ? null : reader.GetString(9),
                        truncado = reader.GetBoolean(10),
                    });
                }
            }

            var hasMore = logs.Count > pageSize;
            if (hasMore) logs.RemoveAt(logs.Count - 1);
            var nextCursor = hasMore ? lastVisibleId : null;
            return Results.Ok(new { registros = logs, proximoCursor = nextCursor });
        }).WithTags("Monitor da API").WithName("ListarLogsApi");
    }
}

internal static class ApiRequestAuditStartup
{
    public static async Task EnsureApiRequestAuditSchemaAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();
        await ApiRequestAuditMiddleware.EnsureSchemaAsync(db);
        app.Logger.LogInformation("Tabela de monitoramento da API verificada.");
    }
}

internal static class ApiRequestAuditExtensions
{
    public static Task UseApiRequestAuditAsync(this WebApplication app)
    {
        return app.EnsureApiRequestAuditSchemaAsync();
    }
}

public static class ApiRequestAuditMiddlewareExtensions
{
    public static IApplicationBuilder UseApiRequestAudit(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ApiRequestAuditMiddleware>();
    }
}
