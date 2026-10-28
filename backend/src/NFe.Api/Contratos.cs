using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Sefaz.Servicos;

namespace NFe.Api;

/// <summary>
/// DTOs de contrato da API. Sao deliberadamente separados das entidades de
/// dominio para nao vazar campos sensiveis (bytes cifrados do certificado,
/// nonce, tag) nem detalhes de persistencia para o painel.
/// </summary>
public static class Contratos
{
    public sealed record EmpresaDto(
        Guid Id,
        string Cnpj,
        string RazaoSocial,
        string? NomeFantasia,
        string InscricaoEstadual,
        string Uf,
        string NomeMunicipio,
        int RegimeTributario,
        string RegimeTributarioDescricao,
        int AmbientePadrao,
        string AmbientePadraoDescricao,
        string? UfAutorizadorOverride,
        bool Ativa,
        bool TemCertificadoAtivo,
        DateTime? CertificadoValidoAte,
        bool TemLogo,
        string? PastaXmlBackup = null)
    {
        public static EmpresaDto De(Empresa e, Certificado? certAtivo, bool temLogo = false)
        {
            return new EmpresaDto(
                e.Id, e.Cnpj, e.RazaoSocial, e.NomeFantasia, e.InscricaoEstadual,
                e.Uf, e.NomeMunicipio,
                (int)e.RegimeTributario, e.RegimeTributario.ToString(),
                (int)e.AmbientePadrao, e.AmbientePadrao.ToString(),
                e.UfAutorizadorOverride, e.Ativa,
                certAtivo is not null, certAtivo?.ValidoAte, temLogo,
                e.PastaXmlBackup);
        }
    }

    /// <summary>Pasta de backup dos XMLs de uma empresa (vazia remove a configuracao).</summary>
    public sealed record PastaXmlRequest(string? Pasta);

    /// <summary>Corpo para buscar na pasta configurada o XML de uma nota.</summary>
    public sealed record BuscarXmlPastaRequest(string ChaveAcesso);

    /// <summary>Corpo do pedido para definir o ambiente padrao da empresa.</summary>
    public sealed record DefinirAmbienteRequest(int Ambiente);

    /// <summary>Contador de numeracao de uma serie (por modelo e ambiente).</summary>
    public sealed record SerieNumeracaoDto(
        Guid Id,
        int Modelo,
        string ModeloDescricao,
        int Serie,
        int Ambiente,
        string AmbienteDescricao,
        long UltimoNumero,
        long ProximoNumero,
        DateTime AtualizadoEm)
    {
        public static SerieNumeracaoDto De(SerieNumeracao s) => new(
            s.Id,
            (int)s.Modelo, s.Modelo.ToString(),
            s.Serie,
            (int)s.Ambiente, s.Ambiente.ToString(),
            s.UltimoNumero, s.UltimoNumero + 1,
            s.AtualizadoEm);
    }

    /// <summary>
    /// Corpo do pedido para inicializar/ajustar a numeracao de uma serie.
    /// <c>ProximoNumero</c> e o numero que a PROXIMA nota emitida devera receber.
    /// </summary>
    public sealed record DefinirNumeracaoRequest(
        int Modelo,
        int Serie,
        int Ambiente,
        long ProximoNumero);

    /// <summary>Registro de uma inutilizacao de faixa de numeracao.</summary>
    public sealed record InutilizacaoDto(
        Guid Id,
        int Modelo,
        string ModeloDescricao,
        int Serie,
        int Ambiente,
        string AmbienteDescricao,
        int Ano,
        long NumeroInicial,
        long NumeroFinal,
        string Justificativa,
        bool Homologado,
        string? Protocolo,
        string? CodigoStatus,
        string? Motivo,
        DateTime? DataRegistro,
        DateTime CriadoEm)
    {
        public static InutilizacaoDto De(InutilizacaoNumeracao i) => new(
            i.Id,
            (int)i.Modelo, i.Modelo.ToString(),
            i.Serie,
            (int)i.Ambiente, i.Ambiente.ToString(),
            i.Ano,
            i.NumeroInicial, i.NumeroFinal,
            i.Justificativa,
            i.Homologado,
            i.Protocolo, i.CodigoStatus, i.Motivo,
            i.DataRegistro, i.CriadoEm);
    }

