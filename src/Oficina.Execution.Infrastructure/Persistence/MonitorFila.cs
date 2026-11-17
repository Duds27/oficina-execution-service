using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oficina.Execution.Application;
using Oficina.Execution.Application.Abstractions;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>Seção <c>Metricas</c>.</summary>
public sealed class MetricasOptions
{
    public const string Secao = "Metricas";

    /// <summary>De quanto em quanto tempo contar a fila de cada etapa.</summary>
    public TimeSpan IntervaloFila { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Teto da contagem por etapa (a consulta é a mesma do endpoint da fila).</summary>
    public int LimiteFila { get; set; } = 1000;
}

/// <summary>
/// Conta a fila de cada etapa no DynamoDB e alimenta o gauge <c>execucao.fila.tamanho</c>.
/// A leitura é periódica e não acontece na coleta da métrica: o callback do gauge só lê memória.
/// </summary>
internal sealed partial class MonitorFila : BackgroundService
{
    private readonly IExecucaoRepositorio _repositorio;
    private readonly MetricasOptions _opcoes;
    private readonly ILogger<MonitorFila> _logger;

    public MonitorFila(IExecucaoRepositorio repositorio, IOptions<MetricasOptions> opcoes, ILogger<MonitorFila> logger)
    {
        _repositorio = repositorio;
        _opcoes = opcoes.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_opcoes.IntervaloFila);
        do
        {
            try
            {
                await ContarAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // DynamoDB fora do ar, por exemplo: o gauge fica com a última leitura.
                LogFalhaAoContar(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ContarAsync(CancellationToken cancellationToken)
    {
        foreach (var etapa in MetricasExecucao.EtapasDaFila)
        {
            var fila = await _repositorio.ListarFilaAsync(etapa, _opcoes.LimiteFila, cancellationToken);
            MetricasExecucao.RegistrarFila(etapa, fila.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível contar a fila para as métricas; nova tentativa na próxima rodada")]
    private partial void LogFalhaAoContar(Exception ex);
}
