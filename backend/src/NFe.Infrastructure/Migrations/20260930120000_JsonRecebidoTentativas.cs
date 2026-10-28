using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NFe.Infrastructure.Data;

#nullable disable

namespace NFe.Infrastructure.Migrations;

[DbContext(typeof(NFeDbContext))]
[Migration("20260930120000_JsonRecebidoTentativas")]
public partial class JsonRecebidoTentativas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "json_recebido",
            table: "tentativa_emissao",
            type: "LONGTEXT",
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
            name: "json_recebido",
            table: "tentativa_emissao_nfse",
            type: "LONGTEXT",
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "json_recebido", table: "tentativa_emissao");
        migrationBuilder.DropColumn(name: "json_recebido", table: "tentativa_emissao_nfse");
    }
}
