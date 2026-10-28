using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;

namespace NFe.Infrastructure.ServicosNfse;

/// <summary>
/// Reserva o proximo nDPS por empresa, serie e ambiente, com lock pessimista
/// (SELECT ... FOR UPDATE) em <c>serie_dps</c>. Independente do
/// <c>ReservadorNumeracao</c> da NF-e: tabela e lock proprios, para que os dois
/// modulos nunca disputem a mesma linha.
/// </summary>
public sealed class ReservadorNumeracaoDps
{
    private const long NumeroDpsMaximo = 999_999_999_999_999;
    private readonly NFeDbContext _db;

    public ReservadorNumeracaoDps(NFeDbContext db) => _db = db;

    /// <summary>
    /// Reserva o numero solicitado pelo emissor ou o proximo nDPS interno.
    /// Exige transacao ativa: a reserva so se torna definitiva se a nota chegar a ser persistida.
    /// </summary>
    public async Task<long> ReservarProximoAsync(
        Guid empresaId, string serie, TipoAmbiente ambiente, CancellationToken ct = default,
        long? numeroInformado = null)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "ReservarProximoAsync exige uma transacao ativa para garantir o lock.");
        if (numeroInformado is < 1 or > NumeroDpsMaximo)
            throw new NumeracaoDpsInvalidaException(
                $"O numero da DPS deve estar entre 1 e {NumeroDpsMaximo} (15 digitos).");

        serie = NormalizarSerie(serie);

        var contador = await _db.SeriesDps
            .FromSqlRaw(
                """
                SELECT * FROM serie_dps
                WHERE empresa_id = {0} AND serie = {1} AND ambiente = {2}
                FOR UPDATE
                """,
                empresaId, serie, (int)ambiente)
            .FirstOrDefaultAsync(ct);

        if (contador is null)
        {
            contador = new SerieDps
            {
                EmpresaId = empresaId,
                Serie = serie,
                Ambiente = ambiente,
                UltimoNumero = 0
            };
            _db.SeriesDps.Add(contador);
        }

        if (numeroInformado is long solicitado)
        {
            var jaUsado = await _db.NotasServico.AnyAsync(n =>
                n.EmpresaId == empresaId && n.Serie == serie && n.Ambiente == ambiente
                && n.NumeroDps == solicitado, ct);
            if (jaUsado)
                throw new NumeracaoDpsInvalidaException(
                    $"O numero de DPS {solicitado} ja foi usado na serie {serie} " +
                    $"no ambiente {(int)ambiente}.");

            // O aplicativo emissor e a autoridade da sequencia. O contador interno
            // acompanha o maior numero recebido sem substituir o numero solicitado.
            contador.UltimoNumero = Math.Max(contador.UltimoNumero, solicitado);
        }
        else
        {
            contador.UltimoNumero += 1;
        }

        contador.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return numeroInformado ?? contador.UltimoNumero;
    }

    /// <summary>
    /// Define qual sera o PROXIMO nDPS da serie (migracao de outro sistema).
    /// Recusa voltar para um numero ja usado nesta serie/ambiente.
    /// </summary>
    public async Task<long> DefinirProximoNumeroAsync(
        Guid empresaId, string serie, TipoAmbiente ambiente, long proximoNumero,
        CancellationToken ct = default)
    {
        if (proximoNumero < 1)
            throw new NumeracaoDpsInvalidaException("O proximo numero deve ser maior ou igual a 1.");
        serie = NormalizarSerie(serie);

        var maiorEmitido = await _db.NotasServico
            .Where(n => n.EmpresaId == empresaId && n.Serie == serie && n.Ambiente == ambiente)
            .Select(n => (long?)n.NumeroDps)
            .MaxAsync(ct) ?? 0;

        if (proximoNumero <= maiorEmitido)
            throw new NumeracaoDpsInvalidaException(
                $"O proximo numero ({proximoNumero}) precisa ser maior que o maior nDPS ja " +
                $"emitido nesta serie ({maiorEmitido}).");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var contador = await _db.SeriesDps
            .FromSqlRaw(
                """
                SELECT * FROM serie_dps
                WHERE empresa_id = {0} AND serie = {1} AND ambiente = {2}
                FOR UPDATE
                """,
                empresaId, serie, (int)ambiente)
            .FirstOrDefaultAsync(ct);

        if (contador is null)
        {
            contador = new SerieDps
            {
                EmpresaId = empresaId, Serie = serie, Ambiente = ambiente,
                UltimoNumero = proximoNumero - 1
            };
            _db.SeriesDps.Add(contador);
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

    /// <summary>Serie da DPS: somente digitos, 5 posicoes com zeros a esquerda.</summary>
    public static string NormalizarSerie(string? serie)
    {
        var d = new string((serie ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 0) d = "1";
        if (d.Length > 5) throw new NumeracaoDpsInvalidaException("A serie da DPS tem no maximo 5 digitos.");
        return d.PadLeft(5, '0');
    }
}

public sealed class NumeracaoDpsInvalidaException : Exception
{
    public NumeracaoDpsInvalidaException(string message) : base(message) { }
}
