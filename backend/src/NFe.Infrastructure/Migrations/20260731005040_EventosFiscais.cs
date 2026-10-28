using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EventosFiscais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evento_fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    documento_fiscal_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    chave_acesso = table.Column<string>(type: "char(44)", fixedLength: true, maxLength: 44, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tipo_evento = table.Column<int>(type: "int", nullable: false),
                    sequencia_evento = table.Column<int>(type: "int", nullable: false),
                    descricao = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    justificativa = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    correcao = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocolo_nfe = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocolo_evento = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_registro = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    homologado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    xml_enviado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_evento = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_retorno = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_fiscal", x => x.id);
                    table.ForeignKey(
                        name: "FK_evento_fiscal_documento_fiscal_documento_fiscal_id",
                        column: x => x.documento_fiscal_id,
                        principalTable: "documento_fiscal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_evento_fiscal_chave",
                table: "evento_fiscal",
                column: "chave_acesso");

            migrationBuilder.CreateIndex(
                name: "ix_evento_fiscal_doc_tipo_seq",
                table: "evento_fiscal",
                columns: new[] { "documento_fiscal_id", "tipo_evento", "sequencia_evento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evento_fiscal");
        }
    }
}
