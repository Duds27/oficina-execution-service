namespace Oficina.Execution.Domain;

/// <summary>
/// Etapas da execução de uma OS na oficina:
/// <c>DiagnosticoPendente → EmDiagnostico → DiagnosticoConcluido → AguardandoExecucao → EmReparo → Finalizada</c>,
/// e <c>Cancelada</c> a partir de qualquer etapa não final.
/// Persistida pelo nome (nunca pelo número), como manda a seção 1.5 dos contratos.
/// </summary>
public enum EtapaExecucao
{
    DiagnosticoPendente,
    EmDiagnostico,
    DiagnosticoConcluido,
    AguardandoExecucao,
    EmReparo,
    Finalizada,
    Cancelada
}
