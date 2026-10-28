using System.IO.Compression;
using System.Text;

namespace NFe.Nfse.Nacional;

/// <summary>
/// A API do Sistema Nacional NFS-e transporta todo XML como GZip + Base64
/// (campos *XmlGZipB64). Este helper concentra a conversao nos dois sentidos.
/// </summary>
public static class GzipBase64
{
    private static readonly UTF8Encoding Utf8SemBom = new(false);

    public static string Compactar(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        var bytes = Utf8SemBom.GetBytes(xml);
        using var saida = new MemoryStream();
        using (var gz = new GZipStream(saida, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(bytes, 0, bytes.Length);

        return Convert.ToBase64String(saida.ToArray());
    }

    public static string Descompactar(string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);

        var comprimido = Convert.FromBase64String(base64.Trim());
        using var entrada = new MemoryStream(comprimido);
        using var gz = new GZipStream(entrada, CompressionMode.Decompress);
        using var leitor = new StreamReader(gz, Utf8SemBom);
        return leitor.ReadToEnd();
    }

    /// <summary>Tenta descompactar; devolve null se o conteudo nao for gzip/base64 valido.</summary>
    public static string? TentarDescompactar(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try { return Descompactar(base64); }
        catch (FormatException) { return null; }
        catch (InvalidDataException) { return null; }
    }
}