    /// <summary>
    /// Corpo do pedido de inutilizacao de faixa. <c>uf</c>, <c>cnpj</c> e
    /// <c>ambiente</c> vem no JSON (a API guarda so certificado e logo);
    /// <c>ufAutorizadorOverride</c> e opcional. <c>ano</c> opcional (usa o ano
    /// corrente quando ausente). A operacao e IRREVERSIVEL.
    /// </summary>
    public sealed record InutilizarRequest(
        string Uf,
        string Cnpj,
        string? UfAutorizadorOverride,
        int Modelo,
        int Serie,
        int Ambiente,
        int? Ano,
        long NumeroInicial,
        long NumeroFinal,
        string Justificativa);

    public sealed record CriarEmpresaRequest(
        string Cnpj,
        string RazaoSocial,
        string? NomeFantasia,
        string InscricaoEstadual,
        string? InscricaoMunicipal,
        string? CnaeFiscal,
        int RegimeTributario,
        string Logradouro,
        string Numero,
        string? Complemento,
        string Bairro,
        string CodigoMunicipio,
        string NomeMunicipio,
        string Uf,
        string Cep,
        string? Telefone,
        string? UfAutorizadorOverride);

    public sealed record AtualizarEmpresaRequest(
        string RazaoSocial,
        string? NomeFantasia,
        string InscricaoEstadual,
        int RegimeTributario,
        string Logradouro,
        string Numero,
        string? Complemento,
        string Bairro,
        string CodigoMunicipio,
        string NomeMunicipio,
        string Uf,
        string Cep,
        string? Telefone,
        string? UfAutorizadorOverride,
        int AmbientePadrao,
        bool Ativa);

    public sealed record CertificadoDto(
        Guid Id,
        Guid EmpresaId,
        string Descricao,
        string Subject,
        string Emissor,
        string? CnpjCertificado,
        DateTime ValidoDe,
        DateTime ValidoAte,
        bool Ativo,
        bool Expirado,
        int DiasParaExpirar,
        DateTime CriadoEm,
        string Tipo = "A1")
    {
        public static CertificadoDto De(Certificado c)
        {
            var agora = DateTime.UtcNow;
            var expirado = c.ValidoAte < agora;
            var dias = (int)Math.Floor((c.ValidoAte - agora).TotalDays);
            return new CertificadoDto(
                c.Id, c.EmpresaId, c.Descricao, c.Subject, c.Emissor,
                c.CnpjCertificado, c.ValidoDe, c.ValidoAte, c.Ativo,
                expirado, dias, c.CriadoEm, c.Tipo.ToString());
        }
    }

    public sealed record StatusServicoDto(
        long Id,
        Guid? EmpresaId,
        string Uf,
        string Autorizador,
        int Ambiente,
        string AmbienteDescricao,
        bool Online,
        string? CodigoStatus,
        string? Motivo,
        long TempoRespostaMs,
        DateTime? DataHoraSefaz,
        string? ErroTransporte,
        DateTime ConsultadoEm)
    {
        public static StatusServicoDto De(ConsultaStatusServico s)
        {
            return new StatusServicoDto(
                s.Id, s.EmpresaId, s.Uf, s.Autorizador,
                (int)s.Ambiente, s.Ambiente.ToString(),
                s.Online, s.CodigoStatus, s.Motivo, s.TempoRespostaMs,
                s.DataHoraSefaz, s.ErroTransporte, s.ConsultadoEm);
        }
    }

    /// <summary>Um evento da trilha de auditoria do documento.</summary>
    public sealed record EventoDocumentoDto(
        long Id,
        int? StatusAnterior,
        int StatusNovo,
        string Descricao,
        string? Detalhe,
        DateTime OcorridoEm)
    {
        public static EventoDocumentoDto De(EventoDocumento e) => new(
            e.Id,
            e.StatusAnterior is null ? null : (int)e.StatusAnterior,
            (int)e.StatusNovo, e.Descricao, e.Detalhe, e.OcorridoEm);
    }

