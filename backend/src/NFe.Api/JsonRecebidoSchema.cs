using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NFe.Infrastructure.Data;

namespace NFe.Api;

internal static class JsonRecebidoSchema
{
    private const string MigrationId = "20260930120000_JsonRecebidoTentativas";

    public static async Task EnsureAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<NFeDbContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

        await using var db = new NFeDbContext(options);
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();

        var database = await ScalarAsync(connection, "SELECT DATABASE()");
        Console.WriteLine($"[schema] Banco conectado: {database}");

        var historyExists = await TableExistsAsync(connection, "__EFMigrationsHistory");
        var nfeTableExists = await TableExistsAsync(connection, "tentativa_emissao");
        var nfseTableExists = await TableExistsAsync(connection, "tentativa_emissao_nfse");

        if (!nfeTableExists)
        {
            if (!historyExists)
            {
                Console.WriteLine(
                    "[schema] Banco novo sem tabelas/histórico EF; as migrations "
                    + "iniciais cuidarão do schema.");
                return;
            }

            throw new InvalidOperationException(
                "A tabela tentativa_emissao não existe no banco conectado; "
                + "não foi possível preparar o campo json_recebido.");
        }

        await EnsureColumnAsync(connection, "tentativa_emissao");

        if (nfseTableExists)
            await EnsureColumnAsync(connection, "tentativa_emissao_nfse");

        if (!historyExists)
        {
            Console.WriteLine(
                "[schema] Coluna NF-e preparada. Banco sem histórico EF; "
                + "nenhum registro de migration foi inserido.");
        }
        else if (nfseTableExists)
        {
            await RecordMigrationAsync(connection);
        }
        else
        {
            Console.WriteLine(
                "[schema] tentativa_emissao_nfse não existe; migration EF não registrada.");
        }
    }

    private static async Task EnsureColumnAsync(DbConnection connection, string table)
    {
        if (await ColumnExistsAsync(connection, table))
        {
            Console.WriteLine($"[schema] {table}.json_recebido já existe.");
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `json_recebido` LONGTEXT NULL";
        command.CommandTimeout = 120;

        try
        {
            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"[schema] Campo {table}.json_recebido criado.");
        }
        catch (DbException)
        {
            // Outro servidor pode estar aplicando a mesma alteração em paralelo.
            if (!await ColumnExistsAsync(connection, table))
                throw;

            Console.WriteLine($"[schema] {table}.json_recebido foi criado em paralelo.");
        }
    }

    private static async Task RecordMigrationAsync(DbConnection connection)
    {
        if (await MigrationExistsAsync(connection))
        {
            Console.WriteLine($"[schema] Migration {MigrationId} já registrada.");
            return;
        }

        var productVersion = await ScalarAsync(
            connection,
            "SELECT `ProductVersion` FROM `__EFMigrationsHistory` "
            + "ORDER BY `MigrationId` DESC LIMIT 1") ?? "9.0.0";

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) "
            + "VALUES (@migrationId, @productVersion)";
        AddParameter(command, "@migrationId", MigrationId);
        AddParameter(command, "@productVersion", productVersion);
        await command.ExecuteNonQueryAsync();

        Console.WriteLine(
            $"[schema] Migration {MigrationId} registrada após confirmar as duas colunas.");
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM `information_schema`.`tables` "
            + "WHERE `table_schema` = DATABASE() AND `table_name` = @tableName";
        AddParameter(command, "@tableName", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM `information_schema`.`columns` "
            + "WHERE `table_schema` = DATABASE() AND `table_name` = @tableName "
            + "AND `column_name` = 'json_recebido'";
        AddParameter(command, "@tableName", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> MigrationExistsAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE `MigrationId` = @migrationId";
        AddParameter(command, "@migrationId", MigrationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<string?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

