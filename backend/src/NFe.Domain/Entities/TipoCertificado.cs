namespace NFe.Domain.Entities;

/// <summary>
/// Forma de custodia da chave privada do certificado. Os valores sao persistidos
/// como inteiro e nao devem ser renumerados.
/// </summary>
public enum TipoCertificado
{
    /// <summary>Arquivo PFX/P12 cifrado no banco; a API assina e fala TLS localmente.</summary>
    A1 = 1,

    /// <summary>
    /// Chave em token/smartcard na maquina do cliente. A API guarda so a parte
    /// publica e delega assinatura e transporte ao agente pareado.
    /// </summary>
    A3 = 3
}
