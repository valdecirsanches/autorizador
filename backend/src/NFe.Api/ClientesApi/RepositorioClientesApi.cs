using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NFe.Infrastructure.Data;

namespace NFe.Api.ClientesApi;

/// <summary>
/// Persistencia dos apps (clientes da API) e das empresas liberadas para cada um.
///
/// Usa SQL direto nas tabelas <c>api_cliente</c> e <c>api_cliente_empresa</c>,
/// criadas de forma aditiva e idempotente no startup (mesmo padrao da tabela
/// api_request_log). Assim nao ha migration do EF nem alteracao no
/// NFeDbContext: o modelo das notas fica intocado.
///
/// A busca por chave (feita em TODA requisicao de app) passa por um cache em
/// memoria de 60 s. Qualquer alteracao feita pelos endpoints de gestao limpa o
/// cache na hora, entao revogar uma chave vale imediatamente neste processo.
/// </summary>
public sealed class RepositorioClientesApi(
    IServiceScopeFactory scopeFactory,
    ILogger<RepositorioClientesApi> logger)
{
    private static readonly TimeSpan ValidadeCachePositivo = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ValidadeCacheNegativo = TimeSpan.FromSeconds(15);
    private const int LimiteEntradasCache = 10_000;

    private readonly ConcurrentDictionary<string, (ClienteApi? Cliente, DateTime ExpiraEm)> _cache = new();

    // ------------------------------------------------------------------ schema

    public static async Task EnsureSchemaAsync(NFeDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS `api_cliente` (
                `id` CHAR(36) NOT NULL,
                `nome` VARCHAR(120) NOT NULL,
                `identificador_chave` VARCHAR(20) NOT NULL,
                `hash_chave` CHAR(64) NOT NULL,
                `pode_cadastrar_empresas` TINYINT(1) NOT NULL DEFAULT 0,
                `ativo` TINYINT(1) NOT NULL DEFAULT 1,
                `criado_em` DATETIME(3) NOT NULL,
                `atualizado_em` DATETIME(3) NULL,
                `ultimo_uso_em` DATETIME(3) NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `ux_api_cliente_hash_chave` (`hash_chave`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS `api_cliente_empresa` (
                `cliente_id` CHAR(36) NOT NULL,
                `empresa_id` CHAR(36) NOT NULL,
                `concedido_em` DATETIME(3) NOT NULL,
                PRIMARY KEY (`cliente_id`, `empresa_id`),
                KEY `ix_api_cliente_empresa_empresa` (`empresa_id`),
                CONSTRAINT `fk_api_cliente_empresa_cliente` FOREIGN KEY (`cliente_id`)
                    REFERENCES `api_cliente` (`id`) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """, ct);
    }

    // ---------------------------------------------------- autenticacao (quente)

    /// <summary>
    /// Localiza o app dono da chave. Retorna null se a chave nao existe.
    /// Clientes desativados SAO retornados (com Ativo=false) para a
    /// autenticacao poder explicar o 401.
    /// </summary>
    public async Task<ClienteApi?> BuscarPorChaveAsync(string chave, CancellationToken ct)
    {
        var hash = ChaveApi.Hash(chave);
        var agora = DateTime.UtcNow;

        if (_cache.TryGetValue(hash, out var entrada) && entrada.ExpiraEm > agora)
            return entrada.Cliente;

        if (_cache.Count > LimiteEntradasCache)
            _cache.Clear(); // protege contra enxurrada de chaves invalidas.

        await using var con = await AbrirAsync(ct);
        ClienteApi? cliente;
        await using (var cmd = con.Connection.CreateCommand())
        {
            cmd.CommandText = SelectCliente + " WHERE `hash_chave` = @hash LIMIT 1";
            Param(cmd, "@hash", hash);
            cliente = await LerUmAsync(con.Connection, cmd, ct);
        }

        if (cliente is { Ativo: true })
        {
            // Atualizado so quando o cache expira: no maximo ~1 escrita/min por app.
            await using var upd = con.Connection.CreateCommand();
            upd.CommandText = "UPDATE `api_cliente` SET `ultimo_uso_em` = @agora WHERE `id` = @id";
            Param(upd, "@agora", agora);
            Param(upd, "@id", cliente.Id.ToString());
            await upd.ExecuteNonQueryAsync(ct);
        }

        _cache[hash] = (cliente, agora + (cliente is null ? ValidadeCacheNegativo : ValidadeCachePositivo));
        return cliente;
    }

    public void LimparCache() => _cache.Clear();

    // ------------------------------------------------------------- gestao

    public async Task<IReadOnlyList<ClienteApi>> ListarAsync(CancellationToken ct)
    {
        await using var con = await AbrirAsync(ct);

        var empresas = new Dictionary<Guid, HashSet<Guid>>();
        await using (var cmdEmp = con.Connection.CreateCommand())
        {
            cmdEmp.CommandText = "SELECT `cliente_id`, `empresa_id` FROM `api_cliente_empresa`";
            await using var r = await cmdEmp.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var cli = LerGuid(r, 0);
                if (!empresas.TryGetValue(cli, out var set))
                    empresas[cli] = set = new HashSet<Guid>();
                set.Add(LerGuid(r, 1));
            }
        }

        var lista = new List<ClienteApi>();
        await using (var cmd = con.Connection.CreateCommand())
        {
            cmd.CommandText = SelectCliente + " ORDER BY `nome`";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = LerGuid(r, 0);
                lista.Add(Mapear(r, empresas.GetValueOrDefault(id) ?? new HashSet<Guid>()));
            }
        }
        return lista;
    }

    public async Task<ClienteApi?> ObterAsync(Guid id, CancellationToken ct)
    {
        await using var con = await AbrirAsync(ct);
        await using var cmd = con.Connection.CreateCommand();
        cmd.CommandText = SelectCliente + " WHERE `id` = @id LIMIT 1";
        Param(cmd, "@id", id.ToString());
        return await LerUmAsync(con.Connection, cmd, ct);
    }

    public async Task<(ClienteApi Cliente, string Chave)> CriarAsync(
        string nome, bool podeCadastrarEmpresas, IReadOnlyCollection<Guid> empresas, CancellationToken ct)
    {
        var (chave, identificador, hash) = ChaveApi.Gerar();
        var id = Guid.NewGuid();
        var agora = DateTime.UtcNow;

        await using var con = await AbrirAsync(ct);
        await using var tx = await con.Connection.BeginTransactionAsync(ct);

        await using (var cmd = con.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO `api_cliente`
                    (`id`, `nome`, `identificador_chave`, `hash_chave`, `pode_cadastrar_empresas`, `ativo`, `criado_em`)
                VALUES (@id, @nome, @ident, @hash, @pode, 1, @agora)
                """;
            Param(cmd, "@id", id.ToString());
            Param(cmd, "@nome", nome);
            Param(cmd, "@ident", identificador);
            Param(cmd, "@hash", hash);
            Param(cmd, "@pode", podeCadastrarEmpresas ? 1 : 0);
            Param(cmd, "@agora", agora);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await SubstituirEmpresasAsync(con.Connection, tx, id, empresas, agora, ct);
        await tx.CommitAsync(ct);
        LimparCache();

        logger.LogInformation("Cliente da API criado: {Nome} ({Id}), chave {Identificador}...",
            nome, id, identificador);

        var cliente = new ClienteApi(id, nome, identificador, podeCadastrarEmpresas, true,
            empresas.ToHashSet(), agora, null, null);
        return (cliente, chave);
    }

    /// <summary>
    /// Atualiza os campos informados (null = manter). <paramref name="empresas"/>
    /// substitui a lista inteira quando informado. Retorna null se nao existe.
    /// </summary>
    public async Task<ClienteApi?> AtualizarAsync(
        Guid id, string? nome, bool? podeCadastrarEmpresas, bool? ativo,
        IReadOnlyCollection<Guid>? empresas, CancellationToken ct)
    {
        var agora = DateTime.UtcNow;
        await using var con = await AbrirAsync(ct);
        await using var tx = await con.Connection.BeginTransactionAsync(ct);

        await using (var cmd = con.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE `api_cliente`
                SET `nome` = COALESCE(@nome, `nome`),
                    `pode_cadastrar_empresas` = COALESCE(@pode, `pode_cadastrar_empresas`),
                    `ativo` = COALESCE(@ativo, `ativo`),
                    `atualizado_em` = @agora
                WHERE `id` = @id
                """;
            Param(cmd, "@nome", (object?)nome ?? DBNull.Value);
            Param(cmd, "@pode", podeCadastrarEmpresas is null ? DBNull.Value : (object)(podeCadastrarEmpresas.Value ? 1 : 0));
            Param(cmd, "@ativo", ativo is null ? DBNull.Value : (object)(ativo.Value ? 1 : 0));
            Param(cmd, "@agora", agora);
            Param(cmd, "@id", id.ToString());
            // MySQL devolve "linhas encontradas" por padrao no MySqlConnector
            // (UseAffectedRows=false), entao 0 significa que o id nao existe.
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                return null;
        }

        if (empresas is not null)
            await SubstituirEmpresasAsync(con.Connection, tx, id, empresas, agora, ct);

        await tx.CommitAsync(ct);
        LimparCache();
        return await ObterAsync(id, ct);
    }

    public async Task<(ClienteApi Cliente, string Chave)?> GerarNovaChaveAsync(Guid id, CancellationToken ct)
    {
        var (chave, identificador, hash) = ChaveApi.Gerar();
        await using (var con = await AbrirAsync(ct))
        await using (var cmd = con.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE `api_cliente`
                SET `identificador_chave` = @ident, `hash_chave` = @hash, `atualizado_em` = @agora
                WHERE `id` = @id
                """;
            Param(cmd, "@ident", identificador);
            Param(cmd, "@hash", hash);
            Param(cmd, "@agora", DateTime.UtcNow);
            Param(cmd, "@id", id.ToString());
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                return null;
        }

        LimparCache();
        logger.LogInformation("Chave do cliente da API {Id} substituida; a anterior deixou de valer.", id);
        var cliente = await ObterAsync(id, ct);
        return cliente is null ? null : (cliente, chave);
    }

    public async Task<bool> ExcluirAsync(Guid id, CancellationToken ct)
    {
        await using var con = await AbrirAsync(ct);
        await using var cmd = con.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM `api_cliente` WHERE `id` = @id"; // empresas saem em cascata.
        Param(cmd, "@id", id.ToString());
        var removido = await cmd.ExecuteNonQueryAsync(ct) > 0;
        LimparCache();
        return removido;
    }

    /// <summary>Libera uma empresa para o app (idempotente).</summary>
    public async Task ConcederEmpresaAsync(Guid clienteId, Guid empresaId, CancellationToken ct)
    {
        await using var con = await AbrirAsync(ct);
        await using var cmd = con.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT IGNORE INTO `api_cliente_empresa` (`cliente_id`, `empresa_id`, `concedido_em`)
            VALUES (@cli, @emp, @agora)
            """;
        Param(cmd, "@cli", clienteId.ToString());
        Param(cmd, "@emp", empresaId.ToString());
        Param(cmd, "@agora", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
        LimparCache();
    }

    // ------------------------------------------------------------- internos

    private const string SelectCliente = """
        SELECT `id`, `nome`, `identificador_chave`, `pode_cadastrar_empresas`, `ativo`,
               `criado_em`, `atualizado_em`, `ultimo_uso_em`
        FROM `api_cliente`
        """;

    private static async Task SubstituirEmpresasAsync(
        DbConnection con, DbTransaction tx, Guid clienteId,
        IReadOnlyCollection<Guid> empresas, DateTime agora, CancellationToken ct)
    {
        await using (var del = con.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM `api_cliente_empresa` WHERE `cliente_id` = @cli";
            Param(del, "@cli", clienteId.ToString());
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var empresaId in empresas.Distinct())
        {
            await using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO `api_cliente_empresa` (`cliente_id`, `empresa_id`, `concedido_em`)
                VALUES (@cli, @emp, @agora)
                """;
            Param(ins, "@cli", clienteId.ToString());
            Param(ins, "@emp", empresaId.ToString());
            Param(ins, "@agora", agora);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<ClienteApi?> LerUmAsync(DbConnection con, DbCommand cmd, CancellationToken ct)
    {
        Guid id;
        Func<IReadOnlySet<Guid>, ClienteApi> montar;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
                return null;
            id = LerGuid(r, 0);
            var nome = r.GetString(1);
            var ident = r.GetString(2);
            var pode = r.GetBoolean(3);
            var ativo = r.GetBoolean(4);
            var criado = Utc(r.GetDateTime(5));
            DateTime? atualizado = r.IsDBNull(6) ? null : Utc(r.GetDateTime(6));
            DateTime? uso = r.IsDBNull(7) ? null : Utc(r.GetDateTime(7));
            montar = emp => new ClienteApi(id, nome, ident, pode, ativo, emp, criado, atualizado, uso);
        }

        var empresas = new HashSet<Guid>();
        await using (var cmdEmp = con.CreateCommand())
        {
            cmdEmp.CommandText = "SELECT `empresa_id` FROM `api_cliente_empresa` WHERE `cliente_id` = @cli";
            Param(cmdEmp, "@cli", id.ToString());
            await using var r = await cmdEmp.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                empresas.Add(LerGuid(r, 0));
        }
        return montar(empresas);
    }

    private static ClienteApi Mapear(DbDataReader r, IReadOnlySet<Guid> empresas) => new(
        LerGuid(r, 0),
        r.GetString(1),
        r.GetString(2),
        r.GetBoolean(3),
        r.GetBoolean(4),
        empresas,
        Utc(r.GetDateTime(5)),
        r.IsDBNull(6) ? null : Utc(r.GetDateTime(6)),
        r.IsDBNull(7) ? null : Utc(r.GetDateTime(7)));

    // Com GuidFormat padrao do MySqlConnector, CHAR(36) chega como Guid; com
    // outros formatos chega como string. Aceita os dois.
    private static Guid LerGuid(DbDataReader r, int i) =>
        r.GetValue(i) is Guid g ? g : Guid.Parse(Convert.ToString(r.GetValue(i))!);

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static void Param(DbCommand cmd, string nome, object valor)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = nome;
        p.Value = valor;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Abre uma conexao propria (escopo novo), independente do DbContext da
    /// requisicao. O escopo e descartado junto com a conexao.
    /// </summary>
    private async Task<ConexaoEscopo> AbrirAsync(CancellationToken ct)
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();
            await db.Database.OpenConnectionAsync(ct);
            return new ConexaoEscopo(scope, db.Database.GetDbConnection());
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class ConexaoEscopo(AsyncServiceScope scope, DbConnection connection) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;
        public ValueTask DisposeAsync() => scope.DisposeAsync(); // fecha a conexao com o DbContext.
    }
}
