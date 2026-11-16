using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oficina.Contracts;
using Oficina.Execution.Application;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Application.UseCases;
using Oficina.Execution.Domain;
using Oficina.Execution.Infrastructure.Messaging;
using Oficina.Execution.UnitTests.TestSupport;

namespace Oficina.Execution.UnitTests.Messaging;

/// <summary>
/// Consumidores no test harness do MassTransit (transporte em memória), com o repositório em
/// memória: confere as filas do contrato, a deduplicação pelo MessageId e o escopo de log.
/// </summary>
public sealed class ConsumidoresTests : IAsyncLifetime
{
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");

    private readonly RepositorioEmMemoria _repositorio = new();
    private readonly LogsCapturados _logs = new();
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.AddProvider(_logs));
        services.AddSingleton<IExecucaoRepositorio>(_repositorio);
        services.AddSingleton<TimeProvider>(new Relogio(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)));
        services.Configure<FaultInjectionOptions>(_ => { });
        services.AddSingleton<ComandosSagaUseCases>();
        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(10), testInactivityTimeout: TimeSpan.FromSeconds(2));
            MensageriaExtensions.AdicionarConsumidores(x);
            x.UsingInMemory((context, cfg) =>
            {
                cfg.UseConsumeFilter(typeof(EscopoDeLogFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        _provider = services.BuildServiceProvider(validateScopes: true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private static SolicitarDiagnostico Solicitar(int osId = 42) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-{osId:D4}",
        OcorridoEm = DateTime.UtcNow,
        Veiculo = new VeiculoInfo { Placa = "ABC1D23", Marca = "VW", Modelo = "Gol", Ano = 2020 }
    };

    private async Task<Guid> EnviarAsync<T>(string fila, T mensagem, Guid? messageId = null) where T : class
    {
        var id = messageId ?? NewId.NextGuid();
        var endpoint = await _harness.Bus.GetSendEndpoint(new Uri($"queue:{fila}"));
        await endpoint.Send(mensagem, contexto => contexto.MessageId = id);
        return id;
    }

    /// <summary>
    /// Polling em vez de <c>Consumed.Any</c>: depois do primeiro período de inatividade do harness,
    /// o <c>Any</c> deixa de esperar mensagens novas.
    /// </summary>
    private async Task EsperarConsumoAsync<T>(Guid messageId) where T : class =>
        await EsperarAsync(() => _harness.Consumed.Select<T>(m => m.Context.MessageId == messageId).Any(),
            $"{typeof(T).Name} não foi consumida.");

    /// <summary>O log do desfecho é a última coisa que o consumidor faz.</summary>
    private Task EsperarLogAsync(Func<string, bool> condicao, int vezes = 1) =>
        EsperarAsync(() => _logs.Registros.Count(r => condicao(r.Mensagem)) >= vezes, "Log esperado não apareceu.");

    private static async Task EsperarAsync(Func<bool> condicao, string erro)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicao())
        {
            Assert.True(DateTime.UtcNow < limite, erro);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Os_tres_comandos_chegam_pelas_filas_do_contrato()
    {
        await EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar());
        await EsperarLogAsync(m => m == "SolicitarDiagnostico da OS OS-0042: Processada");
        Assert.Equal(EtapaExecucao.DiagnosticoPendente, (await _repositorio.ObterAsync(42))!.Etapa);

        await EnviarAsync(FilasExecucao.IniciarExecucao, new IniciarExecucao { CorrelationId = Correlacao, OsId = 42, NumeroOS = "OS-0042" });
        await EsperarLogAsync(m => m == "IniciarExecucao da OS OS-0042: Processada");
        Assert.IsType<ExecucaoRejeitada>(Assert.Single(_repositorio.Outbox));

        await EnviarAsync(FilasExecucao.CancelarExecucao, new CancelarExecucao { CorrelationId = Correlacao, OsId = 42, NumeroOS = "OS-0042", Motivo = "recusado" });
        await EsperarLogAsync(m => m == "CancelarExecucao da OS OS-0042: Processada");
        Assert.Equal(EtapaExecucao.Cancelada, (await _repositorio.ObterAsync(42))!.Etapa);
    }

    [Fact]
    public async Task Reentrega_com_o_mesmo_MessageId_nao_repete_o_efeito()
    {
        var messageId = NewId.NextGuid();

        await EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar(), messageId);
        await EsperarLogAsync(m => m == "SolicitarDiagnostico da OS OS-0042: Processada");
        await EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar(), messageId);
        await EsperarLogAsync(m => m == "SolicitarDiagnostico da OS OS-0042: Duplicada");

        Assert.Equal(1, _repositorio.Gravacoes);
        Assert.Equal(1, (await _repositorio.ObterAsync(42))!.Version);
    }

    [Fact]
    public async Task Logs_do_consumo_levam_CorrelationId_NumeroOS_e_tipo_da_mensagem()
    {
        var messageId = await EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar(7));
        await EsperarLogAsync(m => m == "SolicitarDiagnostico da OS OS-0007: Processada");

        var registro = Assert.Single(_logs.Registros, r => r.Mensagem == "SolicitarDiagnostico da OS OS-0007: Processada");
        Assert.Equal(Correlacao, registro.Escopo["CorrelationId"]);
        Assert.Equal("OS-0007", registro.Escopo["NumeroOS"]);
        Assert.Equal(7, registro.Escopo["OsId"]);
        Assert.Equal("SolicitarDiagnostico", registro.Escopo["TipoMensagem"]);
        Assert.Equal(messageId.ToString(), registro.Escopo["MessageId"]);
    }

    [Fact]
    public async Task Comando_com_dado_invalido_falha_sem_retry()
    {
        var messageId = await EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar() with { NumeroOS = " " });

        await EsperarAsync(() => _harness.Published.Select<Fault<SolicitarDiagnostico>>(f => f.Context.Message.FaultedMessageId == messageId).Any(),
            "Fault não publicado.");
        await EsperarConsumoAsync<SolicitarDiagnostico>(messageId);
        var consumo = Assert.Single(_harness.Consumed.Select<SolicitarDiagnostico>(m => m.Context.MessageId == messageId));
        Assert.IsAssignableFrom<ArgumentException>(consumo.Exception);
        // Sem retry: a mensagem foi lida do repositório uma vez só.
        Assert.Equal(0, _repositorio.Gravacoes);
        Assert.Equal(1, _repositorio.Leituras);
    }
}
