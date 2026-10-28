using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Sefaz.Seguranca;

namespace NFe.Infrastructure.Security;

public sealed class CertificadoException : Exception
{
    public CertificadoException(string message) : base(message) { }
    public CertificadoException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Resultado do cadastro A3: o token em claro so existe neste retorno.</summary>
public sealed record ResultadoCadastroA3(
    Certificado Certificado, AgenteCertificado Agente, string TokenAgente);

/// <summary>
/// Ciclo de vida do certificado A1 e do cadastro do A3: importacao, validacao, cifragem e carga em
/// memoria para uso no TLS mutuo e na assinatura XMLDSig.
///
/// A chave privada nunca e persistida em claro e nunca e escrita em disco: o
/// PFX e carregado com EphemeralKeySet sempre que possivel, evitando deixar
/// residuo no keystore do sistema operacional.
/// </summary>
public sealed class ServicoCertificado
{
    private readonly NFeDbContext _db;
    private readonly CofreCriptografico _cofre;
    private readonly IProvedorAgenteCertificado _agentes;

    public ServicoCertificado(
        NFeDbContext db,
        CofreCriptografico cofre,
        IProvedorAgenteCertificado? agentes = null)
    {
        _db = db;
        _cofre = cofre;
        _agentes = agentes ?? new ProvedorAgenteIndisponivel();
    }

    /// <summary>
    /// Importa um arquivo PFX/P12 para uma empresa. Valida a senha, a presenca
    /// de chave privada e a vigencia antes de persistir qualquer coisa.
    /// Desativa o certificado anterior de forma transacional.
    /// </summary>
    public async Task<Certificado> ImportarAsync(
        Guid empresaId,
        byte[] pfxBytes,
        string senha,
        string descricao,
        CancellationToken ct = default)
    {
        if (pfxBytes is null || pfxBytes.Length == 0)
            throw new CertificadoException("Arquivo do certificado vazio.");

        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(x => x.Id == empresaId, ct)
            ?? throw new CertificadoException($"Empresa {empresaId} nao encontrada.");

        // Carrega para extrair metadados e validar senha/chave privada.
        using var x509 = CarregarX509(pfxBytes, senha);

        if (!x509.HasPrivateKey)
            throw new CertificadoException(
                "O certificado nao contem chave privada. Para assinar a NF-e e " +
                "necessario um arquivo A1 no formato PFX/P12 com a chave privada.");

        var agora = DateTime.UtcNow;
        var validoDe = x509.NotBefore.ToUniversalTime();
        var validoAte = x509.NotAfter.ToUniversalTime();

        if (agora > validoAte)
            throw new CertificadoException(
                $"Certificado vencido em {validoAte:dd/MM/yyyy HH:mm} UTC. " +
                "Nao e possivel importar certificado expirado.");

        var thumbprint = x509.Thumbprint ?? string.Empty;

        // Mesmo certificado (mesmo thumbprint) ja cadastrado nesta empresa: em vez
        // de recusar, REAPROVEITAMOS o registro existente — reativamos e atualizamos
        // senha/PFX/descricao. Assim, reenviar o mesmo certificado (ex.: agora com a
        // senha correta) apenas atualiza o cadastro, sem erro e sem duplicar.
        var existenteCert = await _db.Certificados
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId && c.Thumbprint == thumbprint, ct);

        var cnpjCert = ExtrairCnpj(x509);
        if (cnpjCert is not null &&
            !string.Equals(cnpjCert, empresa.Cnpj, StringComparison.Ordinal))
        {
            throw new CertificadoException(
                $"O CNPJ do certificado ({FormatarCnpj(cnpjCert)}) nao corresponde " +
                $"ao CNPJ da empresa ({FormatarCnpj(empresa.Cnpj)}). A SEFAZ rejeita " +
                "assinatura feita por certificado de outro CNPJ.");
        }

        var ctxPfx = CofreCriptografico.ContextoCertificado(empresaId, "pfx");
        var ctxSenha = CofreCriptografico.ContextoCertificado(empresaId, "senha");

        var pfxCifrado = _cofre.Cifrar(pfxBytes, ctxPfx);
        var senhaCifrada = _cofre.CifrarTexto(senha, ctxSenha);

