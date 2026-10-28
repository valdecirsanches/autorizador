using NFe.Sefaz.Seguranca;

namespace NFe.Infrastructure.Security;

/// <summary>
/// Resolve o canal com o agente pareado a um certificado A3. A implementacao do
/// canal (conexao de saida do agente) entra na etapa seguinte; ate la o provedor
/// padrao responde que o agente esta desconectado.
/// </summary>
public interface IProvedorAgenteCertificado
{
    /// <exception cref="AgenteCertificadoException">Agente desconectado ou revogado.</exception>
    IAgenteCertificado ObterAgente(Guid empresaId, Guid certificadoId);
}

/// <summary>Provedor padrao enquanto nao ha canal ativo: todo A3 aparece como desconectado.</summary>
public sealed class ProvedorAgenteIndisponivel : IProvedorAgenteCertificado
{
    public IAgenteCertificado ObterAgente(Guid empresaId, Guid certificadoId) =>
        throw new AgenteCertificadoException(
            "Agente do certificado A3 desconectado. Verifique se a maquina com o token " +
            "esta ligada, com o token plugado e o agente em execucao.");
}
