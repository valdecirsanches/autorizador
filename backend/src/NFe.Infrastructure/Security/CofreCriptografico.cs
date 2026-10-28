using System.Security.Cryptography;
using System.Text;

namespace NFe.Infrastructure.Security;

/// <summary>Resultado de uma operacao de cifragem AES-GCM.</summary>
public readonly record struct DadoCifrado(byte[] Conteudo, byte[] Nonce, byte[] Tag);

public sealed class CofreException : Exception
{
    public CofreException(string message) : base(message) { }
    public CofreException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Cifragem autenticada AES-256-GCM para material sensivel de certificados.
///
/// Decisoes de projeto:
/// - GCM em vez de CBC: fornece autenticidade junto com confidencialidade, o que
///   impede que um atacante com acesso de escrita ao banco substitua um PFX por
///   outro sem que a decifragem falhe.
/// - Nonce de 96 bits gerado por operacao com RandomNumberGenerator. Reutilizar
///   nonce com a mesma chave em GCM quebra a seguranca do modo, por isso nunca
///   ha nonce derivado nem fixo.
/// - Associated data vincula o criptograma ao contexto (empresa e finalidade),
///   de modo que um blob nao possa ser transplantado de uma empresa para outra.
/// </summary>
public sealed class CofreCriptografico
{
    private const int TamanhoChaveBytes = 32; // AES-256
    private const int TamanhoNonceBytes = 12; // 96 bits, recomendado para GCM
    private const int TamanhoTagBytes = 16;   // 128 bits

    private readonly byte[] _chaveMestra;

    private CofreCriptografico(byte[] chaveMestra) => _chaveMestra = chaveMestra;

    /// <summary>
    /// "Impressao digital" NAO SECRETA da chave mestra ativa: os 8 primeiros
    /// hex do SHA-256 da chave. Serve para o operador CONFIRMAR, sem expor o
    /// segredo, se a chave usada agora e a mesma de quando o certificado foi
    /// cifrado. Como e um hash de mao unica, nao revela a chave.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var hash = SHA256.HashData(_chaveMestra);
            return Convert.ToHexString(hash).ToLowerInvariant()[..8];
        }
    }

    /// <summary>
    /// Constroi o cofre a partir da chave mestra em base64. Falha de forma
    /// explicita e imediata se a chave for ausente ou tiver tamanho incorreto:
    /// e preferivel nao subir a aplicacao a operar com criptografia fraca.
    /// </summary>
    public static CofreCriptografico DeBase64(string? chaveBase64)
    {
        if (string.IsNullOrWhiteSpace(chaveBase64))
            throw new CofreException(
                "Chave mestra ausente. Defina NFE_CERT_MASTER_KEY com 32 bytes em " +
                "base64. Gere com: openssl rand -base64 32");

        byte[] chave;
        try
        {
            chave = Convert.FromBase64String(chaveBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new CofreException(
                "Chave mestra nao esta em base64 valido. " +
                "Gere com: openssl rand -base64 32", ex);
        }

        if (chave.Length != TamanhoChaveBytes)
            throw new CofreException(
                $"Chave mestra deve ter exatamente {TamanhoChaveBytes} bytes " +
                $"({TamanhoChaveBytes * 8} bits) apos decodificar o base64, " +
                $"mas tem {chave.Length}. Gere com: openssl rand -base64 32");

        return new CofreCriptografico(chave);
    }

    /// <summary>Gera uma chave mestra nova em base64, para bootstrap.</summary>
    public static string GerarChaveMestraBase64()
    {
        var chave = RandomNumberGenerator.GetBytes(TamanhoChaveBytes);
        return Convert.ToBase64String(chave);
    }

    public DadoCifrado Cifrar(byte[] plaintext, string contexto)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(TamanhoNonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TamanhoTagBytes];
        var aad = Encoding.UTF8.GetBytes(contexto);

        using var gcm = new AesGcm(_chaveMestra, TamanhoTagBytes);
        gcm.Encrypt(nonce, plaintext, cipher, tag, aad);

        return new DadoCifrado(cipher, nonce, tag);
    }

    public DadoCifrado CifrarTexto(string plaintext, string contexto) =>
        Cifrar(Encoding.UTF8.GetBytes(plaintext), contexto);

    public byte[] Decifrar(DadoCifrado dado, string contexto)
    {
        if (dado.Nonce.Length != TamanhoNonceBytes)
            throw new CofreException(
                $"Nonce com tamanho invalido: {dado.Nonce.Length}, esperado {TamanhoNonceBytes}.");
        if (dado.Tag.Length != TamanhoTagBytes)
            throw new CofreException(
                $"Tag com tamanho invalido: {dado.Tag.Length}, esperado {TamanhoTagBytes}.");

        var plain = new byte[dado.Conteudo.Length];
        var aad = Encoding.UTF8.GetBytes(contexto);

        try
        {
            using var gcm = new AesGcm(_chaveMestra, TamanhoTagBytes);
            gcm.Decrypt(dado.Nonce, dado.Conteudo, dado.Tag, plain, aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CofreException(
                "Falha na verificacao de autenticidade ao decifrar. Causa quase certa: " +
                "a chave mestra ativa NAO e a mesma usada quando o certificado foi " +
                $"salvo. Chave ativa agora (fingerprint): {Fingerprint}. Solucao: " +
                "reenvie o certificado A1 com esta chave ativa. (Contexto/AAD ja foi " +
                "verificado e confere; nao ha corrupcao no banco em uso normal.)", ex);
        }

        return plain;
    }

    public string DecifrarTexto(DadoCifrado dado, string contexto) =>
        Encoding.UTF8.GetString(Decifrar(dado, contexto));

    /// <summary>
    /// Contexto de associated data de um certificado. Inclui a empresa para que
    /// o criptograma seja inutilizavel fora dela.
    /// </summary>
    public static string ContextoCertificado(Guid empresaId, string finalidade) =>
        $"nfe:cert:{empresaId:D}:{finalidade}";
}
