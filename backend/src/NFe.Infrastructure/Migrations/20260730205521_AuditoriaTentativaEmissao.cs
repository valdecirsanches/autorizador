using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditoriaTentativaEmissao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tentativa_emissao",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    documento_fiscal_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<int>(type: "int", nullable: true),
                    numero = table.Column<long>(type: "bigint", nullable: true),
                    chave_idempotencia = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sucesso = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    fase = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mensagem = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    detalhe = table.Column<string>(type: "TEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    chave_acesso = table.Column<string>(type: "varchar(44)", maxLength: 44, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    destinatario_documento = table.Column<string>(type: "varchar(14)", maxLength: 14, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    destinatario_nome = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    valor_total = table.Column<decimal>(type: "decimal(15,2)", nullable: true),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tentativa_emissao", x => x.id);
                    table.ForeignKey(
                        name: "FK_tentativa_emissao_documento_fiscal_documento_fiscal_id",
                        column: x => x.documento_fiscal_id,
                        principalTable: "documento_fiscal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tentativa_emissao_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_tentativa_emissao_documento_fiscal_id",
                table: "tentativa_emissao",
                column: "documento_fiscal_id");

            migrationBuilder.CreateIndex(
                name: "ix_tentativa_empresa_data",
                table: "tentativa_emissao",
                columns: new[] { "empresa_id", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_tentativa_sucesso_data",
                table: "tentativa_emissao",
                columns: new[] { "sucesso", "criado_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tentativa_emissao");
        }
    }
}
