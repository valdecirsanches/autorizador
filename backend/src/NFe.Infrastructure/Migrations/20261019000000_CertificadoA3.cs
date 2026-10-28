using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NFe.Infrastructure.Data;

#nullable disable

namespace NFe.Infrastructure.Migrations;

/// <summary>
/// Suporte a certificado A3: tipo do certificado, parte publica (sem PFX) e
/// tabela dos agentes pareados. So adiciona colunas e uma tabela; os
/// certificados A1 existentes ficam com tipo = 1 e seguem funcionando.
/// </summary>
[DbContext(typeof(NFeDbContext))]
[Migration("20261019000000_CertificadoA3")]
public partial class CertificadoA3 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "tipo",
            table: "certificado",
            type: "int",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<byte[]>(
            name: "certificado_publico",
            table: "certificado",
            type: "BLOB",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "agente_certificado",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false),
                empresa_id = table.Column<Guid>(type: "char(36)", nullable: false),
                certificado_id = table.Column<Guid>(type: "char(36)", nullable: false),
                nome = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                token_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                revogado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                versao_agente = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ultimo_contato_em = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agente_certificado", x => x.id);
                table.ForeignKey(
                    name: "FK_agente_certificado_certificado_certificado_id",
                    column: x => x.certificado_id,
                    principalTable: "certificado",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "ix_agente_certificado_cert",
            table: "agente_certificado",
            column: "certificado_id");

        migrationBuilder.CreateIndex(
            name: "ix_agente_certificado_empresa",
            table: "agente_certificado",
            column: "empresa_id");

        migrationBuilder.CreateIndex(
            name: "ux_agente_certificado_token",
            table: "agente_certificado",
            column: "token_hash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "agente_certificado");
        migrationBuilder.DropColumn(name: "certificado_publico", table: "certificado");
        migrationBuilder.DropColumn(name: "tipo", table: "certificado");
    }
}
