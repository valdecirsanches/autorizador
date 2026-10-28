using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFe.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InicialMultiempresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "empresa",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    cnpj = table.Column<string>(type: "char(14)", fixedLength: true, maxLength: 14, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    razao_social = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    nome_fantasia = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    inscricao_estadual = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    inscricao_municipal = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    cnae_fiscal = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    regime_tributario = table.Column<int>(type: "int", nullable: false),
                    logradouro = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    numero = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    complemento = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    bairro = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_municipio = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    nome_municipio = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    uf = table.Column<string>(type: "char(2)", fixedLength: true, maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    cep = table.Column<string>(type: "char(8)", fixedLength: true, maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_pais = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    nome_pais = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    telefone = table.Column<string>(type: "varchar(14)", maxLength: 14, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ambiente_padrao = table.Column<int>(type: "int", nullable: false),
                    uf_autorizador_override = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ativa = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_empresa", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "certificado",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    descricao = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    pfx_cifrado = table.Column<byte[]>(type: "LONGBLOB", nullable: false),
                    pfx_nonce = table.Column<byte[]>(type: "VARBINARY(12)", nullable: false),
                    pfx_tag = table.Column<byte[]>(type: "VARBINARY(16)", nullable: false),
                    senha_cifrada = table.Column<byte[]>(type: "VARBINARY(512)", nullable: false),
                    senha_nonce = table.Column<byte[]>(type: "VARBINARY(12)", nullable: false),
                    senha_tag = table.Column<byte[]>(type: "VARBINARY(16)", nullable: false),
                    subject = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    emissor = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    numero_serie = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    thumbprint = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    valido_de = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    valido_ate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    cnpj_certificado = table.Column<string>(type: "varchar(14)", maxLength: 14, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ativo = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificado", x => x.id);
                    table.ForeignKey(
                        name: "FK_certificado_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "consulta_status_servico",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    uf = table.Column<string>(type: "char(2)", fixedLength: true, maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    autorizador = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    modelo = table.Column<int>(type: "int", nullable: false),
                    codigo_status = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tempo_medio_resposta = table.Column<int>(type: "int", nullable: true),
                    data_hora_sefaz = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    previsao_retorno = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    tempo_resposta_ms = table.Column<long>(type: "bigint", nullable: false),
                    online = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    erro_transporte = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    consultado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consulta_status_servico", x => x.id);
                    table.ForeignKey(
                        name: "FK_consulta_status_servico_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "documento_fiscal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    chave_acesso = table.Column<string>(type: "char(44)", fixedLength: true, maxLength: 44, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    modelo = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<int>(type: "int", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: false),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    tipo_emissao = table.Column<int>(type: "int", nullable: false),
                    tipo_operacao = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<int>(type: "int", nullable: false),
                    codigo_numerico = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    digito_verificador = table.Column<int>(type: "int", nullable: true),
                    data_emissao = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    data_saida_entrada = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    destinatario_documento = table.Column<string>(type: "varchar(14)", maxLength: 14, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    destinatario_nome = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    destinatario_uf = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    valor_total = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    valor_produtos = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    valor_icms = table.Column<decimal>(type: "decimal(15,2)", nullable: false),
                    natureza_operacao = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_gerado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_assinado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    xml_protocolado = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    recibo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocolo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    codigo_status_retorno = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    motivo_retorno = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_autorizacao = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    digest_value = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    chave_idempotencia = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tentativas_envio = table.Column<int>(type: "int", nullable: false),
                    ultimo_erro = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    criado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documento_fiscal", x => x.id);
                    table.ForeignKey(
                        name: "FK_documento_fiscal_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "serie_numeracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    empresa_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    modelo = table.Column<int>(type: "int", nullable: false),
                    serie = table.Column<int>(type: "int", nullable: false),
                    ambiente = table.Column<int>(type: "int", nullable: false),
                    ultimo_numero = table.Column<long>(type: "bigint", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serie_numeracao", x => x.id);
                    table.ForeignKey(
                        name: "FK_serie_numeracao_empresa_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresa",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "evento_documento",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    documento_fiscal_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    status_anterior = table.Column<int>(type: "int", nullable: true),
                    status_novo = table.Column<int>(type: "int", nullable: false),
                    descricao = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    detalhe = table.Column<string>(type: "TEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ocorrido_em = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_documento", x => x.id);
                    table.ForeignKey(
                        name: "FK_evento_documento_documento_fiscal_documento_fiscal_id",
                        column: x => x.documento_fiscal_id,
                        principalTable: "documento_fiscal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_certificado_empresa_ativo",
                table: "certificado",
                columns: new[] { "empresa_id", "ativo" });

            migrationBuilder.CreateIndex(
                name: "ix_certificado_validade",
                table: "certificado",
                column: "valido_ate");

            migrationBuilder.CreateIndex(
                name: "ux_certificado_empresa_thumb",
                table: "certificado",
                columns: new[] { "empresa_id", "thumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consulta_status_servico_empresa_id",
                table: "consulta_status_servico",
                column: "empresa_id");

            migrationBuilder.CreateIndex(
                name: "ix_status_uf_amb_data",
                table: "consulta_status_servico",
                columns: new[] { "uf", "ambiente", "consultado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_documento_empresa_data",
                table: "documento_fiscal",
                columns: new[] { "empresa_id", "data_emissao" });

            migrationBuilder.CreateIndex(
                name: "ix_documento_status_criado",
                table: "documento_fiscal",
                columns: new[] { "status", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ux_documento_chave_acesso",
                table: "documento_fiscal",
                column: "chave_acesso",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_documento_idempotencia",
                table: "documento_fiscal",
                columns: new[] { "empresa_id", "chave_idempotencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_documento_numeracao",
                table: "documento_fiscal",
                columns: new[] { "empresa_id", "modelo", "serie", "numero", "ambiente" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_empresa_ativa",
                table: "empresa",
                column: "ativa");

            migrationBuilder.CreateIndex(
                name: "ux_empresa_cnpj",
                table: "empresa",
                column: "cnpj",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evento_documento_data",
                table: "evento_documento",
                columns: new[] { "documento_fiscal_id", "ocorrido_em" });

            migrationBuilder.CreateIndex(
                name: "ux_serie_empresa_modelo_serie_amb",
                table: "serie_numeracao",
                columns: new[] { "empresa_id", "modelo", "serie", "ambiente" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificado");

            migrationBuilder.DropTable(
                name: "consulta_status_servico");

            migrationBuilder.DropTable(
                name: "evento_documento");

            migrationBuilder.DropTable(
                name: "serie_numeracao");

            migrationBuilder.DropTable(
                name: "documento_fiscal");

            migrationBuilder.DropTable(
                name: "empresa");
        }
    }
}
