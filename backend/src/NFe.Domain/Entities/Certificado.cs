namespace NFe.Domain.Entities;

/// <summary>
/// Certificado digital A1 vinculado a uma empresa. O conteudo do arquivo PFX e
/// a senha nunca sao persistidos em claro: ambos sao cifrados com AES-256-GCM
/// usando a chave mestra da aplicacao.
/// </summary>
public class Certificado
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>Nome amigavel para identificacao no painel.</summary>
    public string Descricao { get; set; } = string.Empty;

    /// <summary>A1 (PFX no banco) ou A3 (chave no token do cliente).</summary>
    public TipoCertificado Tipo { get; set; } = TipoCertificado.A1;

    /// <summary>
    /// Certificado X.509 publico (DER). Preenchido apenas no A3, onde nao ha PFX;
    /// e usado no KeyInfo da assinatura e para validar o que o agente devolve.
    /// </summary>
    public byte[]? CertificadoPublico { get; set; }

    // No A3 os campos Pfx* e Senha* ficam como arrays vazios: mantem as colunas
    // NOT NULL e dispensa uma migracao que relaxe a restricao.

    /// <summary>Bytes do arquivo PFX cifrados com AES-256-GCM.</summary>
    public byte[] PfxCifrado { get; set; } = Array.Empty<byte>();

    /// <summary>Nonce de 12 bytes usado na cifragem do PFX.</summary>
    public byte[] PfxNonce { get; set; } = Array.Empty<byte>();

    /// <summary>Tag de autenticacao de 16 bytes do PFX.</summary>
    public byte[] PfxTag { get; set; } = Array.Empty<byte>();

    /// <summary>Senha do PFX cifrada com AES-256-GCM.</summary>
    public byte[] SenhaCifrada { get; set; } = Array.Empty<byte>();
    public byte[] SenhaNonce { get; set; } = Array.Empty<byte>();
    public byte[] SenhaTag { get; set; } = Array.Empty<byte>();

    // Metadados extraidos do certificado no momento do upload. Ficam em claro
    // por serem informacao publica do certificado e permitirem consulta rapida
    // de validade sem precisar decifrar nada.

    /// <summary>Subject completo (campo CN inclui razao social e CNPJ).</summary>
    public string Subject { get; set; } = string.Empty;
    public string Emissor { get; set; } = string.Empty;

    /// <summary>Numero de serie em hexadecimal.</summary>
    public string NumeroSerie { get; set; } = string.Empty;

    /// <summary>Thumbprint SHA-1, usado para deduplicacao de upload.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    public DateTime ValidoDe { get; set; }
    public DateTime ValidoAte { get; set; }

    /// <summary>CNPJ extraido da extensao OtherName do certificado ICP-Brasil.</summary>
    public string? CnpjCertificado { get; set; }

    /// <summary>
    /// Apenas um certificado por empresa fica ativo. O upload de um novo
    /// certificado desativa o anterior de forma transacional.
    /// </summary>
    public bool Ativo { get; set; } = true;

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    /// <summary>Vencido em relacao ao instante informado.</summary>
    public bool EstaVencido(DateTime referenciaUtc) => referenciaUtc > ValidoAte;

    /// <summary>Dias restantes de validade, negativo se ja vencido.</summary>
    public int DiasParaVencer(DateTime referenciaUtc) =>
        (int)Math.Floor((ValidoAte - referenciaUtc).TotalDays);
}
