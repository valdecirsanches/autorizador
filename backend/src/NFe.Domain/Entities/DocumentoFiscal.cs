namespace NFe.Domain.Entities;

/// <summary>
/// Documento fiscal e sua trilha completa de autorizacao. Guarda os XML em cada
/// estagio para fins de auditoria e para atender ao prazo legal de guarda.
/// </summary>
public class DocumentoFiscal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>
    /// Chave de acesso de 44 digitos. Definitiva a partir da assinatura.
    /// </summary>
    public string? ChaveAcesso { get; set; }

    public ModeloDocumento Modelo { get; set; } = ModeloDocumento.NFe;
    public int Serie { get; set; }
    public long Numero { get; set; }
    public TipoAmbiente Ambiente { get; set; }
    public TipoEmissao TipoEmissao { get; set; } = TipoEmissao.Normal;
    public TipoOperacao TipoOperacao { get; set; } = TipoOperacao.Saida;

    public StatusDocumento Status { get; set; } = StatusDocumento.Rascunho;

    /// <summary>Codigo numerico aleatorio cNF, 8 digitos.</summary>
    public string CodigoNumerico { get; set; } = string.Empty;

    /// <summary>Digito verificador da chave de acesso.</summary>
    public int? DigitoVerificador { get; set; }

    public DateTime DataEmissao { get; set; }
    public DateTime? DataSaidaEntrada { get; set; }

    // Destinatario desnormalizado para permitir listagem e busca no painel sem
    // precisar reabrir o XML.
    public string? DestinatarioDocumento { get; set; }
    public string? DestinatarioNome { get; set; }
    public string? DestinatarioUf { get; set; }

    public decimal ValorTotal { get; set; }
    public decimal ValorProdutos { get; set; }
    public decimal ValorIcms { get; set; }

    /// <summary>Natureza da operacao, campo natOp.</summary>
    public string NaturezaOperacao { get; set; } = string.Empty;

    // Trilha de XML por estagio.
    public string? XmlGerado { get; set; }
    public string? XmlAssinado { get; set; }

    /// <summary>XML de distribuicao (nfeProc) com o protocolo anexado.</summary>
    public string? XmlProtocolado { get; set; }

    /// <summary>
    /// Resposta BRUTA e completa da SEFAZ (envelope retEnviNFe recebido), guardada
    /// SEMPRE que ha retorno — inclusive em rejeicao sem protocolo. E o "log
    /// completo" que permite diagnosticar por que uma nota nao foi autorizada.
    /// </summary>
    public string? XmlRetornoSefaz { get; set; }

    // Retorno da SEFAZ.
    public string? Recibo { get; set; }
    public string? Protocolo { get; set; }
    public string? CodigoStatusRetorno { get; set; }
    public string? MotivoRetorno { get; set; }
    public DateTime? DataAutorizacao { get; set; }

    /// <summary>Digest value da assinatura, retornado no protocolo.</summary>
    public string? DigestValue { get; set; }

    /// <summary>
    /// Campo legado preservado para compatibilidade com registros existentes.
    /// Novas emissões não usam a chave para deduplicar nem persistem seu valor.
    /// </summary>
    public string? ChaveIdempotencia { get; set; }

    public int TentativasEnvio { get; set; }
    public string? UltimoErro { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<EventoDocumento> Eventos { get; set; } = new List<EventoDocumento>();

    /// <summary>Eventos fiscais oficiais (cancelamento, carta de correcao) da nota.</summary>
    public ICollection<EventoFiscal> EventosFiscais { get; set; } = new List<EventoFiscal>();
}

/// <summary>
/// Trilha de auditoria imutavel. Cada transicao de estado e cada interacao com
/// a SEFAZ gera um registro, nunca uma atualizacao.
/// </summary>
public class EventoDocumento
{
    public long Id { get; set; }

    public Guid DocumentoFiscalId { get; set; }
    public DocumentoFiscal? DocumentoFiscal { get; set; }

    public StatusDocumento? StatusAnterior { get; set; }
    public StatusDocumento StatusNovo { get; set; }

    /// <summary>Descricao curta da transicao, por exemplo "xml assinado".</summary>
    public string Descricao { get; set; } = string.Empty;

    public string? Detalhe { get; set; }
    public DateTime OcorridoEm { get; set; } = DateTime.UtcNow;
}
