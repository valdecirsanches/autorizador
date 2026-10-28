using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace NFe.Infrastructure.Data;

public static class IdempotentMigrationApplier
{
    private static readonly HashSet<string> SupportedMigrations =
    [
        "20260902000000_ModuloNfse",
        "20260930000000_ProcessarNovaTentativaNfe",
        "20260930000001_ChaveAutenticacaoOsasco",
        "20261019000000_CertificadoA3",
        "20261027000000_PastaXmlBackupEmpresa",
    ];

    public static async Task ApplyPendingAsync(
        NFeDbContext db,
        IReadOnlyCollection<string> pendingMigrations,
        CancellationToken cancellationToken = default)
    {
        foreach (var migrationId in pendingMigrations)
        {
            if (!SupportedMigrations.Contains(migrationId))
            {
                await db.Database.MigrateAsync(migrationId, cancellationToken);
                continue;
            }

            await ApplyIdempotentlyAsync(db, migrationId, cancellationToken);
        }
    }

    private static async Task ApplyIdempotentlyAsync(
        NFeDbContext db,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var migration = migrationsAssembly.CreateMigration(
            migrationsAssembly.Migrations[migrationId],
            db.Database.ProviderName!);
        var operations = new List<MigrationOperation>();
        var deferredConstraints = new List<MigrationOperation>();
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        if (closeConnection)
            await connection.OpenAsync(cancellationToken);

        try
        {
            foreach (var operation in migration.UpOperations)
            {
                switch (operation)
                {
                    case CreateTableOperation createTable:
                        if (!await TableExistsAsync(
                                connection, createTable.Name, cancellationToken))
                        {
                            operations.Add(createTable);
                            continue;
                        }

                        await EnsureExistingTableHasColumnsAsync(
                            connection, createTable, cancellationToken);
                        await AddMissingTableConstraintsAsync(
                            connection, createTable, deferredConstraints, cancellationToken);
                        break;

                    case CreateIndexOperation createIndex:
                        var createIndexExisting = await ReadIndexAsync(
                            connection,
                            createIndex.Table,
                            createIndex.Name,
                            cancellationToken);
                        if (createIndexExisting is null)
                        {
                            operations.Add(createIndex);
                            break;
                        }

                        EnsureIndexMatches(
                            createIndex.Table,
                            createIndex.Name,
                            createIndex.Columns,
                            createIndex.IsUnique,
                            createIndexExisting);
                        break;

                    case DropIndexOperation dropIndex:
                        if (await ReadIndexAsync(
                                connection,
                                dropIndex.Table,
                                dropIndex.Name,
                                cancellationToken) is not null)
                            operations.Add(dropIndex);
                        break;

                    case AddColumnOperation addColumn:
                        var existingColumn = await ReadColumnAsync(
                            connection,
                            addColumn.Table,
                            addColumn.Name,
                            cancellationToken);
                        if (existingColumn is null)
                        {
                            operations.Add(addColumn);
                            break;
                        }

                        EnsureColumnMatches(addColumn, existingColumn);
                        break;

                    default:
                        operations.Add(operation);
                        break;
                }
            }

            operations.AddRange(deferredConstraints);
            var sqlCommands = db.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, migration.TargetModel);
            foreach (var sqlCommand in sqlCommands)
                await ExecuteAsync(connection, sqlCommand.CommandText, cancellationToken);

            var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(3)
                ?? "9.0.0";
            var historySql = db.GetService<IHistoryRepository>()
                .GetInsertScript(new HistoryRow(migrationId, productVersion));
            await ExecuteAsync(connection, historySql, cancellationToken);

            Console.WriteLine(
                $"[migration] {migrationId} aplicada; objetos já existentes foram "
                + "validados e preservados.");
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;
            """;
        AddParameter(command, "@table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task EnsureExistingTableHasColumnsAsync(
        DbConnection connection,
        CreateTableOperation table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;
            """;
        AddParameter(command, "@table", table.Name);
        var actual = new Dictionary<string, ColumnDefinition>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actual[reader.GetString(0)] = new ColumnDefinition(
                reader.GetString(1), reader.GetString(2) == "YES");

        foreach (var expected in table.Columns)
        {
            if (!actual.TryGetValue(expected.Name, out var existing))
                throw new InvalidOperationException(
                    $"A tabela '{table.Name}' já existe, mas a coluna "
                    + $"'{expected.Name}' não. A migration não foi marcada como aplicada.");

            var expectedType = expected.ColumnType ?? expected.ClrType.Name;
            if (!string.Equals(
                    existing.ColumnType, expectedType, StringComparison.OrdinalIgnoreCase)
                || existing.IsNullable != expected.IsNullable)
                throw new InvalidOperationException(
                    $"A coluna '{table.Name}.{expected.Name}' já existe com definição "
                    + "diferente da migration. A migration não foi ignorada.");
        }
    }

