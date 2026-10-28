namespace NFe.Domain.Entities;

/// <summary>
/// Agente instalado na maquina do cliente, onde o token A3 esta plugado. Cada
/// agente e pareado a um certificado A3 e se autentica na API com um token que
/// so e exibido uma vez; no banco ficam apenas o hash SHA-256 e metadados.
/// </summary>
public class AgenteCertificado
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Guid CertificadoId { get; set; }
    public Certificado? Certificado { get; set; }

    /// <summary>Nome amigavel da maquina/instalacao, para o painel.</summary>
    public string Nome { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex minusculo) do token de pareamento.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public bool Revogado { get; set; }

    public string? VersaoAgente { get; set; }
    public DateTime? UltimoContatoEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
