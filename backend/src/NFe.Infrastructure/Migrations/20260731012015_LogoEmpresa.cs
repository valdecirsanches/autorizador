using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LogoEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logo_empresa",
                columns: table => new
                {
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    conteudo = table.Column<byte[]>(type: "LONGBLOB", nullable: false),
                    content_type = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logo_empresa", x => x.empresa_id);
                    table.ForeignKey(
                        name: "FK_logo_empresa_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logo_empresa");
        }
    }
}
