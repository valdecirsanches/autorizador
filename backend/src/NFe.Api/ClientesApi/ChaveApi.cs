using System.Security.Cryptography;
using System.Text;

namespace NFe.Api.ClientesApi;

/// <summary>
/// Geracao e hash das chaves de acesso por app ("clientes da API").
///
/// Formato: <c>nfeapp_</c> + 43 caracteres base64url (32 bytes aleatorios).
/// Exemplo: <c>nfeapp_Q2h4c0R3b1pV...</c>
///
/// Apenas o SHA-256 (hex minusculo) e gravado no banco. Como a chave tem 256
/// bits de entropia, SHA-256 sem salt e suficiente (nao e senha humana) e
/// permite localizar o cliente com um unico SELECT por indice unico.
/// A chave em texto puro so existe na resposta da criacao/rotacao.
/// </summary>
public static class ChaveApi
{
    public const string Prefixo = "nfeapp_";

    /// <summary>Caracteres exibidos para identificar a chave (prefixo + 6).</summary>
    public const int TamanhoIdentificador = 13;

    public static (string Chave, string Identificador, string Hash) Gerar()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var corpo = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var chave = Prefixo + corpo;
        return (chave, chave[..TamanhoIdentificador], Hash(chave));
    }

    public static bool TemFormatoDeChave(string? valor) =>
        valor is not null
        && valor.StartsWith(Prefixo, StringComparison.Ordinal)
        && valor.Length is > 20 and < 200;

    public static string Hash(string chave) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chave))).ToLowerInvariant();
}
