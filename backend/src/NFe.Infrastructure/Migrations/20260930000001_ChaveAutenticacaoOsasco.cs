using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NFe.Infrastructure.Data;

#nullable disable

namespace NFe.Infrastructure.Migrations;

[DbContext(typeof(NFeDbContext))]
[Migration("20260930000001_ChaveAutenticacaoOsasco")]
public sealed partial class ChaveAutenticacaoOsasco : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "osasco_chave_autenticacao",
            table: "empresa_nfse_config",
            type: "varchar(500)",
            maxLength: 500,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "osasco_chave_autenticacao",
            table: "empresa_nfse_config");
    }
}
