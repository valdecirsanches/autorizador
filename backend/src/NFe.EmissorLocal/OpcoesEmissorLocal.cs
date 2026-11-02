namespace NFe.EmissorLocal;

/// <summary>Configuracao (secao "EmissorLocal" do appsettings.json ou variaveis EmissorLocal__Chave).</summary>
public sealed class OpcoesEmissorLocal
{
    /// <summary>Endereco de escuta. So localhost por padrao: o caixa chama do proprio PC.</summary>
    public string Url { get; set; } = "http://127.0.0.1:5240";

    /// <summary>
    /// Token que o caixa envia em "Authorization: Bearer ...". Obrigatorio: sem
    /// ele, qualquer pagina aberta no navegador do caixa poderia emitir notas.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>Origens (sites) autorizadas a chamar o emissor pelo navegador (CORS).</summary>
    public string[] OrigensPermitidas { get; set; } = [];

    /// <summary>Arquivo .pfx do certificado A1 da loja.</summary>
    public string? CertificadoArquivo { get; set; }

    /// <summary>Senha do .pfx (prefira a variavel de ambiente EmissorLocal__CertificadoSenha).</summary>
    public string? CertificadoSenha { get; set; }

    /// <summary>
    /// Alternativa ao .pfx: impressao digital de um certificado instalado no
    /// Windows (CurrentUser\My ou LocalMachine\My), inclusive A3.
    /// </summary>
    public string? CertificadoThumbprint { get; set; }

    /// <summary>Pasta da fila. Vazio = %ProgramData%\NFeEmissorLocal\fila (Windows) ou ./fila.</summary>
    public string? PastaFila { get; set; }

    /// <summary>API central que recebe as notas para transmitir (ex.: https://nfe-api.exemplo.com.br).</summary>
    public string? ApiCentralUrl { get; set; }

    /// <summary>Chave de app (nfeapp_...) ou token da API central.</summary>
    public string? ApiCentralToken { get; set; }

    /// <summary>Intervalo entre tentativas de transmissao, em segundos.</summary>
    public int IntervaloTransmissaoSegundos { get; set; } = 60;

    /// <summary>xJust padrao das notas em contingencia (15 a 256 caracteres).</summary>
    public string JustificativaContingencia { get; set; } =
        "Sem comunicacao com a SEFAZ para autorizacao on-line";

    public string PastaFilaEfetiva()
    {
        if (!string.IsNullOrWhiteSpace(PastaFila)) return PastaFila!;
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NFeEmissorLocal", "fila");
        return Path.Combine(AppContext.BaseDirectory, "fila");
    }

    /// <summary>Problemas de configuracao que impedem o emissor de subir.</summary>
    public List<string> Problemas()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < 16)
            p.Add("EmissorLocal:Token obrigatorio (minimo 16 caracteres).");
        if (string.IsNullOrWhiteSpace(CertificadoArquivo) && string.IsNullOrWhiteSpace(CertificadoThumbprint))
            p.Add("Informe EmissorLocal:CertificadoArquivo (.pfx) ou EmissorLocal:CertificadoThumbprint.");
        if (JustificativaContingencia.Trim().Length is < 15 or > 256)
            p.Add("EmissorLocal:JustificativaContingencia deve ter de 15 a 256 caracteres.");
        if (IntervaloTransmissaoSegundos < 10)
            p.Add("EmissorLocal:IntervaloTransmissaoSegundos deve ser de pelo menos 10.");
        return p;
    }
}
