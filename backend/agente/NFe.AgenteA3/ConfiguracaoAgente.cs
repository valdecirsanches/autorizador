using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NFe.AgenteA3;

/// <summary>
/// Configuracao local do agente. O PIN do token fica cifrado com DPAPI (escopo do
/// usuario do Windows): so quem roda o agente nesta conta consegue decifrar, e ele
/// nunca e enviado a API.
/// </summary>
public sealed class ConfiguracaoAgente
{
    public string ApiUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
    public string PinProtegido { get; set; } = string.Empty;

    /// <summary>Aceita cadeia ICP-Brasil ausente no Windows. Use so se a SEFAZ nao validar.</summary>
    public bool PermitirCadeiaNaoConfiavel { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly byte[] Entropia = "NFeAgenteA3.v1"u8.ToArray();

    public static string Caminho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NFeAgenteA3", "agente.json");

    public static ConfiguracaoAgente? Carregar()
    {
        if (!File.Exists(Caminho)) return null;
        return JsonSerializer.Deserialize<ConfiguracaoAgente>(File.ReadAllText(Caminho));
    }

    public void Salvar()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        File.WriteAllText(Caminho, JsonSerializer.Serialize(this, Json));
    }

    public void DefinirPin(string pin)
    {
        var cifrado = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(pin), Entropia, DataProtectionScope.CurrentUser);
        PinProtegido = Convert.ToBase64String(cifrado);
    }

    public string? LerPin()
    {
        if (string.IsNullOrEmpty(PinProtegido)) return null;
        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(PinProtegido), Entropia, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public Uri UriWebSocket()
    {
        var b = new UriBuilder(ApiUrl.TrimEnd('/') + "/agente/ws");
        b.Scheme = b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss";
        b.Port = new Uri(ApiUrl).IsDefaultPort ? -1 : new Uri(ApiUrl).Port;
        return b.Uri;
    }
}
