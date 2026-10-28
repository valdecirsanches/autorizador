namespace NFe.Domain.Entities;

/// <summary>
/// Historico de consultas ao servico de status da SEFAZ. Serve tanto ao painel
/// de monitoramento quanto a decisao automatica de entrada em contingencia.
/// Registra tambem as falhas de transporte, que sao justamente o sinal mais
/// importante de indisponibilidade.
/// </summary>
public class ConsultaStatusServico
{
    public long Id { get; set; }

    /// <summary>
    /// Empresa que originou a consulta. Nulo quando a consulta e do monitor
    /// automatico, que nao pertence a nenhuma empresa especifica.
    /// </summary>
    public Guid? EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>UF consultada, sigla de 2 letras.</summary>
    public string Uf { get; set; } = string.Empty;

    /// <summary>Autorizador efetivamente acionado, por exemplo SVRS ou SP.</summary>
    public string Autorizador { get; set; } = string.Empty;

    public TipoAmbiente Ambiente { get; set; }
    public ModeloDocumento Modelo { get; set; } = ModeloDocumento.NFe;

    /// <summary>cStat retornado. 107 significa servico em operacao.</summary>
    public string? CodigoStatus { get; set; }

    /// <summary>xMotivo retornado pela SEFAZ.</summary>
    public string? Motivo { get; set; }

    /// <summary>tMed, tempo medio de resposta em segundos informado pela SEFAZ.</summary>
    public int? TempoMedioResposta { get; set; }

    /// <summary>dhRecbto convertido para UTC.</summary>
    public DateTime? DataHoraSefaz { get; set; }

    /// <summary>
    /// Previsao de retorno informada pela SEFAZ quando o servico esta parado.
    /// </summary>
    public DateTime? PrevisaoRetorno { get; set; }

    /// <summary>Latencia medida pelo autorizador, em milissegundos.</summary>
    public long TempoRespostaMs { get; set; }

    /// <summary>
    /// Verdadeiro apenas quando houve resposta valida com cStat 107.
    /// </summary>
    public bool Online { get; set; }

    /// <summary>
    /// Falha de rede, TLS ou certificado. Preenchido quando nao houve resposta
    /// SOAP interpretavel, caso em que CodigoStatus fica nulo.
    /// </summary>
    public string? ErroTransporte { get; set; }

    public DateTime ConsultadoEm { get; set; } = DateTime.UtcNow;
}
