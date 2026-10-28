namespace NFe.Domain.Entities;

/// <summary>
/// NFS-e e sua trilha completa: DPS gerada/assinada, retorno do provedor e XML da
/// NFS-e. Entidade independente de <see cref="DocumentoFiscal"/> (NF-e).
/// </summary>
public class NotaServico
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public PadraoNfse Padrao { get; set; } = PadraoNfse.Nacional;
    public TipoAmbiente Ambiente { get; set; }

    /// <summary>Serie da DPS (ate 5 posicoes).</summary>
    public string Serie { get; set; } = "00001";

    /// <summary>Numero da DPS (nDPS), ate 15 digitos.</summary>
    public long NumeroDps { get; set; }

    /// <summary>Id da DPS (45 caracteres: "DPS" + cLocEmi + tpInsc + CNPJ + serie + nDPS).</summary>
    public string? IdDps { get; set; }

    /// <summary>Chave de acesso da NFS-e (50 digitos no padrao nacional).</summary>
    public string? ChaveAcesso { get; set; }

    /// <summary>Numero da NFS-e atribuido pelo provedor (nNFSe).</summary>
    public string? NumeroNfse { get; set; }

    /// <summary>Codigo de verificacao (ABRASF) ou complemento do nacional, quando houver.</summary>
    public string? CodigoVerificacao { get; set; }

    public StatusNfse Status { get; set; } = StatusNfse.Rascunho;

    public DateTime DataEmissao { get; set; }

    /// <summary>Data de competencia do servico (dCompet).</summary>
    public DateTime DataCompetencia { get; set; }

    // Tomador desnormalizado para listagem/busca no painel.
    public string? TomadorDocumento { get; set; }
    public string? TomadorNome { get; set; }
    public string? TomadorMunicipio { get; set; }

    /// <summary>Codigo de tributacao nacional (cTribNac), 6 digitos.</summary>
    public string CodigoTributacaoNacional { get; set; } = string.Empty;

    /// <summary>Descricao do servico prestado (xDescServ).</summary>
    public string DescricaoServico { get; set; } = string.Empty;

    public decimal ValorServico { get; set; }
    public decimal ValorDescontos { get; set; }
    public decimal AliquotaIss { get; set; }
    public decimal ValorIss { get; set; }
    public bool IssRetido { get; set; }

    /// <summary>Valor liquido (servico - descontos - retencoes), quando informado pelo provedor.</summary>
    public decimal? ValorLiquido { get; set; }

    // Trilha de XML.
    public string? XmlDps { get; set; }
    public string? XmlDpsAssinado { get; set; }

    /// <summary>XML NFSe retornado pelo provedor (documento oficial).</summary>
    public string? XmlNfse { get; set; }

    /// <summary>Retorno bruto e completo do provedor (JSON/SOAP), sempre guardado.</summary>
    public string? RetornoBruto { get; set; }

    public string? CodigoRetorno { get; set; }
    public string? MotivoRetorno { get; set; }
    public DateTime? DataAutorizacao { get; set; }

    /// <summary>Chave de idempotencia enviada pelo cliente (unica por empresa).</summary>
    public string? ChaveIdempotencia { get; set; }

    public int TentativasEnvio { get; set; }
    public string? UltimoErro { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<EventoNotaServico> Eventos { get; set; } = new List<EventoNotaServico>();
}

/// <summary>
/// Evento oficial registrado no provedor para uma NFS-e (cancelamento). Uma linha
/// por transmissao; a mais recente homologada e a que vale.
/// </summary>
public class EventoNotaServico
{
    public long Id { get; set; }

    public Guid NotaServicoId { get; set; }
    public NotaServico? NotaServico { get; set; }

    /// <summary>Chave de acesso da NFS-e alvo, desnormalizada.</summary>
    public string ChaveAcesso { get; set; } = string.Empty;

    /// <summary>Codigo do evento. Nacional: "e101101" = cancelamento.</summary>
    public string TipoEvento { get; set; } = string.Empty;

    public int Sequencia { get; set; } = 1;

    /// <summary>Codigo do motivo (cMotivo) quando o padrao exige.</summary>
    public string? CodigoMotivo { get; set; }

    /// <summary>Justificativa textual (xMotivo).</summary>
    public string? Justificativa { get; set; }

    public string? Protocolo { get; set; }
    public string? CodigoStatus { get; set; }
    public string? Motivo { get; set; }
    public bool Homologado { get; set; }
    public DateTime? DataRegistro { get; set; }

    public string? XmlEnviado { get; set; }
    public string? XmlRetorno { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
