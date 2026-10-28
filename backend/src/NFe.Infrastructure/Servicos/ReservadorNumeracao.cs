using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Reserva o proximo numero de NF-e por empresa, modelo, serie e ambiente.
///
/// A numeracao fiscal nao admite lacunas nem repeticoes. Sob concorrencia, dois
/// pedidos simultaneos nao podem receber o mesmo numero, por isso a reserva usa
/// lock pessimista de linha (SELECT ... FOR UPDATE) dentro de uma transacao.
/// Homologacao e producao tem contadores totalmente independentes.
/// </summary>
public sealed class ReservadorNumeracao
{
    private readonly NFeDbContext _db;

    public ReservadorNumeracao(NFeDbContext db) => _db = db;

    /// <summary>
    /// Reserva e retorna o proximo numero. Deve ser chamado dentro de uma
    /// transacao ja aberta pelo orquestrador, para que a reserva so se torne
    /// definitiva se o documento chegar a ser persistido — evitando "queimar"
    /// numeros em falhas antes da emissao.
    /// </summary>
    public async Task<long> ReservarProximoAsync(
        Guid empresaId,
        ModeloDocumento modelo,
        int serie,
        TipoAmbiente ambiente,
        CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "ReservarProximoAsync exige uma transacao ativa para garantir o lock.");

        // FOR UPDATE bloqueia a linha do contador ate o commit. Outra reserva
        // concorrente fica em espera em vez de ler o mesmo UltimoNumero.
        var contador = await _db.SeriesNumeracao
            .FromSqlRaw(
                """
                SELECT * FROM serie_numeracao
                WHERE empresa_id = {0} AND modelo = {1} AND serie = {2} AND ambiente = {3}
                FOR UPDATE
                """,
                empresaId, (int)modelo, serie, (int)ambiente)
            .FirstOrDefaultAsync(ct);

        if (contador is null)
        {
            // Primeira nota da serie. O INSERT concorrente e barrado pelo indice
            // unico (empresa, modelo, serie, ambiente); o perdedor cai em
            // DbUpdateException e o orquestrador retenta.
            contador = new SerieNumeracao
            {
                EmpresaId = empresaId,
                Modelo = modelo,
                Serie = serie,
                Ambiente = ambiente,
                UltimoNumero = 0
            };
            _db.SeriesNumeracao.Add(contador);
        }

        contador.UltimoNumero += 1;
        contador.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return contador.UltimoNumero;
    }

    /// <summary>
    /// Inicializa (ou ajusta) o contador de uma serie, definindo qual sera o
    /// PROXIMO numero a emitir. Uso tipico: migracao de outro sistema, em que a
    /// numeracao ja avancou e precisa continuar de onde parou.
    ///
    /// Abre a propria transacao com lock pessimista. Recusa definir um proximo
    /// numero que reutilizaria uma numeracao ja emitida nesta serie/ambiente
    /// (a SEFAZ rejeitaria a chave duplicada): o proximo numero deve ser maior
    /// que o maior numero ja usado.
    /// </summary>
    /// <returns>O ultimo numero reservado apos o ajuste (proximoNumero - 1).</returns>
    public async Task<long> DefinirProximoNumeroAsync(
        Guid empresaId,
        ModeloDocumento modelo,
        int serie,
        TipoAmbiente ambiente,
        long proximoNumero,
        CancellationToken ct = default)
    {
        if (proximoNumero < 1)
            throw new NumeracaoInvalidaException("O proximo numero deve ser maior ou igual a 1.");
        if (serie is < 0 or > 999)
            throw new NumeracaoInvalidaException("A serie deve estar entre 0 e 999.");

        // Nunca deixar o proximo numero cair sobre uma numeracao ja emitida.
        var maiorEmitido = await _db.DocumentosFiscais
            .Where(d => d.EmpresaId == empresaId
                && d.Modelo == modelo
                && d.Serie == serie
                && d.Ambiente == ambiente)
            .Select(d => (long?)d.Numero)
            .MaxAsync(ct) ?? 0;

        if (proximoNumero <= maiorEmitido)
            throw new NumeracaoInvalidaException(
                $"O proximo numero ({proximoNumero}) precisa ser maior que o maior numero ja " +
                $"emitido nesta serie ({maiorEmitido}), para nao repetir uma NF-e existente.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var contador = await _db.SeriesNumeracao
            .FromSqlRaw(
                """
                SELECT * FROM serie_numeracao
                WHERE empresa_id = {0} AND modelo = {1} AND serie = {2} AND ambiente = {3}
                FOR UPDATE
                """,
                empresaId, (int)modelo, serie, (int)ambiente)
            .FirstOrDefaultAsync(ct);

        if (contador is null)
        {
            contador = new SerieNumeracao
            {
                EmpresaId = empresaId,
                Modelo = modelo,
                Serie = serie,
                Ambiente = ambiente,
                UltimoNumero = proximoNumero - 1
            };
            _db.SeriesNumeracao.Add(contador);
        }
        else
        {
            contador.UltimoNumero = proximoNumero - 1;
        }

        contador.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return contador.UltimoNumero;
    }
}

/// <summary>Erro de regra ao inicializar/ajustar a numeracao de uma serie.</summary>
public sealed class NumeracaoInvalidaException : Exception
{
    public NumeracaoInvalidaException(string message) : base(message) { }
}