    /// <summary>
    /// Resultado de uma emissao de NF-e. Nao inclui os XML por padrao (podem ser
    /// grandes); use o endpoint de XML para baixar o documento autorizado.
    /// </summary>
    public sealed record DocumentoFiscalDto(
        Guid Id,
        Guid EmpresaId,
        string? ChaveAcesso,
        int Modelo,
        int Serie,
        long Numero,
        int Ambiente,
        string AmbienteDescricao,
        int Status,
        string StatusDescricao,
        bool Autorizado,
        string? Protocolo,
        string? CodigoStatusRetorno,
        string? MotivoRetorno,
        DateTime? DataAutorizacao,
        string? DestinatarioDocumento,
        string? DestinatarioNome,
        string? DestinatarioUf,
        decimal ValorTotal,
        decimal ValorProdutos,
        string NaturezaOperacao,
        int TentativasEnvio,
        string? UltimoErro,
        // True quando ha resposta bruta da SEFAZ salva (baixavel em /nfe/{id}/retorno-sefaz).
        bool TemRetornoSefaz,
        // True quando ha QUALQUER XML salvo (assinado ou autorizado). Permite baixar
        // o XML enviado (assinado) em /nfe/{id}/xml mesmo quando a nota foi rejeitada.
        bool TemXml,
        DateTime CriadoEm,
        DateTime AtualizadoEm,
        IReadOnlyList<EventoDocumentoDto>? Eventos,
        // Preenchidos SOMENTE na resposta de emissao quando a nota e autorizada:
        // o XML autorizado (nfeProc) e o DANFE em PDF, ambos em Base64, para o app
        // salvar sem uma segunda requisicao. Nulos nos demais endpoints.
        string? XmlBase64 = null,
        string? DanfePdfBase64 = null)
    {
        public static DocumentoFiscalDto De(DocumentoFiscal d, bool incluirEventos = false)
        {
            return new DocumentoFiscalDto(
                d.Id, d.EmpresaId, d.ChaveAcesso,
                (int)d.Modelo, d.Serie, d.Numero,
                (int)d.Ambiente, d.Ambiente.ToString(),
                (int)d.Status, d.Status.ToString(),
                d.Status == StatusDocumento.Autorizado,
                d.Protocolo, d.CodigoStatusRetorno, d.MotivoRetorno, d.DataAutorizacao,
                d.DestinatarioDocumento, d.DestinatarioNome, d.DestinatarioUf,
                d.ValorTotal, d.ValorProdutos, d.NaturezaOperacao,
                d.TentativasEnvio, d.UltimoErro,
                !string.IsNullOrEmpty(d.XmlRetornoSefaz),
                !string.IsNullOrEmpty(d.XmlProtocolado) || !string.IsNullOrEmpty(d.XmlAssinado),
                d.CriadoEm, d.AtualizadoEm,
                incluirEventos
                    ? d.Eventos.OrderBy(e => e.OcorridoEm)
                        .Select(EventoDocumentoDto.De).ToList()
                    : null);
        }
    }

    /// <summary>
    /// Corpo da consulta de situacao por chave de acesso. UF e ambiente saem da
    /// chave; <c>ambiente</c> (1|2), <c>ufAutorizadorOverride</c> e
    /// <c>contingencia</c> sao opcionais.
    /// </summary>
    public sealed record ConsultaProtocoloRequest(
        string ChaveAcesso,
        int? Ambiente = null,
        string? UfAutorizadorOverride = null,
        bool? Contingencia = null);

    /// <summary>
    /// Corpo para anexar o XML de uma nota emitida fora da API. <c>xml</c> e a NFe
    /// assinada ou o nfeProc; <c>xmlBase64</c> e a alternativa em Base64.
    /// </summary>
    public sealed record AnexarXmlRequest(
        string ChaveAcesso,
        string? Xml = null,
        string? XmlBase64 = null,
        int? Ambiente = null);

    /// <summary>
    /// Corpo da consulta de status do servico. UF e ambiente vem no JSON.
    /// </summary>
    public sealed record ConsultaStatusRequest(
        string Uf,
        int Ambiente,
        string? UfAutorizadorOverride = null,
        bool? Contingencia = null);

    /// <summary>Corpo do pedido de cancelamento de uma NF-e.</summary>
    public sealed record CancelamentoRequest(string Justificativa);

    /// <summary>Corpo do pedido de carta de correcao (xCorrecao, 15 a 1000 caracteres).</summary>
    public sealed record CartaCorrecaoRequest(string Correcao);

