namespace Oficina.Execution.Application;

/// <summary>
/// Falhas simuladas para a demonstração das compensações (seção <c>FaultInjection</c>).
/// Com <see cref="RejeitarExecucao"/> ligado, todo <c>IniciarExecucao</c> é rejeitado com
/// o motivo <see cref="MotivoRejeicaoSimulada"/>, e a saga dispara o estorno (C3).
/// </summary>
public sealed class FaultInjectionOptions
{
    public const string Secao = "FaultInjection";
    public const string MotivoRejeicaoSimulada = "falha simulada";

    public bool RejeitarExecucao { get; set; }
}
