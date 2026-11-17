using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Oficina.Execution.Domain;

namespace Oficina.Execution.Application;

/// <summary>
/// Métricas de negócio do execution (Meter <c>Oficina.Execucao</c>), exportadas pelo OpenTelemetry.
/// Só usa System.Diagnostics.Metrics: a camada de aplicação não depende do exportador.
/// </summary>
public static class MetricasExecucao
{
    public const string NomeMeter = "Oficina.Execucao";

    /// <summary>Etapas em que a OS espera na oficina (a fila do <c>GET /api/execucoes/fila</c>).</summary>
    public static readonly IReadOnlyList<EtapaExecucao> EtapasDaFila =
    [
        EtapaExecucao.DiagnosticoPendente,
        EtapaExecucao.EmDiagnostico,
        EtapaExecucao.DiagnosticoConcluido,
        EtapaExecucao.AguardandoExecucao,
        EtapaExecucao.EmReparo
    ];

    private static readonly Meter Meter = new(NomeMeter, "1.0.0");
    private static readonly ConcurrentDictionary<EtapaExecucao, long> TamanhoDaFila = new();

    /// <summary>Do início do reparo à finalização, a mesma duração do evento ExecucaoFinalizada.</summary>
    public static readonly Histogram<double> Duracao =
        Meter.CreateHistogram<double>("execucao.duracao", unit: "s", description: "Duração da execução até a finalização.");

    static MetricasExecucao()
    {
        // Cada réplica lê a fila inteira: no New Relic, use latest() ou max(), não sum().
        Meter.CreateObservableGauge(
            "execucao.fila.tamanho",
            () => TamanhoDaFila.Select(f => new Measurement<long>(f.Value, new KeyValuePair<string, object?>("etapa", f.Key.ToString()))),
            unit: "{os}",
            description: "OS em cada etapa da fila, lidas a cada poucos segundos.");
    }

    public static void RegistrarFila(EtapaExecucao etapa, long tamanho) => TamanhoDaFila[etapa] = tamanho;

    public static long? Fila(EtapaExecucao etapa) => TamanhoDaFila.TryGetValue(etapa, out var tamanho) ? tamanho : null;
}
