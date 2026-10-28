namespace NFe.Domain.Entities;

/// <summary>
/// Registro imutavel de CADA tentativa de emissao de NFS-e — inclusive as que
/// falham antes de virar uma <see cref="NotaServico"/> (validacao, certificado,
/// configuracao ausente). Uma linha por POST; nunca atualizada.
/// </summary>
public class TentativaEmissaoNfse
{
    public long Id { get; set; }

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public Guid? NotaServicoId { get; set; }
    public NotaServico? NotaServico { get; set; }

    public int Ambiente { get; set; }
    public string? Serie { get; set; }
    public long? NumeroDps { get; set; }

    public string? ChaveIdempotencia { get; set; }

    public bool Sucesso { get; set; }

    /// <summary>
    /// Fase em que terminou: "Validacao", "Configuracao", "Certificado",
    /// "Transmissao", "Rejeicao", "Autorizada".
    /// </summary>
    public string Fase { get; set; } = string.Empty;

    public string? CodigoStatus { get; set; }
    public string Mensagem { get; set; } = string.Empty;
    public string? Detalhe { get; set; }

    public string? ChaveAcesso { get; set; }
    public string? TomadorDocumento { get; set; }
    public string? TomadorNome { get; set; }
    public decimal? ValorServico { get; set; }

    /// <summary>Corpo JSON original recebido no POST de emissao.</summary>
    public string? JsonRecebido { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
