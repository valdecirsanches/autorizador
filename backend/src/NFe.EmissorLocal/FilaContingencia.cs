using System.Text.Json;
using System.Text.Json.Serialization;

namespace NFe.EmissorLocal;

/// <summary>Situacao de uma NFC-e na fila do emissor local.</summary>
public static class SituacaoFila
{
    /// <summary>Assinada em contingencia, aguardando transmissao.</summary>
    public const string Pendente = "pendente";
    /// <summary>Autorizada pela SEFAZ (via API central).</summary>
    public const string Autorizada = "autorizada";
    /// <summary>Rejeitada: precisa ser corrigida e emitida de novo.</summary>
    public const string Rejeitada = "rejeitada";
    /// <summary>Denegada: o numero nao pode ser reaproveitado.</summary>
    public const string Denegada = "denegada";

    public static bool Final(string s) => s is Autorizada or Rejeitada or Denegada;
}

/// <summary>NFC-e emitida em contingencia off-line e o andamento da transmissao.</summary>
public sealed class ItemFila
{
    public string Chave { get; set; } = "";
    /// <summary>Id da empresa na API central (o mesmo da rota de emissao).</summary>
    public Guid EmpresaId { get; set; }
    public string Cnpj { get; set; } = "";
    public int Ambiente { get; set; }
    public int Serie { get; set; }
    public long Numero { get; set; }
    public decimal ValorTotal { get; set; }
    public DateTimeOffset DataEmissao { get; set; }
    /// <summary>Prazo para transmitir: dhEmi + 24 h (BA, CE, PE e SC: 1o dia util seguinte).</summary>
    public DateTimeOffset PrazoTransmissao { get; set; }
    /// <summary>XML assinado, com o QR Code no infNFeSupl. Nunca alterar.</summary>
    public string Xml { get; set; } = "";
    public string QrCode { get; set; } = "";
    public string UrlChave { get; set; } = "";

    public string Situacao { get; set; } = SituacaoFila.Pendente;
    public int Tentativas { get; set; }
    public DateTimeOffset? UltimaTentativa { get; set; }
    public string? UltimoErro { get; set; }

    // Preenchidos quando a API central responde.
    public Guid? DocumentoId { get; set; }
    public string? Protocolo { get; set; }
    public string? CodigoStatus { get; set; }
    public string? Motivo { get; set; }
    /// <summary>nfeProc (XML autorizado) devolvido pela API central.</summary>
    public string? XmlAutorizado { get; set; }

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset AtualizadoEm { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool PrazoVencido => Situacao == SituacaoFila.Pendente && DateTimeOffset.Now > PrazoTransmissao;
}

/// <summary>
/// Fila em disco: um JSON por NFC-e ({chave}.json). Gravacao atomica (arquivo
/// temporario + troca) para nao perder nota se o PC desligar no meio. Notas
/// rejeitadas substituidas por nova emissao vao para a subpasta "historico".
/// </summary>
public sealed class FilaContingencia
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _pasta;
    private readonly object _trava = new();

    public FilaContingencia(string pasta)
    {
        _pasta = pasta;
        Directory.CreateDirectory(_pasta);
        Directory.CreateDirectory(Path.Combine(_pasta, "historico"));
    }

    public string Pasta => _pasta;

    public IReadOnlyList<ItemFila> Listar()
    {
        lock (_trava)
        {
            var itens = new List<ItemFila>();
            foreach (var arq in Directory.EnumerateFiles(_pasta, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<ItemFila>(File.ReadAllText(arq), Json);
                    if (item is not null && item.Chave.Length == 44) itens.Add(item);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // Arquivo corrompido ou em uso: ignorado nesta leitura.
                }
            }
            return itens.OrderBy(i => i.DataEmissao).ToList();
        }
    }

    public ItemFila? Obter(string chave)
    {
        var c = new string((chave ?? "").Where(char.IsDigit).ToArray());
        if (c.Length != 44) return null;
        var arq = Path.Combine(_pasta, c + ".json");
        lock (_trava)
        {
            if (!File.Exists(arq)) return null;
            return JsonSerializer.Deserialize<ItemFila>(File.ReadAllText(arq), Json);
        }
    }

    /// <summary>Nota ja emitida com o mesmo CNPJ, ambiente, serie e numero (a mais recente).</summary>
    public ItemFila? BuscarPorNumero(string cnpj, int ambiente, int serie, long numero) =>
        Listar()
            .Where(i => i.Cnpj == cnpj && i.Ambiente == ambiente && i.Serie == serie && i.Numero == numero)
            .OrderByDescending(i => i.CriadoEm)
            .FirstOrDefault();

    public void Salvar(ItemFila item)
    {
        item.AtualizadoEm = DateTimeOffset.Now;
        var destino = Path.Combine(_pasta, item.Chave + ".json");
        var tmp = destino + ".tmp";
        lock (_trava)
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(item, Json));
            File.Move(tmp, destino, overwrite: true);
        }
    }

    /// <summary>Move a nota para o historico (ex.: rejeitada que foi emitida de novo).</summary>
    public void Arquivar(ItemFila item)
    {
        var origem = Path.Combine(_pasta, item.Chave + ".json");
        var destino = Path.Combine(_pasta, "historico", $"{item.Chave}-{DateTime.Now:yyyyMMddHHmmss}.json");
        lock (_trava)
        {
            if (File.Exists(origem)) File.Move(origem, destino, overwrite: true);
        }
    }
}
