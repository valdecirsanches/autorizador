namespace NFe.Domain.Modelos;

/// <summary>
/// Documento fiscal referenciado pela nota (grupo NFref do ide). Usado em
/// devolucao de mercadoria, nota complementar e nota de ajuste.
/// </summary>
public sealed class DocumentoReferenciadoRequest
{
    /// <summary>Chave de acesso da NF-e referenciada (refNFe, 44 digitos com DV valido).</summary>
    public string ChaveNFe { get; set; } = string.Empty;
}
