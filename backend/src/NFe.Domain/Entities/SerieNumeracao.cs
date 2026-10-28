namespace NFe.Domain.Entities;

/// <summary>
/// Controle de numeracao sequencial por empresa, modelo, serie e ambiente.
/// A numeracao da NF-e nao admite lacunas nem repeticoes, por isso a reserva do
/// proximo numero e feita sob lock pessimista dentro de transacao.
/// </summary>
public class SerieNumeracao
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public ModeloDocumento Modelo { get; set; } = ModeloDocumento.NFe;

    /// <summary>Serie de 0 a 999.</summary>
    public int Serie { get; set; }

    /// <summary>
    /// Homologacao e producao possuem numeracoes totalmente independentes.
    /// </summary>
    public TipoAmbiente Ambiente { get; set; }

    /// <summary>Ultimo numero efetivamente reservado.</summary>
    public long UltimoNumero { get; set; }

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
