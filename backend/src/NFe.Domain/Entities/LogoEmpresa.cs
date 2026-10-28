namespace NFe.Domain.Entities;

/// <summary>
/// Logotipo do emitente, impresso no DANFE. Fica em tabela separada da Empresa
/// (relacao 1:1) porque os bytes da imagem sao pesados e a lista de empresas e
/// carregada com frequencia em varias telas: manter o blob fora de "empresa"
/// evita trazer a imagem em toda consulta.
///
/// Sem logo cadastrado, o DANFE imprime apenas o texto do emitente (fallback).
/// </summary>
public class LogoEmpresa
{
    /// <summary>Chave primaria e, ao mesmo tempo, FK para a empresa (1:1).</summary>
    public Guid EmpresaId { get; set; }

    /// <summary>Bytes da imagem (PNG ou JPEG).</summary>
    public byte[] Conteudo { get; set; } = Array.Empty<byte>();

    /// <summary>MIME type, ex.: image/png, image/jpeg.</summary>
    public string ContentType { get; set; } = "image/png";

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public Empresa? Empresa { get; set; }
}
