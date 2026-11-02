using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace NFe.Sefaz.Seguranca;

/// <summary>
/// CNPJ do titular de um certificado e-CNPJ (ICP-Brasil), sem depender de banco.
/// Le a extensao OtherName 2.16.76.1.3.3 do SubjectAlternativeName e, se ela
/// nao existir, o sufixo ":CNPJ" do CN ("RAZAO SOCIAL:12345678000195").
/// </summary>
public static class CnpjCertificado
{
    private const string OidCnpj = "2.16.76.1.3.3";

    public static string? Extrair(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);

        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // SubjectAlternativeName
            var raw = ext.RawData;
            var alvo = OidParaBytes(OidCnpj);
            var idx = IndexOf(raw, alvo);
            if (idx < 0) continue;

            // Apos o OID vem [0] EXPLICIT com um OCTET STRING/PrintableString.
            var p = idx + alvo.Length;
            while (p < raw.Length - 2)
            {
                var tag = raw[p];
                var len = raw[p + 1];
                if (len == 14 && p + 2 + len <= raw.Length)
                {
                    var candidato = System.Text.Encoding.ASCII.GetString(raw, p + 2, len);
                    if (candidato.All(char.IsDigit)) return candidato;
                }
                if (tag is 0xA0 or 0x04 or 0x13 or 0x0C or 0x16) { p += 2; continue; }
                break;
            }
        }

        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "";
        var m = Regex.Match(cn, @":(\d{14})\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static byte[] OidParaBytes(string oid)
    {
        var partes = oid.Split('.').Select(int.Parse).ToArray();
        var bytes = new List<byte> { (byte)(partes[0] * 40 + partes[1]) };
        foreach (var v in partes.Skip(2))
        {
            if (v < 128) { bytes.Add((byte)v); continue; }
            var pilha = new Stack<byte>();
            var val = v;
            pilha.Push((byte)(val & 0x7F));
            val >>= 7;
            while (val > 0) { pilha.Push((byte)((val & 0x7F) | 0x80)); val >>= 7; }
            bytes.AddRange(pilha);
        }
        return new byte[] { 0x06, (byte)bytes.Count }.Concat(bytes).ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
