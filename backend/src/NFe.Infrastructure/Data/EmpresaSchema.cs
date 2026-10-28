using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NFe.Infrastructure.Data;

/// <summary>
/// Garante, de forma aditiva e idempotente, as colunas da tabela empresa que
/// a API le em toda consulta. Evita que a listagem de empresas quebre quando a
/// API sobe antes de a migration correspondente ser aplicada no banco.
/// </summary>
public static class EmpresaSchema
{
    public static async Task<bool> EnsurePastaXmlBackupAsync(
        NFeDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var fechar = connection.State != ConnectionState.Open;
        if (fechar) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'empresa'
                  AND COLUMN_NAME = 'pasta_xml_backup';
                """;
            var existe = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (existe) return false;

            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE `empresa` ADD COLUMN `pasta_xml_backup` varchar(500) NULL;",
                cancellationToken);
            return true;
        }
        finally
        {
            if (fechar) await connection.CloseAsync();
        }
    }
}
