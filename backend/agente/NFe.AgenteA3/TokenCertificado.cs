using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NFe.AgenteA3;

/// <summary>
/// Acesso ao certificado A3 pelo repositorio do Windows (o driver do fabricante
/// publica o certificado do token la). A chave privada nunca sai do token: aqui
/// so pedimos a ele que assine.
///
/// O PIN e tentado UMA vez. Se o token recusar, o agente para de usa-lo ate ser
/// reiniciado: tentar de novo com PIN errado bloqueia o dispositivo.
/// </summary>
public sealed class TokenCertificado : IDisposable
{
    private readonly string _thumbprint;
    private readonly string? _pin;
    private readonly object _trava = new();

    private X509Certificate2? _certificado;
    private RSA? _rsa;
    private string? _bloqueio;

    public TokenCertificado(string thumbprint, string? pin)
    {
        _thumbprint = thumbprint;
        _pin = pin;
    }

    public static IReadOnlyList<X509Certificate2> Listar()
    {
        var lista = new List<X509Certificate2>();
        foreach (var local in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, local);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var c in store.Certificates)
            {
                if (c.HasPrivateKey && c.NotAfter > DateTime.Now
                    && lista.All(x => x.Thumbprint != c.Thumbprint))
                    lista.Add(c);
            }
        }
        return lista;
    }

    public X509Certificate2 Certificado
    {
        get { lock (_trava) { Abrir(); return _certificado!; } }
    }

    public byte[] AssinarHash(byte[] hash, HashAlgorithmName algoritmo)
    {
        lock (_trava)
        {
            if (_bloqueio is not null) throw new InvalidOperationException(_bloqueio);
            Abrir();

            try
            {
                return _rsa!.SignHash(hash, algoritmo, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException ex)
            {
                _bloqueio =
                    "O token recusou a operacao (PIN incorreto ou token removido). Para proteger " +
                    "o token contra bloqueio, o agente nao tentara de novo: confira o PIN com " +
                    "'NFeAgenteA3 configurar' e reinicie o agente.";
                throw new InvalidOperationException(_bloqueio, ex);
            }
        }
    }

    private void Abrir()
    {
        if (_rsa is not null) return;

        _certificado = Localizar()
            ?? throw new InvalidOperationException(
                "Certificado nao encontrado. Verifique se o token esta plugado e o driver instalado.");

        var rsa = _certificado.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("O certificado nao expoe chave privada RSA.");

        _rsa = _pin is null ? rsa : AplicarPin(rsa, _pin);
    }

    private X509Certificate2? Localizar()
    {
        foreach (var local in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, local);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var achados = store.Certificates.Find(
                X509FindType.FindByThumbprint, _thumbprint, validOnly: false);
            if (achados.Count > 0) return achados[0];
        }
        return null;
    }

    private static RSA AplicarPin(RSA rsa, string pin)
    {
        // Driver moderno (CNG / Smart Card KSP).
        if (rsa is RSACng cng)
        {
            var valor = Encoding.Unicode.GetBytes(pin + "\0");
            cng.Key.SetProperty(new CngProperty("SmartCardPin", valor, CngPropertyOptions.None));
            return cng;
        }

        // Driver legado (CSP).
        if (rsa is RSACryptoServiceProvider csp)
        {
            var info = csp.CspKeyContainerInfo;
            var senha = new SecureString();
            foreach (var ch in pin) senha.AppendChar(ch);
            senha.MakeReadOnly();

            var parametros = new CspParameters(info.ProviderType, info.ProviderName, info.KeyContainerName)
            {
                KeyPassword = senha,
                Flags = CspProviderFlags.UseExistingKey,
            };
            return new RSACryptoServiceProvider(parametros);
        }

        return rsa;
    }

    public void Dispose()
    {
        _rsa?.Dispose();
        _certificado?.Dispose();
    }
}
