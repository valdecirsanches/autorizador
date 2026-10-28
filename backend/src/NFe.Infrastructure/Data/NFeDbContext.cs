using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;

namespace NFe.Infrastructure.Data;

/// <summary>
/// Contexto EF Core para MySQL/MariaDB. Os indices unicos aqui declarados sao
/// parte da regra fiscal, nao apenas otimizacao: a chave de acesso permanece
/// unica; a numeracao pode ter varias tentativas controladas pelo app emissor.
/// </summary>
public class NFeDbContext : DbContext
{
    public NFeDbContext(DbContextOptions<NFeDbContext> options) : base(options) { }

    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<LogoEmpresa> LogosEmpresa => Set<LogoEmpresa>();
    public DbSet<Certificado> Certificados => Set<Certificado>();
    public DbSet<AgenteCertificado> AgentesCertificado => Set<AgenteCertificado>();
    public DbSet<SerieNumeracao> SeriesNumeracao => Set<SerieNumeracao>();
    public DbSet<InutilizacaoNumeracao> InutilizacoesNumeracao => Set<InutilizacaoNumeracao>();
    public DbSet<DocumentoFiscal> DocumentosFiscais => Set<DocumentoFiscal>();
    public DbSet<EventoDocumento> EventosDocumento => Set<EventoDocumento>();
    public DbSet<EventoFiscal> EventosFiscais => Set<EventoFiscal>();
    public DbSet<ConsultaStatusServico> ConsultasStatusServico => Set<ConsultaStatusServico>();
    public DbSet<TentativaEmissao> TentativasEmissao => Set<TentativaEmissao>();

    // ---- Modulo NFS-e (tabelas proprias; nada compartilhado com a NF-e) ----
    public DbSet<EmpresaNfseConfig> EmpresasNfseConfig => Set<EmpresaNfseConfig>();
    public DbSet<SerieDps> SeriesDps => Set<SerieDps>();
    public DbSet<NotaServico> NotasServico => Set<NotaServico>();
    public DbSet<EventoNotaServico> EventosNotaServico => Set<EventoNotaServico>();
    public DbSet<TentativaEmissaoNfse> TentativasEmissaoNfse => Set<TentativaEmissaoNfse>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        MapearNfse(b);

