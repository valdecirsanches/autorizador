using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <summary>
    /// Modulo NFS-e. Migracao 100% aditiva: cria cinco tabelas novas e nao toca
    /// em nenhuma tabela do fluxo de NF-e.
    /// </summary>
    public partial class ModuloNfse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------- empresa_nfse_config -----------------------
            migrationBuilder.CreateTable(
                name: "empresa_nfse_config",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    padrao = table.Column<int>(type: "int", nullable: false),
                    serie_dps = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    op_simp_nac = table.Column<int>(type: "int", nullable: false),
                    reg_ap_trib_sn = table.Column<int>(type: "int", nullable: false),
                    reg_esp_trib = table.Column<int>(type: "int", nullable: false),
                    codigo_municipio_emissao = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    versao_aplicativo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    abrasf_url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    abrasf_versao = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    abrasf_usuario = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    abrasf_senha = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ativa = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_empresa_nfse_config", x => x.id);
                    table.ForeignKey(
                        name: "FK_empresa_nfse_config_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ux_nfse_config_empresa",
                table: "empresa_nfse_config",
                column: "empresa_id",
                unique: true);

            // ----------------------------- serie_dps ----------------------------
            migrationBuilder.CreateTable(
                name: "serie_dps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    serie = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    ultimo_numero = table.Column<long>(type: "bigint", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serie_dps", x => x.id);
                    table.ForeignKey(
                        name: "FK_serie_dps_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ux_serie_dps",
                table: "serie_dps",
                columns: new[] { "empresa_id", "serie", "ambiente" },
                unique: true);

            // --------------------------- nota_servico ---------------------------
            migrationBuilder.CreateTable(
                name: "nota_servico",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    padrao = table.Column<int>(type: "int", nullable: false),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    numero_dps = table.Column<long>(type: "bigint", nullable: false),
                    id_dps = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    chave_acesso = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    numero_nfse = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_verificacao = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<int>(type: "int", nullable: false),
                    data_emissao = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    data_competencia = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    tomador_documento = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tomador_nome = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tomador_municipio = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_tributacao_nacional = table.Column<string>(type: "varchar(6)", maxLength: 6, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    descricao_servico = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    valor_servico = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    valor_descontos = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    aliquota_iss = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    valor_iss = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    iss_retido = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    valor_liquido = table.Column<decimal>(type: "decimal(15,2)", nullable: true),
                    xml_dps = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_dps_assinado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_nfse = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    retorno_bruto = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_retorno = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo_retorno = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_autorizacao = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    chave_idempotencia = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tentativas_envio = table.Column<int>(type: "int", nullable: false),
                    ultimo_erro = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nota_servico", x => x.id);
                    table.ForeignKey(
                        name: "FK_nota_servico_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ux_nota_servico_numeracao",
                table: "nota_servico",
                columns: new[] { "empresa_id", "serie", "numero_dps", "ambiente" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_nota_servico_chave",
                table: "nota_servico",
                column: "chave_acesso",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_nota_servico_idempotencia",
                table: "nota_servico",
                columns: new[] { "empresa_id", "chave_idempotencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nota_servico_empresa_status",
                table: "nota_servico",
                columns: new[] { "empresa_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_nota_servico_data_emissao",
                table: "nota_servico",
                column: "data_emissao");

            // ------------------------ evento_nota_servico -----------------------
            migrationBuilder.CreateTable(
                name: "evento_nota_servico",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    nota_servico_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    chave_acesso = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tipo_evento = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sequencia = table.Column<int>(type: "int", nullable: false),
                    codigo_motivo = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    justificativa = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocolo = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    homologado = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    data_registro = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    xml_enviado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_retorno = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_nota_servico", x => x.id);
                    table.ForeignKey(
                        name: "FK_evento_nota_servico_nota_servico_nota_servico_id",
                        column: x => x.nota_servico_id,
                        principalTable: "nota_servico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_evento_nfse_nota_tipo_seq",
                table: "evento_nota_servico",
                columns: new[] { "nota_servico_id", "tipo_evento", "sequencia" });

            migrationBuilder.CreateIndex(
                name: "ix_evento_nfse_chave",
                table: "evento_nota_servico",
                column: "chave_acesso");

            // ---------------------- tentativa_emissao_nfse ----------------------
            migrationBuilder.CreateTable(
                name: "tentativa_emissao_nfse",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    nota_servico_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    numero_dps = table.Column<long>(type: "bigint", nullable: true),
                    chave_idempotencia = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sucesso = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    fase = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mensagem = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    detalhe = table.Column<string>(type: "TEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    chave_acesso = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tomador_documento = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tomador_nome = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    valor_servico = table.Column<decimal>(type: "decimal(15,2)", nullable: true),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tentativa_emissao_nfse", x => x.id);
                    table.ForeignKey(
                        name: "FK_tentativa_emissao_nfse_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tentativa_emissao_nfse_nota_servico_nota_servico_id",
                        column: x => x.nota_servico_id,
                        principalTable: "nota_servico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_tentativa_emissao_nfse_nota_servico_id",
                table: "tentativa_emissao_nfse",
                column: "nota_servico_id");

            migrationBuilder.CreateIndex(
                name: "ix_tentativa_nfse_empresa_data",
                table: "tentativa_emissao_nfse",
                columns: new[] { "empresa_id", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_tentativa_nfse_sucesso_data",
                table: "tentativa_emissao_nfse",
                columns: new[] { "sucesso", "criado_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "tentativa_emissao_nfse");
            migrationBuilder.DropTable(name: "evento_nota_servico");
            migrationBuilder.DropTable(name: "nota_servico");
            migrationBuilder.DropTable(name: "serie_dps");
            migrationBuilder.DropTable(name: "empresa_nfse_config");
        }
    }
}
