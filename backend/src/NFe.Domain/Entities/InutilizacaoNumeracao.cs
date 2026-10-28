namespace NFe.Domain.Entities;

/// <summary>
/// Registro de uma inutilizacao de faixa de numeracao (servico NfeInutilizacao4,
/// pedido inutNFe). Declara a SEFAZ que uma faixa continua de numeros [inicial,
/// final] de uma serie NAO sera usada — tipicamente por uma quebra de sequencia
/// (falha de sistema que "queimou" numeros sem emitir). E irreversivel e a
/// guarda do XML e obrigatoria, por isso e persistida como trilha append-only,
/// nunca sofrendo update apos a resposta da SEFAZ.
/// </summary>
public class InutilizacaoNumeracao
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmpresaId { get; set; }
    public Empresa? Empresa { get; set; }

    public ModeloDocumento Modelo { get; set; } = ModeloDocumento.NFe;

    /// <summary>Serie de 0 a 999.</summary>
    public int Serie { get; set; }

    /// <summary>Homologacao e producao sao faixas independentes.</summary>
    public TipoAmbiente Ambiente { get; set; }

    /// <summary>Ano (2 digitos) da inutilizacao, compoe o Id do infInut.</summary>
    public int Ano { get; set; }

    public long NumeroInicial { get; set; }
    public long NumeroFinal { get; set; }

    /// <summary>Justificativa (xJust), 15 a 255 caracteres.</summary>
    public string Justificativa { get; set; } = string.Empty;

    // ----------------------------- Retorno SEFAZ ----------------------------

    /// <summary>cStat 102 = numeracao inutilizada com sucesso.</summary>
    public bool Homologado { get; set; }

    /// <summary>Id do infInut: "ID" + cUF + ano + CNPJ + mod + serie + nIni + nFin.</summary>
    public string? IdInutilizacao { get; set; }

    public string? CodigoStatus { get; set; }
    public string? Motivo { get; set; }
    public string? Protocolo { get; set; }
    public DateTime? DataRegistro { get; set; }

    public string? XmlEnviado { get; set; }
    public string? XmlRetorno { get; set; }

    /// <summary>procInutNFe (pedido assinado + retorno), XML de guarda fiscal.</summary>
    public string? XmlInutilizacao { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
