namespace NFe.Domain.Entities;

/// <summary>
/// Evento fiscal registrado na SEFAZ e vinculado a uma NF-e autorizada
/// (cancelamento, carta de correcao, etc.). Diferente de <see cref="EventoDocumento"/>,
/// que e a trilha interna de mudancas de estado, este e o evento OFICIAL com
/// protocolo proprio da SEFAZ e XML de guarda (procEventoNFe).
///
/// Modelado de forma generica para atender aos varios tipos de evento: o tipo
/// (tpEvento) e a sequencia (nSeqEvento) identificam o evento. Uma linha e
/// gravada a cada transmissao; a mais recente homologada e a que vale.
/// </summary>
public class EventoFiscal
{
    public long Id { get; set; }

    public Guid DocumentoFiscalId { get; set; }
    public DocumentoFiscal? DocumentoFiscal { get; set; }

    /// <summary>Chave de acesso da NF-e alvo, desnormalizada para consulta.</summary>
    public string ChaveAcesso { get; set; } = string.Empty;

    /// <summary>Codigo do evento (tpEvento). 110111 = cancelamento, 110110 = CC-e.</summary>
    public int TipoEvento { get; set; }

    /// <summary>Sequencia do evento (nSeqEvento). Cancelamento e sempre 1.</summary>
    public int SequenciaEvento { get; set; } = 1;

    /// <summary>Descricao do evento (descEvento), por exemplo "Cancelamento".</summary>
    public string Descricao { get; set; } = string.Empty;

    /// <summary>Justificativa (xJust) do cancelamento. 15 a 255 caracteres.</summary>
    public string? Justificativa { get; set; }

    /// <summary>Texto da correcao (xCorrecao) da carta de correcao.</summary>
    public string? Correcao { get; set; }

    /// <summary>Protocolo de autorizacao da NF-e alvo (nProt), exigido no cancelamento.</summary>
    public string? ProtocoloNFe { get; set; }

    // ---- Retorno da SEFAZ ----

    /// <summary>Protocolo do EVENTO retornado pela SEFAZ (nProt do retEvento).</summary>
    public string? ProtocoloEvento { get; set; }

    /// <summary>cStat do evento (135/155 homologado, 136 registrado sem vinculo).</summary>
    public string? CodigoStatus { get; set; }

    /// <summary>xMotivo do evento.</summary>
    public string? Motivo { get; set; }

    /// <summary>Data e hora do registro do evento na SEFAZ (dhRegEvento).</summary>
    public DateTime? DataRegistro { get; set; }

    /// <summary>True quando o evento foi homologado (cStat 135 ou 155).</summary>
    public bool Homologado { get; set; }

    // ---- Trilha de XML ----

    /// <summary>envEvento assinado, exatamente como transmitido.</summary>
    public string? XmlEnviado { get; set; }

    /// <summary>procEventoNFe (evento assinado + retEvento), XML de guarda.</summary>
    public string? XmlEvento { get; set; }

    /// <summary>Resposta bruta e completa da SEFAZ (envelope retEnvEvento).</summary>
    public string? XmlRetorno { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