    private static async Task AddMissingTableConstraintsAsync(
        DbConnection connection,
        CreateTableOperation table,
        ICollection<MigrationOperation> deferredConstraints,
        CancellationToken cancellationToken)
    {
        if (table.PrimaryKey is not null)
        {
            var primaryKey = table.PrimaryKey;
            var existing = await ReadIndexAsync(
                connection, table.Name, "PRIMARY", cancellationToken);
            if (existing is null)
            {
                primaryKey.Table = table.Name;
                primaryKey.Schema = table.Schema;
                deferredConstraints.Add(primaryKey);
            }
            else
            {
                EnsureIndexMatches(
                    table.Name,
                    "PRIMARY",
                    primaryKey.Columns,
                    isUnique: true,
                    existing);
            }
        }

        foreach (var uniqueConstraint in table.UniqueConstraints)
        {
            var existing = await ReadIndexAsync(
                connection, table.Name, uniqueConstraint.Name, cancellationToken);
            if (existing is null)
            {
                uniqueConstraint.Table = table.Name;
                uniqueConstraint.Schema = table.Schema;
                deferredConstraints.Add(uniqueConstraint);
            }
            else
            {
                EnsureIndexMatches(
                    table.Name,
                    uniqueConstraint.Name,
                    uniqueConstraint.Columns,
                    isUnique: true,
                    existing);
            }
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            if (await ForeignKeyExistsAsync(
                    connection, table.Name, foreignKey.Name, cancellationToken))
                continue;

            foreignKey.Table = table.Name;
            foreignKey.Schema = table.Schema;
            deferredConstraints.Add(foreignKey);
        }
    }

    private static async Task<bool> ForeignKeyExistsAsync(
        DbConnection connection,
        string table,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
              AND CONSTRAINT_NAME = @name;
            """;
        AddParameter(command, "@table", table);
        AddParameter(command, "@name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<IndexDefinition?> ReadIndexAsync(
        DbConnection connection,
        string table,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, NON_UNIQUE
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
              AND INDEX_NAME = @name
            ORDER BY SEQ_IN_INDEX;
            """;
        AddParameter(command, "@table", table);
        AddParameter(command, "@name", name);

        var columns = new List<string>();
        bool? isUnique = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
            isUnique = reader.GetInt32(1) == 0;
        }

        return columns.Count == 0 ? null : new IndexDefinition(columns, isUnique == true);
    }

    private static void EnsureIndexMatches(
        string table,
        string name,
        IReadOnlyList<string> expectedColumns,
        bool isUnique,
        IndexDefinition actual)
    {
        if (actual.IsUnique != isUnique
            || !actual.Columns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O índice '{name}' da tabela '{table}' já existe com definição diferente. "
                + "A migration foi interrompida sem ignorar a divergência.");
    }

    private static async Task<ColumnDefinition?> ReadColumnAsync(
        DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_TYPE, IS_NULLABLE
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
              AND COLUMN_NAME = @column;
            """;
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ColumnDefinition(reader.GetString(0), reader.GetString(1) == "YES")
            : null;
    }

    private static void EnsureColumnMatches(
        AddColumnOperation expected,
        ColumnDefinition actual)
    {
        var expectedType = expected.ColumnType ?? expected.ClrType.Name;
        if (!string.Equals(actual.ColumnType, expectedType, StringComparison.OrdinalIgnoreCase)
            || actual.IsNullable != expected.IsNullable)
            throw new InvalidOperationException(
                $"A coluna '{expected.Table}.{expected.Name}' já existe com definição "
                + "diferente da migration; ela não foi ignorada.");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record IndexDefinition(IReadOnlyList<string> Columns, bool IsUnique);
    private sealed record ColumnDefinition(string ColumnType, bool IsNullable);
}