        var entidade = new Certificado
        {
            EmpresaId = empresaId,
            Descricao = string.IsNullOrWhiteSpace(descricao)
                ? $"A1 {validoAte:MM/yyyy}"
                : descricao.Trim(),
            PfxCifrado = pfxCifrado.Conteudo,
            PfxNonce = pfxCifrado.Nonce,
            PfxTag = pfxCifrado.Tag,
            SenhaCifrada = senhaCifrada.Conteudo,
            SenhaNonce = senhaCifrada.Nonce,
            SenhaTag = senhaCifrada.Tag,
            Subject = Truncar(x509.Subject, 500),
            Emissor = Truncar(x509.Issuer, 500),
            NumeroSerie = x509.SerialNumber ?? string.Empty,
            Thumbprint = thumbprint,
            ValidoDe = validoDe,
            ValidoAte = validoAte,
            CnpjCertificado = cnpjCert,
            Ativo = true,
            CriadoEm = agora
        };

        // Reaproveita a transacao que o chamador ja abriu (ex.: o onboarding cria
        // a empresa e importa o certificado na MESMA transacao). Abrir uma segunda
        // transacao na mesma conexao lanca no MySqlConnector "The connection is
        // already in a transaction and cannot participate in another transaction"
        // — e, ainda pior, pode devolver ao pool uma conexao com transacao aberta,
        // fazendo requisicoes seguintes (ate um cadastro simples) falharem com o
        // mesmo erro. Quando NAO ha transacao ambiente (import avulso de
        // certificado), gerenciamos a nossa.
        var txExterna = _db.Database.CurrentTransaction;
        var tx = txExterna is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Apenas um certificado ativo por empresa.
            await _db.Certificados
                .Where(c => c.EmpresaId == empresaId && c.Ativo)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Ativo, false), ct);

            // ExecuteUpdateAsync grava direto no banco e nao sincroniza o change
            // tracker. Sem recarregar, uma instancia ja rastreada continuaria com
            // Ativo=true em memoria e um SaveChanges posterior poderia reverter a
            // desativacao no banco. Como ha no maximo um ativo por empresa, o custo
            // e trivial.
            var rastreados = _db.ChangeTracker.Entries<Certificado>()
                .Where(e => e.State is not EntityState.Detached and not EntityState.Added
                            && e.Entity.EmpresaId == empresaId)
                .ToList();

            foreach (var entry in rastreados)
            {
                await entry.ReloadAsync(ct);
            }

            Certificado persistido;
            if (existenteCert is not null)
            {
                // Reaproveita o registro ja existente: atualiza os campos que podem
                // ter mudado (senha corrigida, novo PFX com mesma chave, descricao)
                // e reativa. O ReloadAsync acima ja sincronizou o Ativo=false.
                existenteCert.Descricao = entidade.Descricao;
                existenteCert.Tipo = TipoCertificado.A1;
                existenteCert.CertificadoPublico = null;
                existenteCert.PfxCifrado = entidade.PfxCifrado;
                existenteCert.PfxNonce = entidade.PfxNonce;
                existenteCert.PfxTag = entidade.PfxTag;
                existenteCert.SenhaCifrada = entidade.SenhaCifrada;
                existenteCert.SenhaNonce = entidade.SenhaNonce;
                existenteCert.SenhaTag = entidade.SenhaTag;
                existenteCert.Subject = entidade.Subject;
                existenteCert.Emissor = entidade.Emissor;
                existenteCert.NumeroSerie = entidade.NumeroSerie;
                existenteCert.ValidoDe = entidade.ValidoDe;
                existenteCert.ValidoAte = entidade.ValidoAte;
                existenteCert.CnpjCertificado = entidade.CnpjCertificado;
                existenteCert.Ativo = true;
                persistido = existenteCert;
            }
            else
            {
                _db.Certificados.Add(entidade);
                persistido = entidade;
            }

            await _db.SaveChangesAsync(ct);

            // Diagnostico: registra COM QUAL chave (fingerprint) o certificado foi
            // cifrado e salvo. A emissao/leitura tambem loga/erra com o fingerprint
            // ativo. Se os dois numeros forem IGUAIS, o certificado decifra; se
            // forem DIFERENTES, ha duas chaves/dois processos ou o upload bateu em
            // outra API — e o problema fica visivel de imediato nos logs.
            Console.WriteLine(
                $"[cert] Certificado {persistido.Id} (empresa {empresaId}) salvo e "
                + $"cifrado com chave fingerprint {_cofre.Fingerprint}. "
                + "A emissao SO funciona se o fingerprint ativo for este mesmo.");

            // So confirmamos se a transacao e NOSSA. Se veio do chamador, ele
            // commita (ou faz rollback) no ponto certo.
            if (tx is not null)
                await tx.CommitAsync(ct);

            return persistido;
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Cadastra um certificado A3 a partir da parte PUBLICA (.cer/.crt, DER ou
    /// PEM). Valida vigencia e titularidade como no A1, torna o A3 o certificado
    /// ativo da empresa e emite o token de pareamento do agente. O token so e
    /// devolvido aqui, uma unica vez; no banco fica apenas o hash SHA-256.
    /// Parear de novo revoga os agentes anteriores deste certificado.
    /// </summary>
    public async Task<ResultadoCadastroA3> RegistrarA3Async(
        Guid empresaId,
        byte[] certificadoPublico,
        string descricao,
        string nomeAgente,
        CancellationToken ct = default)
    {
        if (certificadoPublico is null || certificadoPublico.Length == 0)
            throw new CertificadoException("Arquivo do certificado vazio.");

        var empresa = await _db.Empresas
            .FirstOrDefaultAsync(x => x.Id == empresaId, ct)
            ?? throw new CertificadoException($"Empresa {empresaId} nao encontrada.");

        using var x509 = CarregarPublico(certificadoPublico);

        if (x509.HasPrivateKey)
            throw new CertificadoException(
                "O arquivo contem chave privada. No A3 envie apenas a parte publica " +
                "(.cer/.crt); a chave permanece no token.");

        if (x509.GetRSAPublicKey() is null)
            throw new CertificadoException(
                "O certificado nao possui chave publica RSA, exigida pela assinatura da NF-e.");

        var agora = DateTime.UtcNow;
        var validoDe = x509.NotBefore.ToUniversalTime();
        var validoAte = x509.NotAfter.ToUniversalTime();

        if (agora > validoAte)
            throw new CertificadoException(
                $"Certificado vencido em {validoAte:dd/MM/yyyy HH:mm} UTC. " +
                "Nao e possivel cadastrar certificado expirado.");

        var cnpjCert = ExtrairCnpj(x509);
        if (cnpjCert is not null &&
            !string.Equals(cnpjCert, empresa.Cnpj, StringComparison.Ordinal))
        {
            throw new CertificadoException(
                $"O CNPJ do certificado ({FormatarCnpj(cnpjCert)}) nao corresponde " +
                $"ao CNPJ da empresa ({FormatarCnpj(empresa.Cnpj)}). A SEFAZ rejeita " +
                "assinatura feita por certificado de outro CNPJ.");
        }

        var thumbprint = x509.Thumbprint ?? string.Empty;
        var existente = await _db.Certificados
            .FirstOrDefaultAsync(c => c.EmpresaId == empresaId && c.Thumbprint == thumbprint, ct);

        var token = GerarTokenAgente();
        var txExterna = _db.Database.CurrentTransaction;
        var tx = txExterna is null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await _db.Certificados
                .Where(c => c.EmpresaId == empresaId && c.Ativo)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Ativo, false), ct);

            // ExecuteUpdateAsync nao sincroniza o change tracker (ver ImportarAsync).
            var rastreados = _db.ChangeTracker.Entries<Certificado>()
                .Where(e => e.State is not EntityState.Detached and not EntityState.Added
                            && e.Entity.EmpresaId == empresaId)
                .ToList();
            foreach (var entry in rastreados)
                await entry.ReloadAsync(ct);

            var descricaoFinal = string.IsNullOrWhiteSpace(descricao)
                ? $"A3 {validoAte:MM/yyyy}"
                : Truncar(descricao.Trim(), 120);

            Certificado cert;
            if (existente is not null)
            {
                cert = existente;
            }
            else
            {
                cert = new Certificado { EmpresaId = empresaId, Thumbprint = thumbprint, CriadoEm = agora };
                _db.Certificados.Add(cert);
            }

            cert.Descricao = descricaoFinal;
            cert.Tipo = TipoCertificado.A3;
            cert.CertificadoPublico = x509.RawData;
            cert.PfxCifrado = Array.Empty<byte>();
            cert.PfxNonce = Array.Empty<byte>();
            cert.PfxTag = Array.Empty<byte>();
            cert.SenhaCifrada = Array.Empty<byte>();
            cert.SenhaNonce = Array.Empty<byte>();
            cert.SenhaTag = Array.Empty<byte>();
            cert.Subject = Truncar(x509.Subject, 500);
            cert.Emissor = Truncar(x509.Issuer, 500);
            cert.NumeroSerie = Truncar(x509.SerialNumber ?? string.Empty, 80);
            cert.ValidoDe = validoDe;
            cert.ValidoAte = validoAte;
            cert.CnpjCertificado = cnpjCert;
            cert.Ativo = true;

            await _db.SaveChangesAsync(ct);

            await _db.AgentesCertificado
                .Where(a => a.CertificadoId == cert.Id && !a.Revogado)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Revogado, true), ct);

            var agente = new AgenteCertificado
            {
                EmpresaId = empresaId,
                CertificadoId = cert.Id,
                Nome = string.IsNullOrWhiteSpace(nomeAgente)
                    ? "Agente A3"
                    : Truncar(nomeAgente.Trim(), 120),
                TokenHash = HashToken(token),
                CriadoEm = agora
            };
            _db.AgentesCertificado.Add(agente);
            await _db.SaveChangesAsync(ct);

            if (tx is not null)
                await tx.CommitAsync(ct);

            return new ResultadoCadastroA3(cert, agente, token);
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync();
        }
    }

    /// <summary>SHA-256 em hexadecimal minusculo: forma como o token e guardado e consultado.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string GerarTokenAgente()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static X509Certificate2 CarregarPublico(byte[] bytes)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException)
        {
            // Aceita tambem .pem/.crt em texto.
            try
            {
                var texto = System.Text.Encoding.ASCII.GetString(bytes);
                if (texto.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                    return X509Certificate2.CreateFromPem(texto);
            }
            catch (CryptographicException) { }

            throw new CertificadoException(
                "Nao foi possivel ler o certificado. Envie a parte publica do A3 no " +
                "formato .cer/.crt (DER ou PEM); nao envie .pfx.");
        }
    }

    /// <summary>
    /// Carrega o certificado ativo da empresa em memoria, pronto para TLS e
    /// assinatura. O chamador e responsavel por descartar a instancia.
    /// </summary>
    public async Task<X509Certificate2> CarregarAtivoAsync(
        Guid empresaId,
        CancellationToken ct = default)
    {
        var cert = await BuscarAtivoValidoAsync(empresaId, ct);

        if (cert.Tipo == TipoCertificado.A3)
            throw new CertificadoException(
                "O certificado ativo e A3 (token). Este fluxo ainda exige certificado A1.");

        return Materializar(cert);
    }

    /// <summary>
    /// Carrega o certificado ativo para os fluxos de NF-e, seja A1 (chave em
    /// memoria) ou A3 (chave no token do cliente, via agente). O chamador
    /// descarta o retorno com `using`.
    /// </summary>
    /// <exception cref="AgenteCertificadoException">A3 com agente desconectado.</exception>
    public async Task<CertificadoSefaz> CarregarAtivoSefazAsync(
        Guid empresaId,
        CancellationToken ct = default)
    {
        var cert = await BuscarAtivoValidoAsync(empresaId, ct);

        if (cert.Tipo != TipoCertificado.A3)
            return new CertificadoLocal(Materializar(cert), donoDoCertificado: true);

        if (cert.CertificadoPublico is not { Length: > 0 })
            throw new CertificadoException(
                "Certificado A3 sem a parte publica cadastrada. Refaca o cadastro do A3.");

        // Falha cedo (antes de abrir transacao ou gravar documento) se o agente
        // estiver offline.
        var agente = _agentes.ObterAgente(empresaId, cert.Id);
        return new CertificadoRemoto(
            X509CertificateLoader.LoadCertificate(cert.CertificadoPublico), agente);
    }

    private async Task<Certificado> BuscarAtivoValidoAsync(Guid empresaId, CancellationToken ct)
    {
        var cert = await _db.Certificados
            .Where(c => c.EmpresaId == empresaId && c.Ativo)
            .OrderByDescending(c => c.CriadoEm)
            .FirstOrDefaultAsync(ct)
            ?? throw new CertificadoException(
                "Nenhum certificado ativo para esta empresa. Faca o upload de um A1.");

        var agora = DateTime.UtcNow;
        if (agora > cert.ValidoAte)
            throw new CertificadoException(
                $"O certificado ativo venceu em {cert.ValidoAte:dd/MM/yyyy HH:mm} UTC. " +
                "Envie um certificado novo para continuar emitindo.");

        return cert;
    }

    /// <summary>Decifra e instancia o X509 a partir da entidade persistida.</summary>
    public X509Certificate2 Materializar(Certificado cert)
    {
        if (cert.Tipo == TipoCertificado.A3)
            throw new CertificadoException(
                "Certificado A3 nao possui PFX: a chave privada fica no token do cliente.");

        var ctxPfx = CofreCriptografico.ContextoCertificado(cert.EmpresaId, "pfx");
        var ctxSenha = CofreCriptografico.ContextoCertificado(cert.EmpresaId, "senha");

        // A decifragem falha (CofreException) quando a NFE_CERT_MASTER_KEY ativa
        // NAO e a mesma que cifrou este certificado. Convertemos em
        // CertificadoException para os endpoints responderem 422 com uma acao
        // clara ("reenvie o A1") em vez de um 500 generico. Incluimos o
        // fingerprint da chave ativa para o operador confirmar qual chave esta
        // em uso sem expor o segredo.
        byte[] pfx;
        string senha;
        try
        {
            pfx = _cofre.Decifrar(
                new DadoCifrado(cert.PfxCifrado, cert.PfxNonce, cert.PfxTag), ctxPfx);
            senha = _cofre.DecifrarTexto(
                new DadoCifrado(cert.SenhaCifrada, cert.SenhaNonce, cert.SenhaTag), ctxSenha);
        }
        catch (CofreException ex)
        {
            throw new CertificadoException(
                "Nao foi possivel abrir o certificado salvo: a chave mestra atual "
                + $"(fingerprint {_cofre.Fingerprint}) nao e a mesma usada quando o "
                + "certificado foi enviado. Reenvie o certificado A1 (arquivo .pfx + "
                + "senha) para regravá-lo com a chave atual.", ex);
        }

        try
        {
            return CarregarX509(pfx, senha);
        }
        finally
        {
            // Reduz a janela de exposicao da chave privada em memoria gerenciada.
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Carrega o PFX preferindo conjunto de chaves efemero, que nao persiste a
    /// chave privada no keystore do sistema. Em plataformas onde a flag nao e
    /// suportada, recorre ao carregamento padrao.
    /// </summary>
    private static X509Certificate2 CarregarX509(byte[] pfxBytes, string senha)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                senha,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            // EphemeralKeySet nao e suportado em todas as plataformas.
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pfxBytes, senha, X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException ex)
            {
                throw new CertificadoException(DiagnosticarFalhaPfx(pfxBytes, ex), ex);
            }
        }
    }

    /// <summary>
    /// Monta uma mensagem que distingue as causas reais da falha ao abrir um PFX,
    /// para o usuario saber se e problema de SENHA, de ARQUIVO ERRADO ou de
    /// ENCRIPTACAO LEGADA. O arquivo ja foi recebido — a falha e sempre no
    /// processamento (decodificacao PKCS#12).
    /// </summary>
    private static string DiagnosticarFalhaPfx(byte[] pfxBytes, CryptographicException ex)
    {
        // PEM/base64 no lugar do binario .pfx.
        if (pfxBytes.Length >= 5)
        {
            var inicioTexto = System.Text.Encoding.ASCII.GetString(pfxBytes, 0, Math.Min(64, pfxBytes.Length));
            if (inicioTexto.Contains("-----BEGIN", StringComparison.Ordinal))
                return "O arquivo enviado esta em formato PEM (texto '-----BEGIN...'), nao no " +
                       "formato PFX/P12 binario. Exporte o certificado A1 como .pfx/.p12 e tente novamente.";
        }

        // DER valido comeca com SEQUENCE em forma longa: 0x30 0x82. Se nao comeca
        // assim, o conteudo NAO e um PKCS#12 — provavelmente e .cer/.crt (so a
        // parte publica), base64, ou o upload corrompeu os bytes.
        var pareceDer = pfxBytes.Length >= 2 && pfxBytes[0] == 0x30 && pfxBytes[1] == 0x82;
        if (!pareceDer)
            return "O arquivo nao parece um PFX/P12 valido (estrutura binaria inesperada). " +
                   "Verifique se voce enviou o .pfx/.p12 com a chave privada — e nao um .cer/.crt " +
                   "(apenas parte publica) ou um arquivo corrompido. " +
                   $"Detalhe tecnico: {ex.Message}";

        // Estrutura PKCS#12 valida, mas a carga falhou. As duas causas classicas:
        // senha incorreta OU encriptacao legada (RC2) recusada pelo OpenSSL 3
        // usado pelo .NET no Linux — comum em certificados A1 brasileiros.
        var msg = ex.Message ?? string.Empty;
        var indicaLegado =
            msg.Contains("algorithm", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("RC2", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("PKCS12", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("PBE", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("legacy", StringComparison.OrdinalIgnoreCase);

        if (indicaLegado)
            return "O arquivo e um PFX/P12 valido, mas usa um algoritmo de encriptacao ANTIGO " +
                   "(comum em certificados A1 brasileiros) que o servidor recusa. A senha pode ate " +
                   "estar correta. Reexporte o certificado com criptografia moderna (AES-256) ou " +
                   $"peca ao emissor um .pfx atualizado. Detalhe tecnico: {ex.Message}";

        return "Nao foi possivel abrir o certificado. A causa mais provavel e SENHA INCORRETA. " +
               "Confirme a senha do certificado A1 e tente novamente. " +
               $"Detalhe tecnico: {ex.Message}";
    }

    /// <summary>
    /// Extrai o CNPJ do titular da extensao OtherName do perfil ICP-Brasil
    /// (OID 2.16.76.1.3.3). Retorna nulo quando a extensao nao esta presente,
    /// caso em que a validacao de titularidade e simplesmente omitida.
    /// </summary>
    public static string? ExtrairCnpj(X509Certificate2 cert)
    {
        const string oidCnpj = "2.16.76.1.3.3";

        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // SubjectAlternativeName

            // A extensao e DER. Em vez de escrever um parser ASN.1 completo,
            // procura-se o OID codificado e le-se o conteudo adjacente.
            var raw = ext.RawData;
            var alvo = OidParaBytes(oidCnpj);
            var idx = IndexOf(raw, alvo);
            if (idx < 0) continue;

            // Apos o OID vem [0] EXPLICIT contendo um OCTET STRING/PrintableString.
            var p = idx + alvo.Length;
            while (p < raw.Length - 2)
            {
                var tag = raw[p];
                var len = raw[p + 1];

                // Conteudo textual de 14 digitos indica o CNPJ.
                if (len == 14 && p + 2 + len <= raw.Length)
                {
                    var candidato = System.Text.Encoding.ASCII.GetString(raw, p + 2, len);
                    if (candidato.All(char.IsDigit)) return candidato;
                }

                if (tag is 0xA0 or 0x04 or 0x13 or 0x0C or 0x16) { p += 2; continue; }
                break;
            }
        }

        return null;
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
        // 0x06 = OBJECT IDENTIFIER
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

    private static string Truncar(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static string FormatarCnpj(string cnpj) =>
        cnpj.Length != 14 ? cnpj :
        $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}";
}
