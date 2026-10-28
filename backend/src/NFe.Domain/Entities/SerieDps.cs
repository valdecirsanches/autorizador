namespace NFe.Domain.Entities;

/// <summary>
/// Numeracao sequencial da DPS (nDPS) por empresa, serie e ambiente. Separada de
/// <see cref="SerieNumeracao"/> (NF-e) para que os dois modulos nao compartilhem
/// nem tabela nem lock.
/// </summary>
public class SerieDps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>Serie ate 5 posicoes, conforme layout da DPS.</summary>
    public string Serie { get; set; } = "00001";

    public TipoAmbiente Ambiente { get; set; }

    /// <summary>Ultimo nDPS efetivamente reservado.</summary>
    public long UltimoNumero { get; set; }

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
