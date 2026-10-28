using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations;

public partial class ProcessarNovaTentativaNfe : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_documento_numeracao",
            table: "documento_fiscal");

        migrationBuilder.CreateIndex(
            name: "ix_documento_numeracao",
            table: "documento_fiscal",
            columns: new[] { "empresa_id", "modelo", "serie", "numero", "ambiente" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_documento_numeracao",
            table: "documento_fiscal");

        migrationBuilder.CreateIndex(
            name: "ux_documento_numeracao",
            table: "documento_fiscal",
            columns: new[] { "empresa_id", "modelo", "serie", "numero", "ambiente" },
            unique: true);
    }
}
