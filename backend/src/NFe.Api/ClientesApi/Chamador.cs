namespace NFe.Api.ClientesApi;

/// <summary>
/// App cadastrado para consumir a API com chave propria.
/// </summary>
public sealed record ClienteApi(
    Guid Id,
    string Nome,
    string IdentificadorChave,
    bool PodeCadastrarEmpresas,
    bool Ativo,
    IReadOnlySet<Guid> Empresas,
    DateTime CriadoEm,
    DateTime? AtualizadoEm,
    DateTime? UltimoUsoEm);

/// <summary>
/// Quem esta chamando a requisicao atual. Fica em HttpContext.Items e e lido
/// pela autorizacao, pelos endpoints que filtram dados e pela auditoria.
///
/// - Mestre: usou o NFE_API_TOKEN (painel e integracoes antigas). Acesso total,
///   exatamente como era antes das chaves por app.
/// - App: usou uma chave "nfeapp_..."; enxerga so as empresas liberadas para ele.
/// </summary>
public sealed class Chamador
{
    private const string ChaveItem = "NFe.Api.Chamador";

    private Chamador(ClienteApi? cliente) => Cliente = cliente;

    public static Chamador Mestre { get; } = new(null);

    public static Chamador DeApp(ClienteApi cliente) => new(cliente);

    /// <summary>Preenchido apenas quando o chamador e um app.</summary>
    public ClienteApi? Cliente { get; }

    public bool EhMestre => Cliente is null;

    public bool PodeAcessar(Guid empresaId) =>
        EhMestre || Cliente!.Empresas.Contains(empresaId);

    public bool PodeCadastrarEmpresas => EhMestre || Cliente!.PodeCadastrarEmpresas;

    /// <summary>Texto gravado na coluna "usuario" do monitor da API.</summary>
    public string? NomeAuditoria => Cliente is null ? null : $"App: {Cliente.Nome}";

    public void Gravar(HttpContext ctx) => ctx.Items[ChaveItem] = this;

    /// <summary>
    /// Chamador da requisicao. Sem registro (rotas livres como /health), trata
    /// como mestre para nao alterar o comportamento desses endpoints.
    /// </summary>
    public static Chamador De(HttpContext ctx) =>
        ctx.Items.TryGetValue(ChaveItem, out var valor) && valor is Chamador c ? c : Mestre;

    /// <summary>Retorna o chamador apenas se ele foi registrado pela autenticacao.</summary>
    public static Chamador? Registrado(HttpContext ctx) =>
        ctx.Items.TryGetValue(ChaveItem, out var valor) ? valor as Chamador : null;
}