        // ---------------------------- Empresa ----------------------------
        b.Entity<Empresa>(e =>
        {
            e.ToTable("empresa");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Cnpj).HasColumnName("cnpj").HasMaxLength(14).IsFixedLength().IsRequired();
            e.Property(x => x.RazaoSocial).HasColumnName("razao_social").HasMaxLength(200).IsRequired();
            e.Property(x => x.NomeFantasia).HasColumnName("nome_fantasia").HasMaxLength(200);
            e.Property(x => x.InscricaoEstadual).HasColumnName("inscricao_estadual").HasMaxLength(20).IsRequired();
            e.Property(x => x.InscricaoMunicipal).HasColumnName("inscricao_municipal").HasMaxLength(20);
            e.Property(x => x.CnaeFiscal).HasColumnName("cnae_fiscal").HasMaxLength(7);
            e.Property(x => x.RegimeTributario).HasColumnName("regime_tributario").HasConversion<int>().IsRequired();

            e.Property(x => x.Logradouro).HasColumnName("logradouro").HasMaxLength(200).IsRequired();
            e.Property(x => x.Numero).HasColumnName("numero").HasMaxLength(60).IsRequired();
            e.Property(x => x.Complemento).HasColumnName("complemento").HasMaxLength(60);
            e.Property(x => x.Bairro).HasColumnName("bairro").HasMaxLength(60).IsRequired();
            e.Property(x => x.CodigoMunicipio).HasColumnName("codigo_municipio").HasMaxLength(7).IsRequired();
            e.Property(x => x.NomeMunicipio).HasColumnName("nome_municipio").HasMaxLength(120).IsRequired();
            e.Property(x => x.Uf).HasColumnName("uf").HasMaxLength(2).IsFixedLength().IsRequired();
            e.Property(x => x.Cep).HasColumnName("cep").HasMaxLength(8).IsFixedLength().IsRequired();
            e.Property(x => x.CodigoPais).HasColumnName("codigo_pais").HasMaxLength(4).IsRequired();
            e.Property(x => x.NomePais).HasColumnName("nome_pais").HasMaxLength(60).IsRequired();
            e.Property(x => x.Telefone).HasColumnName("telefone").HasMaxLength(14);

            e.Property(x => x.AmbientePadrao).HasColumnName("ambiente_padrao").HasConversion<int>().IsRequired();
            e.Property(x => x.UfAutorizadorOverride).HasColumnName("uf_autorizador_override").HasMaxLength(2);
            e.Property(x => x.PastaXmlBackup).HasColumnName("pasta_xml_backup").HasMaxLength(500);
            e.Property(x => x.Ativa).HasColumnName("ativa").IsRequired();
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            // Um CNPJ nunca se repete. Base do isolamento multiempresa.
            e.HasIndex(x => x.Cnpj).IsUnique().HasDatabaseName("ux_empresa_cnpj");
            e.HasIndex(x => x.Ativa).HasDatabaseName("ix_empresa_ativa");
        });

        // -------------------------- LogoEmpresa --------------------------
        // 1:1 com Empresa. Blob mantido fora da tabela "empresa" para nao pesar
        // as consultas da lista de empresas.
        b.Entity<LogoEmpresa>(e =>
        {
            e.ToTable("logo_empresa");
            e.HasKey(x => x.EmpresaId);
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Conteudo).HasColumnName("conteudo").HasColumnType("LONGBLOB").IsRequired();
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(60).IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithOne()
                .HasForeignKey<LogoEmpresa>(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // -------------------------- Certificado --------------------------
        b.Entity<Certificado>(e =>
        {
            e.ToTable("certificado");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(120).IsRequired();
            e.Property(x => x.Tipo).HasColumnName("tipo").HasConversion<int>().HasDefaultValue(TipoCertificado.A1).IsRequired();
            e.Property(x => x.CertificadoPublico).HasColumnName("certificado_publico").HasColumnType("BLOB");

            // Um .pfx cifrado fica tipicamente entre 2 KB e 12 KB.
            e.Property(x => x.PfxCifrado).HasColumnName("pfx_cifrado").HasColumnType("LONGBLOB").IsRequired();
            e.Property(x => x.PfxNonce).HasColumnName("pfx_nonce").HasColumnType("VARBINARY(12)").IsRequired();
            e.Property(x => x.PfxTag).HasColumnName("pfx_tag").HasColumnType("VARBINARY(16)").IsRequired();
            e.Property(x => x.SenhaCifrada).HasColumnName("senha_cifrada").HasColumnType("VARBINARY(512)").IsRequired();
            e.Property(x => x.SenhaNonce).HasColumnName("senha_nonce").HasColumnType("VARBINARY(12)").IsRequired();
            e.Property(x => x.SenhaTag).HasColumnName("senha_tag").HasColumnType("VARBINARY(16)").IsRequired();

            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(500).IsRequired();
            e.Property(x => x.Emissor).HasColumnName("emissor").HasMaxLength(500).IsRequired();
            e.Property(x => x.NumeroSerie).HasColumnName("numero_serie").HasMaxLength(80).IsRequired();
            e.Property(x => x.Thumbprint).HasColumnName("thumbprint").HasMaxLength(40).IsRequired();
            e.Property(x => x.ValidoDe).HasColumnName("valido_de").IsRequired();
            e.Property(x => x.ValidoAte).HasColumnName("valido_ate").IsRequired();
            e.Property(x => x.CnpjCertificado).HasColumnName("cnpj_certificado").HasMaxLength(14);
            e.Property(x => x.Ativo).HasColumnName("ativo").IsRequired();
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany(x => x.Certificados)
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);

            // Impede cadastrar duas vezes o mesmo certificado na mesma empresa.
            e.HasIndex(x => new { x.EmpresaId, x.Thumbprint }).IsUnique().HasDatabaseName("ux_certificado_empresa_thumb");
            e.HasIndex(x => new { x.EmpresaId, x.Ativo }).HasDatabaseName("ix_certificado_empresa_ativo");
            e.HasIndex(x => x.ValidoAte).HasDatabaseName("ix_certificado_validade");
        });

        // ----------------------- AgenteCertificado -----------------------
        b.Entity<AgenteCertificado>(e =>
        {
            e.ToTable("agente_certificado");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.CertificadoId).HasColumnName("certificado_id").IsRequired();
            e.Property(x => x.Nome).HasColumnName("nome").HasMaxLength(120).IsRequired();
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.Revogado).HasColumnName("revogado").IsRequired();
            e.Property(x => x.VersaoAgente).HasColumnName("versao_agente").HasMaxLength(40);
            e.Property(x => x.UltimoContatoEm).HasColumnName("ultimo_contato_em");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.Certificado)
                .WithMany()
                .HasForeignKey(x => x.CertificadoId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_agente_certificado_token");
            e.HasIndex(x => x.CertificadoId).HasDatabaseName("ix_agente_certificado_cert");
            e.HasIndex(x => x.EmpresaId).HasDatabaseName("ix_agente_certificado_empresa");
        });

        // ------------------------ SerieNumeracao ------------------------
        b.Entity<SerieNumeracao>(e =>
        {
            e.ToTable("serie_numeracao");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Modelo).HasColumnName("modelo").HasConversion<int>().IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.UltimoNumero).HasColumnName("ultimo_numero").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany(x => x.Series)
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmpresaId, x.Modelo, x.Serie, x.Ambiente })
                .IsUnique().HasDatabaseName("ux_serie_empresa_modelo_serie_amb");
        });

        // -------------------- InutilizacaoNumeracao ---------------------
        // Trilha append-only das inutilizacoes de faixa. Uma linha por pedido;
        // a homologada (cStat 102) guarda o XML de guarda fiscal (procInutNFe).
        b.Entity<InutilizacaoNumeracao>(e =>
        {
            e.ToTable("inutilizacao_numeracao");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Modelo).HasColumnName("modelo").HasConversion<int>().IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.Ano).HasColumnName("ano").IsRequired();
            e.Property(x => x.NumeroInicial).HasColumnName("numero_inicial").IsRequired();
            e.Property(x => x.NumeroFinal).HasColumnName("numero_final").IsRequired();
            e.Property(x => x.Justificativa).HasColumnName("justificativa").HasMaxLength(255).IsRequired();

            e.Property(x => x.Homologado).HasColumnName("homologado").IsRequired();
            e.Property(x => x.IdInutilizacao).HasColumnName("id_inutilizacao").HasMaxLength(45);
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(5);
            e.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(500);
            e.Property(x => x.Protocolo).HasColumnName("protocolo").HasMaxLength(20);
            e.Property(x => x.DataRegistro).HasColumnName("data_registro");

            e.Property(x => x.XmlEnviado).HasColumnName("xml_enviado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlRetorno).HasColumnName("xml_retorno").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlInutilizacao).HasColumnName("xml_inutilizacao").HasColumnType("LONGTEXT");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EmpresaId, x.Modelo, x.Serie, x.Ambiente })
                .HasDatabaseName("ix_inut_empresa_modelo_serie_amb");
        });

        // ----------------------- DocumentoFiscal ------------------------
        b.Entity<DocumentoFiscal>(e =>
        {
            e.ToTable("documento_fiscal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(44).IsFixedLength();

            e.Property(x => x.Modelo).HasColumnName("modelo").HasConversion<int>().IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").IsRequired();
            e.Property(x => x.Numero).HasColumnName("numero").IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.TipoEmissao).HasColumnName("tipo_emissao").HasConversion<int>().IsRequired();
            e.Property(x => x.TipoOperacao).HasColumnName("tipo_operacao").HasConversion<int>().IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();

            e.Property(x => x.CodigoNumerico).HasColumnName("codigo_numerico").HasMaxLength(8).IsRequired();
            e.Property(x => x.DigitoVerificador).HasColumnName("digito_verificador");

            e.Property(x => x.DataEmissao).HasColumnName("data_emissao").IsRequired();
            e.Property(x => x.DataSaidaEntrada).HasColumnName("data_saida_entrada");

            e.Property(x => x.DestinatarioDocumento).HasColumnName("destinatario_documento").HasMaxLength(14);
            e.Property(x => x.DestinatarioNome).HasColumnName("destinatario_nome").HasMaxLength(200);
            e.Property(x => x.DestinatarioUf).HasColumnName("destinatario_uf").HasMaxLength(2);

            // decimal explicito: nunca usar ponto flutuante para valor fiscal.
            e.Property(x => x.ValorTotal).HasColumnName("valor_total").HasColumnType("decimal(15,2)").IsRequired();
            e.Property(x => x.ValorProdutos).HasColumnName("valor_produtos").HasColumnType("decimal(15,2)").IsRequired();
            e.Property(x => x.ValorIcms).HasColumnName("valor_icms").HasColumnType("decimal(15,2)").IsRequired();

            e.Property(x => x.NaturezaOperacao).HasColumnName("natureza_operacao").HasMaxLength(60).IsRequired();

            e.Property(x => x.XmlGerado).HasColumnName("xml_gerado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlAssinado).HasColumnName("xml_assinado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlProtocolado).HasColumnName("xml_protocolado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlRetornoSefaz).HasColumnName("xml_retorno_sefaz").HasColumnType("LONGTEXT");

            e.Property(x => x.Recibo).HasColumnName("recibo").HasMaxLength(20);
            e.Property(x => x.Protocolo).HasColumnName("protocolo").HasMaxLength(20);
            e.Property(x => x.CodigoStatusRetorno).HasColumnName("codigo_status_retorno").HasMaxLength(5);
            e.Property(x => x.MotivoRetorno).HasColumnName("motivo_retorno").HasMaxLength(500);
            e.Property(x => x.DataAutorizacao).HasColumnName("data_autorizacao");
            e.Property(x => x.DigestValue).HasColumnName("digest_value").HasMaxLength(64);

            e.Property(x => x.ChaveIdempotencia).HasColumnName("chave_idempotencia").HasMaxLength(80);
            e.Property(x => x.TentativasEnvio).HasColumnName("tentativas_envio").IsRequired();
            e.Property(x => x.UltimoErro).HasColumnName("ultimo_erro").HasMaxLength(1000);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            // Documento fiscal nunca e apagado em cascata com a empresa.
            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            // A chave de acesso e unica em todo o territorio nacional.
            e.HasIndex(x => x.ChaveAcesso).IsUnique().HasDatabaseName("ux_documento_chave_acesso");

            // Mantem consultas por serie/numero eficientes sem bloquear novas
            // tentativas para a mesma numeracao; o app emissor controla o fluxo.
            e.HasIndex(x => new { x.EmpresaId, x.Modelo, x.Serie, x.Numero, x.Ambiente })
                .HasDatabaseName("ix_documento_numeracao");

            // Mantido para compatibilidade com valores legados; novas emissões
            // deixam este campo nulo e registram o header apenas na auditoria.
            e.HasIndex(x => new { x.EmpresaId, x.ChaveIdempotencia })
                .IsUnique().HasDatabaseName("ux_documento_idempotencia");

            e.HasIndex(x => new { x.Status, x.CriadoEm }).HasDatabaseName("ix_documento_status_criado");
            e.HasIndex(x => new { x.EmpresaId, x.DataEmissao }).HasDatabaseName("ix_documento_empresa_data");
        });

        // ----------------------- EventoDocumento ------------------------
        b.Entity<EventoDocumento>(e =>
        {
            e.ToTable("evento_documento");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DocumentoFiscalId).HasColumnName("documento_fiscal_id").IsRequired();
            e.Property(x => x.StatusAnterior).HasColumnName("status_anterior").HasConversion<int?>();
            e.Property(x => x.StatusNovo).HasColumnName("status_novo").HasConversion<int>().IsRequired();
            e.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(500).IsRequired();
            e.Property(x => x.Detalhe).HasColumnName("detalhe").HasColumnType("TEXT");
            e.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em").IsRequired();

            e.HasOne(x => x.DocumentoFiscal)
                .WithMany(x => x.Eventos)
                .HasForeignKey(x => x.DocumentoFiscalId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.DocumentoFiscalId, x.OcorridoEm }).HasDatabaseName("ix_evento_documento_data");
        });

        // ------------------------- EventoFiscal -------------------------
        // Evento oficial da SEFAZ (cancelamento, CC-e). Append-only: uma linha
        // por transmissao; a mais recente homologada e a valida.
        b.Entity<EventoFiscal>(e =>
        {
            e.ToTable("evento_fiscal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DocumentoFiscalId).HasColumnName("documento_fiscal_id").IsRequired();
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(44).IsFixedLength().IsRequired();
            e.Property(x => x.TipoEvento).HasColumnName("tipo_evento").IsRequired();
            e.Property(x => x.SequenciaEvento).HasColumnName("sequencia_evento").IsRequired();
            e.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(120).IsRequired();
            e.Property(x => x.Justificativa).HasColumnName("justificativa").HasMaxLength(255);
            e.Property(x => x.Correcao).HasColumnName("correcao").HasMaxLength(1000);
            e.Property(x => x.ProtocoloNFe).HasColumnName("protocolo_nfe").HasMaxLength(20);

            e.Property(x => x.ProtocoloEvento).HasColumnName("protocolo_evento").HasMaxLength(20);
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(5);
            e.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(500);
            e.Property(x => x.DataRegistro).HasColumnName("data_registro");
            e.Property(x => x.Homologado).HasColumnName("homologado").IsRequired();

            e.Property(x => x.XmlEnviado).HasColumnName("xml_enviado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlEvento).HasColumnName("xml_evento").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlRetorno).HasColumnName("xml_retorno").HasColumnType("LONGTEXT");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.DocumentoFiscal)
                .WithMany(x => x.EventosFiscais)
                .HasForeignKey(x => x.DocumentoFiscalId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.DocumentoFiscalId, x.TipoEvento, x.SequenciaEvento })
                .HasDatabaseName("ix_evento_fiscal_doc_tipo_seq");
            e.HasIndex(x => x.ChaveAcesso).HasDatabaseName("ix_evento_fiscal_chave");
        });

        // -------------------- ConsultaStatusServico ---------------------
        b.Entity<ConsultaStatusServico>(e =>
        {
            e.ToTable("consulta_status_servico");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id");
            e.Property(x => x.Uf).HasColumnName("uf").HasMaxLength(2).IsFixedLength().IsRequired();
            e.Property(x => x.Autorizador).HasColumnName("autorizador").HasMaxLength(20).IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.Modelo).HasColumnName("modelo").HasConversion<int>().IsRequired();
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(5);
            e.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(500);
            e.Property(x => x.TempoMedioResposta).HasColumnName("tempo_medio_resposta");
            e.Property(x => x.DataHoraSefaz).HasColumnName("data_hora_sefaz");
            e.Property(x => x.PrevisaoRetorno).HasColumnName("previsao_retorno");
            e.Property(x => x.TempoRespostaMs).HasColumnName("tempo_resposta_ms").IsRequired();
            e.Property(x => x.Online).HasColumnName("online").IsRequired();
            e.Property(x => x.ErroTransporte).HasColumnName("erro_transporte").HasMaxLength(1000);
            e.Property(x => x.ConsultadoEm).HasColumnName("consultado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.Uf, x.Ambiente, x.ConsultadoEm }).HasDatabaseName("ix_status_uf_amb_data");
        });

        // ----------------------- TentativaEmissao -----------------------
        // Trilha de TODA tentativa de emissao, inclusive as que falham antes de
        // virar documento. Nunca sofre update: uma linha por POST de emissao.
        b.Entity<TentativaEmissao>(e =>
        {
            e.ToTable("tentativa_emissao");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.DocumentoFiscalId).HasColumnName("documento_fiscal_id");
            e.Property(x => x.Ambiente).HasColumnName("ambiente").IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie");
            e.Property(x => x.Numero).HasColumnName("numero");
            e.Property(x => x.ChaveIdempotencia).HasColumnName("chave_idempotencia").HasMaxLength(80);
            e.Property(x => x.Sucesso).HasColumnName("sucesso").IsRequired();
            e.Property(x => x.Fase).HasColumnName("fase").HasMaxLength(30).IsRequired();
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(5);
            e.Property(x => x.Mensagem).HasColumnName("mensagem").HasMaxLength(1000).IsRequired();
            e.Property(x => x.Detalhe).HasColumnName("detalhe").HasColumnType("TEXT");
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(44);
            e.Property(x => x.DestinatarioDocumento).HasColumnName("destinatario_documento").HasMaxLength(14);
            e.Property(x => x.DestinatarioNome).HasColumnName("destinatario_nome").HasMaxLength(200);
            e.Property(x => x.ValorTotal).HasColumnName("valor_total").HasColumnType("decimal(15,2)");
            e.Property(x => x.JsonRecebido).HasColumnName("json_recebido").HasColumnType("LONGTEXT");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            // Nao apaga em cascata com a empresa; a trilha e permanente.
            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            // Se o documento for apagado, a tentativa sobrevive (FK vira null).
            e.HasOne(x => x.DocumentoFiscal)
                .WithMany()
                .HasForeignKey(x => x.DocumentoFiscalId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.EmpresaId, x.CriadoEm }).HasDatabaseName("ix_tentativa_empresa_data");
            e.HasIndex(x => new { x.Sucesso, x.CriadoEm }).HasDatabaseName("ix_tentativa_sucesso_data");
        });

        foreach (var entity in b.Model.GetEntityTypes())
            entity.SetAnnotation("MySql:CharSet", "utf8mb4");
    }

    /// <summary>
    /// Mapeamento do modulo NFS-e. Separado em metodo proprio para deixar claro
    /// que e 100% aditivo: cria tabelas novas e nao altera nenhuma tabela da NF-e.
    /// </summary>
    private static void MapearNfse(ModelBuilder b)
    {
        // ----------------------- EmpresaNfseConfig -----------------------
        b.Entity<EmpresaNfseConfig>(e =>
        {
            e.ToTable("empresa_nfse_config");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Padrao).HasColumnName("padrao").HasConversion<int>().IsRequired();
            e.Property(x => x.SerieDps).HasColumnName("serie_dps").HasMaxLength(5).IsRequired();
            e.Property(x => x.OpSimpNac).HasColumnName("op_simp_nac").HasConversion<int>().IsRequired();
            e.Property(x => x.RegApTribSN).HasColumnName("reg_ap_trib_sn").IsRequired();
            e.Property(x => x.RegEspTrib).HasColumnName("reg_esp_trib").HasConversion<int>().IsRequired();
            e.Property(x => x.CodigoMunicipioEmissao).HasColumnName("codigo_municipio_emissao").HasMaxLength(7);
            e.Property(x => x.VersaoAplicativo).HasColumnName("versao_aplicativo").HasMaxLength(20).IsRequired();
            e.Property(x => x.AbrasfUrl).HasColumnName("abrasf_url").HasMaxLength(500);
            e.Property(x => x.AbrasfVersao).HasColumnName("abrasf_versao").HasMaxLength(10);
            e.Property(x => x.AbrasfUsuario).HasColumnName("abrasf_usuario").HasMaxLength(120);
            e.Property(x => x.AbrasfSenha).HasColumnName("abrasf_senha").HasMaxLength(200);
            e.Property(x => x.OsascoChaveAutenticacao).HasColumnName("osasco_chave_autenticacao").HasMaxLength(500);
            e.Property(x => x.Ativa).HasColumnName("ativa").IsRequired();
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);

            // 1:1 logico com a empresa.
            e.HasIndex(x => x.EmpresaId).IsUnique().HasDatabaseName("ux_nfse_config_empresa");
        });

        // ---------------------------- SerieDps ----------------------------
        b.Entity<SerieDps>(e =>
        {
            e.ToTable("serie_dps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").HasMaxLength(5).IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.UltimoNumero).HasColumnName("ultimo_numero").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);

            // Um contador por (empresa, serie, ambiente): base da numeracao sem repeticao.
            e.HasIndex(x => new { x.EmpresaId, x.Serie, x.Ambiente })
                .IsUnique().HasDatabaseName("ux_serie_dps");
        });

        // --------------------------- NotaServico --------------------------
        b.Entity<NotaServico>(e =>
        {
            e.ToTable("nota_servico");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.Padrao).HasColumnName("padrao").HasConversion<int>().IsRequired();
            e.Property(x => x.Ambiente).HasColumnName("ambiente").HasConversion<int>().IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").HasMaxLength(5).IsRequired();
            e.Property(x => x.NumeroDps).HasColumnName("numero_dps").IsRequired();
            e.Property(x => x.IdDps).HasColumnName("id_dps").HasMaxLength(45);
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(50);
            e.Property(x => x.NumeroNfse).HasColumnName("numero_nfse").HasMaxLength(20);
            e.Property(x => x.CodigoVerificacao).HasColumnName("codigo_verificacao").HasMaxLength(40);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
            e.Property(x => x.DataEmissao).HasColumnName("data_emissao").IsRequired();
            e.Property(x => x.DataCompetencia).HasColumnName("data_competencia").IsRequired();
            e.Property(x => x.TomadorDocumento).HasColumnName("tomador_documento").HasMaxLength(40);
            e.Property(x => x.TomadorNome).HasColumnName("tomador_nome").HasMaxLength(300);
            e.Property(x => x.TomadorMunicipio).HasColumnName("tomador_municipio").HasMaxLength(120);
            e.Property(x => x.CodigoTributacaoNacional).HasColumnName("codigo_tributacao_nacional").HasMaxLength(6).IsRequired();
            e.Property(x => x.DescricaoServico).HasColumnName("descricao_servico").HasMaxLength(2000).IsRequired();
            e.Property(x => x.ValorServico).HasColumnName("valor_servico").HasColumnType("decimal(15,2)").IsRequired();
            e.Property(x => x.ValorDescontos).HasColumnName("valor_descontos").HasColumnType("decimal(15,2)").IsRequired();
            e.Property(x => x.AliquotaIss).HasColumnName("aliquota_iss").HasColumnType("decimal(5,2)").IsRequired();
            e.Property(x => x.ValorIss).HasColumnName("valor_iss").HasColumnType("decimal(15,2)").IsRequired();
            e.Property(x => x.IssRetido).HasColumnName("iss_retido").IsRequired();
            e.Property(x => x.ValorLiquido).HasColumnName("valor_liquido").HasColumnType("decimal(15,2)");
            e.Property(x => x.XmlDps).HasColumnName("xml_dps").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlDpsAssinado).HasColumnName("xml_dps_assinado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlNfse).HasColumnName("xml_nfse").HasColumnType("LONGTEXT");
            e.Property(x => x.RetornoBruto).HasColumnName("retorno_bruto").HasColumnType("LONGTEXT");
            e.Property(x => x.CodigoRetorno).HasColumnName("codigo_retorno").HasMaxLength(20);
            e.Property(x => x.MotivoRetorno).HasColumnName("motivo_retorno").HasMaxLength(1000);
            e.Property(x => x.DataAutorizacao).HasColumnName("data_autorizacao");
            e.Property(x => x.ChaveIdempotencia).HasColumnName("chave_idempotencia").HasMaxLength(80);
            e.Property(x => x.TentativasEnvio).HasColumnName("tentativas_envio").IsRequired();
            e.Property(x => x.UltimoErro).HasColumnName("ultimo_erro").HasMaxLength(2000);
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            // Regras fiscais no banco: nDPS unico por serie/ambiente; chave unica;
            // idempotencia unica por empresa (NULL nao colide no MySQL).
            e.HasIndex(x => new { x.EmpresaId, x.Serie, x.NumeroDps, x.Ambiente })
                .IsUnique().HasDatabaseName("ux_nota_servico_numeracao");
            e.HasIndex(x => x.ChaveAcesso).IsUnique().HasDatabaseName("ux_nota_servico_chave");
            e.HasIndex(x => new { x.EmpresaId, x.ChaveIdempotencia })
                .IsUnique().HasDatabaseName("ux_nota_servico_idempotencia");
            e.HasIndex(x => new { x.EmpresaId, x.Status }).HasDatabaseName("ix_nota_servico_empresa_status");
            e.HasIndex(x => x.DataEmissao).HasDatabaseName("ix_nota_servico_data_emissao");
        });

        // ------------------------ EventoNotaServico -----------------------
        b.Entity<EventoNotaServico>(e =>
        {
            e.ToTable("evento_nota_servico");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.NotaServicoId).HasColumnName("nota_servico_id").IsRequired();
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(50).IsRequired();
            e.Property(x => x.TipoEvento).HasColumnName("tipo_evento").HasMaxLength(10).IsRequired();
            e.Property(x => x.Sequencia).HasColumnName("sequencia").IsRequired();
            e.Property(x => x.CodigoMotivo).HasColumnName("codigo_motivo").HasMaxLength(5);
            e.Property(x => x.Justificativa).HasColumnName("justificativa").HasMaxLength(255);
            e.Property(x => x.Protocolo).HasColumnName("protocolo").HasMaxLength(60);
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(20);
            e.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(1000);
            e.Property(x => x.Homologado).HasColumnName("homologado").IsRequired();
            e.Property(x => x.DataRegistro).HasColumnName("data_registro");
            e.Property(x => x.XmlEnviado).HasColumnName("xml_enviado").HasColumnType("LONGTEXT");
            e.Property(x => x.XmlRetorno).HasColumnName("xml_retorno").HasColumnType("LONGTEXT");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.NotaServico)
                .WithMany(x => x.Eventos)
                .HasForeignKey(x => x.NotaServicoId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.NotaServicoId, x.TipoEvento, x.Sequencia })
                .HasDatabaseName("ix_evento_nfse_nota_tipo_seq");
            e.HasIndex(x => x.ChaveAcesso).HasDatabaseName("ix_evento_nfse_chave");
        });

        // ---------------------- TentativaEmissaoNfse ----------------------
        b.Entity<TentativaEmissaoNfse>(e =>
        {
            e.ToTable("tentativa_emissao_nfse");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.EmpresaId).HasColumnName("empresa_id").IsRequired();
            e.Property(x => x.NotaServicoId).HasColumnName("nota_servico_id");
            e.Property(x => x.Ambiente).HasColumnName("ambiente").IsRequired();
            e.Property(x => x.Serie).HasColumnName("serie").HasMaxLength(5);
            e.Property(x => x.NumeroDps).HasColumnName("numero_dps");
            e.Property(x => x.ChaveIdempotencia).HasColumnName("chave_idempotencia").HasMaxLength(80);
            e.Property(x => x.Sucesso).HasColumnName("sucesso").IsRequired();
            e.Property(x => x.Fase).HasColumnName("fase").HasMaxLength(30).IsRequired();
            e.Property(x => x.CodigoStatus).HasColumnName("codigo_status").HasMaxLength(20);
            e.Property(x => x.Mensagem).HasColumnName("mensagem").HasMaxLength(1000).IsRequired();
            e.Property(x => x.Detalhe).HasColumnName("detalhe").HasColumnType("TEXT");
            e.Property(x => x.ChaveAcesso).HasColumnName("chave_acesso").HasMaxLength(50);
            e.Property(x => x.TomadorDocumento).HasColumnName("tomador_documento").HasMaxLength(40);
            e.Property(x => x.TomadorNome).HasColumnName("tomador_nome").HasMaxLength(300);
            e.Property(x => x.ValorServico).HasColumnName("valor_servico").HasColumnType("decimal(15,2)");
            e.Property(x => x.JsonRecebido).HasColumnName("json_recebido").HasColumnType("LONGTEXT");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").IsRequired();

            e.HasOne(x => x.Empresa)
                .WithMany()
                .HasForeignKey(x => x.EmpresaId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.NotaServico)
                .WithMany()
                .HasForeignKey(x => x.NotaServicoId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.EmpresaId, x.CriadoEm }).HasDatabaseName("ix_tentativa_nfse_empresa_data");
            e.HasIndex(x => new { x.Sucesso, x.CriadoEm }).HasDatabaseName("ix_tentativa_nfse_sucesso_data");
        });
    }
}
