using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using Oficina.Execution.Application;
using Oficina.Execution.Application.UseCases;
using Oficina.Execution.Domain;
using Oficina.Execution.Infrastructure.Messaging;
using Oficina.Execution.Infrastructure.Persistence;
using Oficina.Execution.UnitTests.TestSupport;

namespace Oficina.Execution.UnitTests.UseCases;

/// <summary>O Meter é estático: esta coleção roda sozinha, para outra classe não somar nas medições.</summary>
[CollectionDefinition(nameof(MetricasExecucaoColecao), DisableParallelization = true)]
public class MetricasExecucaoColecao;

/// <summary>Métricas de negócio e trace do outbox do execution (Etapa 5).</summary>
[Collection(nameof(MetricasExecucaoColecao))]
public sealed class MetricasExecucaoTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly ColetorDeMetricas _metricas = new(MetricasExecucao.NomeMeter);
    private readonly RepositorioEmMemoria _memoria = new();
    private readonly Relogio _relogio = new(T0);

    public void Dispose() => _metricas.Dispose();

    private Execucao Semear(int osId, Action<Execucao>? ate = null)
    {
        var execucao = Execucao.Abrir(osId, Guid.NewGuid(), $"OS-{osId:D4}", new Veiculo("ABC1D23", "VW", "Gol", 2020), null, T0);
        ate?.Invoke(execucao);
        _memoria.Semear(execucao);
        return execucao;
    }

    [Fact]
    public async Task Finalizar_registra_a_duracao_do_reparo()
    {
        Semear(42, e =>
        {
            e.IniciarDiagnostico("joao", T0);
            e.ConcluirDiagnostico(null, "joao", T0);
            e.IniciarExecucao([], T0);
        });
        var casos = new ExecucaoUseCases(_memoria, _relogio);
        await casos.IniciarReparoAsync(42, "joao");
        _relogio.Avancar(TimeSpan.FromMinutes(90));

        await casos.FinalizarAsync(42, "joao");

        Assert.Equal(5400, Assert.Single(_metricas.Medicoes("execucao.duracao")).Valor);
    }

    [Fact]
    public async Task Finalizar_sem_sucesso_nao_registra()
    {
        Semear(43);

        var resultado = await new ExecucaoUseCases(_memoria, _relogio).FinalizarAsync(43, "joao");

        Assert.NotEqual(StatusResultado.Ok, resultado.Status);
        Assert.Empty(_metricas.Medicoes("execucao.duracao"));
    }

    [Fact]
    public async Task Monitor_conta_a_fila_de_cada_etapa()
    {
        Semear(1);
        Semear(2);
        Semear(3, e => e.IniciarDiagnostico("joao", T0));
        var monitor = new MonitorFila(_memoria, Options.Create(new MetricasOptions()), NullLogger<MonitorFila>.Instance);

        await monitor.ContarAsync(CancellationToken.None);

        Assert.Equal(2, MetricasExecucao.Fila(EtapaExecucao.DiagnosticoPendente));
        Assert.Equal(1, MetricasExecucao.Fila(EtapaExecucao.EmDiagnostico));
        Assert.Equal(0, MetricasExecucao.Fila(EtapaExecucao.EmReparo));
        _metricas.Observar("execucao.fila.tamanho");
        var porEtapa = _metricas.Medicoes("execucao.fila.tamanho").ToDictionary(m => m.Tags["etapa"]!, m => m.Valor);
        Assert.Equal(2, porEtapa["DiagnosticoPendente"]);
    }

    [Fact]
    public void Outbox_grava_o_traceparent_e_a_publicacao_continua_o_trace()
    {
        using var ouvinte = OuvirFonte(OutboxPublisher.NomeFonteTrace);
        using var consumo = new Activity("consumo").SetIdFormat(ActivityIdFormat.W3C).Start();
        var evento = new DiagnosticoIniciado { CorrelationId = Guid.NewGuid(), OsId = 1, NumeroOS = "OS-0001", OcorridoEm = T0, Mecanico = "joao" };

        var item = ItensDynamoDb.ItemOutbox(Guid.NewGuid(), evento, T0);
        var registro = ItensDynamoDb.ParaRegistroOutbox(item);
        consumo.Stop();

        Assert.Equal(consumo.Id, registro.Traceparent);
        using var publicacao = OutboxPublisher.IniciarSpan(registro, typeof(DiagnosticoIniciado));
        Assert.NotNull(publicacao);
        Assert.Equal(consumo.TraceId, publicacao!.TraceId);
        Assert.Equal(consumo.SpanId, publicacao.ParentSpanId);
        Assert.Equal("outbox publish DiagnosticoIniciado", publicacao.DisplayName);
    }

    [Fact]
    public void Outbox_sem_trace_publica_num_trace_novo()
    {
        using var ouvinte = OuvirFonte(OutboxPublisher.NomeFonteTrace);
        Activity.Current = null;
        var evento = new DiagnosticoIniciado { CorrelationId = Guid.NewGuid(), OsId = 1, NumeroOS = "OS-0001", OcorridoEm = T0, Mecanico = "joao" };

        var registro = ItensDynamoDb.ParaRegistroOutbox(ItensDynamoDb.ItemOutbox(Guid.NewGuid(), evento, T0));

        Assert.Null(registro.Traceparent);
        using var publicacao = OutboxPublisher.IniciarSpan(registro, typeof(DiagnosticoIniciado));
        Assert.NotNull(publicacao);
        Assert.Equal(default, publicacao!.ParentSpanId);
    }

    private static ActivityListener OuvirFonte(string nome)
    {
        var ouvinte = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == nome,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(ouvinte);
        return ouvinte;
    }
}
