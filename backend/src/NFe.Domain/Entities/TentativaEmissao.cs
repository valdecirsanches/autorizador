namespace NFe.Domain.Entities;

/// <summary>
/// Registro imutavel de CADA tentativa de emissao — inclusive as que falham
/// antes de virar um <see cref="DocumentoFiscal"/> (erro de validacao de campos,
/// certificado, schema XSD, resolucao de servico da SEFAZ).
///
/// Existe para responder "o que aconteceu com a nota que meu app enviou?" mesmo
/// quando nenhum documento chegou a ser persistido. Nunca e atualizado: cada
/// POST de emissao gera exatamente uma linha.
/// </summary>
public class TentativaEmissao
{
    public long Id { get; set; }

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    /// <summary>
    /// Documento gerado, quando a tentativa chegou a persistir uma nota. Null
    /// nas falhas iniciais (validacao/certificado/XSD), que sao justamente as
    /// que antes ficavam invisiveis.
    /// </summary>
    public Guid? DocumentoFiscalId { get; set; }
    public DocumentoFiscal? DocumentoFiscal { get; set; }

    public int Ambiente { get; set; }
    public int? Serie { get; set; }
    public long? Numero { get; set; }

    /// <summary>Chave de idempotencia enviada pelo cliente, se houve.</summary>
    public string? ChaveIdempotencia { get; set; }

    /// <summary>true = autorizada; false = qualquer falha.</summary>
    public bool Sucesso { get; set; }

    /// <summary>
    /// Fase em que a tentativa terminou: "Validacao", "Certificado", "Schema",
    /// "ResolucaoServico", "Transmissao", "Rejeicao", "Autorizada".
    /// </summary>
    public string Fase { get; set; } = string.Empty;

    /// <summary>Codigo de status da SEFAZ (cStat) quando aplicavel.</summary>
    public string? CodigoStatus { get; set; }

    /// <summary>Mensagem legivel do resultado (motivo da rejeicao/erro).</summary>
    public string Mensagem { get; set; } = string.Empty;

    /// <summary>
    /// Detalhes adicionais — por exemplo a lista de campos invalidos, separados
    /// por " | ". Ajuda o operador a corrigir o app emissor.
    /// </summary>
    public string? Detalhe { get; set; }

    /// <summary>Chave de acesso, quando ja calculada.</summary>
    public string? ChaveAcesso { get; set; }

    public string? DestinatarioDocumento { get; set; }
    public string? DestinatarioNome { get; set; }
    public decimal? ValorTotal { get; set; }

    /// <summary>Corpo JSON original recebido no POST de emissao.</summary>
    public string? JsonRecebido { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