    /// <summary>Um evento fiscal oficial (cancelamento, CC-e) da nota.</summary>
    public sealed record EventoFiscalDto(
        long Id,
        Guid DocumentoFiscalId,
        string ChaveAcesso,
        int TipoEvento,
        string TipoEventoDescricao,
        int SequenciaEvento,
        string Descricao,
        string? Justificativa,
        string? Correcao,
        string? ProtocoloEvento,
        string? CodigoStatus,
        string? Motivo,
        bool Homologado,
        DateTime? DataRegistro,
        DateTime CriadoEm)
    {
        public static EventoFiscalDto De(EventoFiscal e) => new(
            e.Id, e.DocumentoFiscalId, e.ChaveAcesso,
            e.TipoEvento, TiposEvento.Descricao(e.TipoEvento.ToString()),
            e.SequenciaEvento, e.Descricao, e.Justificativa, e.Correcao,
            e.ProtocoloEvento, e.CodigoStatus, e.Motivo, e.Homologado,
            e.DataRegistro, e.CriadoEm);
    }

    /// <summary>Uma tentativa de emissao (sucesso ou falha) para o painel.</summary>
    public sealed record TentativaEmissaoDto(
        long Id,
        string? DocumentoFiscalId,
        int Ambiente,
        int? Serie,
        long? Numero,
        bool Sucesso,
        string Fase,
        string? CodigoStatus,
        string Mensagem,
        string? Detalhe,
        string? ChaveAcesso,
        string? DestinatarioDocumento,
        string? DestinatarioNome,
        decimal? ValorTotal,
        DateTime CriadoEm)
    {
        public static TentativaEmissaoDto De(TentativaEmissao t) => new(
            t.Id,
            t.DocumentoFiscalId?.ToString(),
            t.Ambiente, t.Serie, t.Numero,
            t.Sucesso, t.Fase, t.CodigoStatus, t.Mensagem, t.Detalhe,
            t.ChaveAcesso, t.DestinatarioDocumento, t.DestinatarioNome,
            t.ValorTotal, t.CriadoEm);
    }

    /// <summary>Um evento vinculado a NF-e (cancelamento, CCe) no retorno da consulta.</summary>
    public sealed record EventoVinculadoDto(
        string TipoEvento,
        string Descricao,
        string? NumeroProtocolo,
        DateTime? DataEvento)
    {
        public static EventoVinculadoDto De(EventoVinculado e) =>
            new(e.TipoEvento, e.Descricao, e.NumeroProtocolo, e.DataEvento);
    }

    /// <summary>Resultado da consulta de situacao de uma NF-e na SEFAZ.</summary>
    public sealed record ConsultaProtocoloDto(
        string ChaveAcesso,
        string Uf,
        string Autorizador,
        int Ambiente,
        string AmbienteDescricao,
        string CodigoStatus,
        string Motivo,
        string? VersaoAplicacao,
        string? NumeroProtocolo,
        bool Autorizada,
        bool Cancelada,
        bool Denegada,
        DateTime? DataAutorizacao,
        DateTime? DataHoraSefaz,
        long TempoRespostaMs,
        IReadOnlyList<EventoVinculadoDto> Eventos,
        // Preenchidos quando a nota e desta empresa e esta autorizada/cancelada:
        // registro na base, XML (nfeProc) e DANFE em Base64 para o app gravar.
        DocumentoFiscalDto? Documento = null,
        string? XmlBase64 = null,
        string? DanfePdfBase64 = null,
        bool CadastradaAgora = false,
        string? Aviso = null,
        // "api" (ja na base) ou "pasta" (achado agora na pasta); null quando sem XML.
        string? XmlOrigem = null,
        // Por que o XML nao veio: pasta_nao_configurada, nao_encontrado ou erro.
        string? XmlPendente = null)
    {
        public static ConsultaProtocoloDto De(ConsultaProtocoloResultado r) => new(
            r.ChaveAcesso, r.Uf, r.Autorizador,
            (int)r.Ambiente, r.Ambiente.ToString(),
            r.CodigoStatus, r.Motivo, r.VersaoAplicacao, r.NumeroProtocolo,
            r.Autorizada, r.Cancelada, r.Denegada,
            r.DataAutorizacao, r.DataHoraSefaz, r.TempoRespostaMs,
            r.Eventos.Select(EventoVinculadoDto.De).ToList());
    }
}
