using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NFe.Infrastructure.Data;

#nullable disable

namespace NFe.Infrastructure.Migrations;

/// <summary>
/// Pasta de backup dos XMLs por empresa. So adiciona uma coluna anulavel.
/// </summary>
[DbContext(typeof(NFeDbContext))]
[Migration("20261027000000_PastaXmlBackupEmpresa")]
public partial class PastaXmlBackupEmpresa : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "pasta_xml_backup",
            table: "empresa",
            type: "varchar(500)",
            maxLength: 500,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "pasta_xml_backup", table: "empresa");
    }
}
