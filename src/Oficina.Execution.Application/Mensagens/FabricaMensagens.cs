using Oficina.Contracts;
using Oficina.Execution.Domain;

namespace Oficina.Execution.Application.Mensagens;

/// <summary>Eventos que o execution-service publica, com o envelope preenchido a partir da execução.</summary>
public static class FabricaMensagens
{
    public static DiagnosticoIniciado DiagnosticoIniciado(Execucao execucao, DateTime agora) =>
        Envelope(new DiagnosticoIniciado { Mecanico = execucao.Estado.Mecanico ?? string.Empty }, execucao, agora);

    public static DiagnosticoConcluido DiagnosticoConcluido(Execucao execucao, DateTime agora) =>
        Envelope(new DiagnosticoConcluido { Observacoes = execucao.Estado.ObservacoesDiagnostico }, execucao, agora);

    public static ExecucaoIniciada ExecucaoIniciada(Execucao execucao, DateTime agora) =>
        Envelope(new ExecucaoIniciada(), execucao, agora);

    public static ExecucaoRejeitada ExecucaoRejeitada(Execucao execucao, string motivo, DateTime agora) =>
        Envelope(new ExecucaoRejeitada { Motivo = motivo }, execucao, agora);

    /// <summary>Rejeição sem execução gravada: o envelope vem do próprio comando.</summary>
    public static ExecucaoRejeitada ExecucaoRejeitada(IMensagemOS comando, string motivo, DateTime agora) => new()
    {
        CorrelationId = comando.CorrelationId,
        OsId = comando.OsId,
        NumeroOS = comando.NumeroOS,
        OcorridoEm = agora,
        Motivo = motivo
    };

    public static ExecucaoFinalizada ExecucaoFinalizada(Execucao execucao, int duracaoSegundos, DateTime agora) =>
        Envelope(new ExecucaoFinalizada { DuracaoSegundos = duracaoSegundos }, execucao, agora);

    private static T Envelope<T>(T mensagem, Execucao execucao, DateTime agora) where T : MensagemOS =>
        mensagem with
        {
            CorrelationId = execucao.CorrelationId,
            OsId = execucao.OsId,
            NumeroOS = execucao.NumeroOS,
            OcorridoEm = agora
        };
}
