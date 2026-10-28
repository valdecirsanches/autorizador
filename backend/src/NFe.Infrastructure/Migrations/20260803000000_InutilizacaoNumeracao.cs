using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InutilizacaoNumeracao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inutilizacao_numeracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    modelo = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<int>(type: "int", nullable: false),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    ano = table.Column<int>(type: "int", nullable: false),
                    numero_inicial = table.Column<long>(type: "bigint", nullable: false),
                    numero_final = table.Column<long>(type: "bigint", nullable: false),
                    justificativa = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    homologado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    id_inutilizacao = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocolo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_registro = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    xml_enviado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_retorno = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_inutilizacao = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inutilizacao_numeracao", x => x.id);
                    table.ForeignKey(
                        name: "FK_inutilizacao_numeracao_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_inut_empresa_modelo_serie_amb",
                table: "inutilizacao_numeracao",
                columns: new[] { "empresa_id", "modelo", "serie", "ambiente" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inutilizacao_numeracao");
        }
    }
}
