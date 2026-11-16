using System.Collections.Concurrent;
using MassTransit;
using Oficina.Contracts;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>
/// Um bus à parte no mesmo RabbitMQ, no papel do os-service: manda os comandos para as filas
/// do contrato e recebe os eventos numa fila própria, como a da saga.
/// </summary>
public sealed class OsServiceSimulado : IAsyncDisposable
{
    private readonly IBusControl _bus;

    private OsServiceSimulado(IBusControl bus) => _bus = bus;

    public ConcurrentQueue<(Guid? MessageId, string[] Tipos, IMensagemOS Mensagem)> Recebidos { get; } = new();

    public static async Task<OsServiceSimulado> IniciarAsync(MensageriaFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        OsServiceSimulado? simulado = null;

        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(fixture.RabbitHost, fixture.RabbitPort, "/", h =>
            {
                h.Username(MensageriaFixture.Usuario);
                h.Password(MensageriaFixture.Senha);
            });

            cfg.ReceiveEndpoint($"teste-os-saga-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Handler<DiagnosticoIniciado>(c => Guardar(simulado!, c));
                e.Handler<DiagnosticoConcluido>(c => Guardar(simulado!, c));
                e.Handler<ExecucaoIniciada>(c => Guardar(simulado!, c));
                e.Handler<ExecucaoRejeitada>(c => Guardar(simulado!, c));
                e.Handler<ExecucaoFinalizada>(c => Guardar(simulado!, c));
            });
        });

        simulado = new OsServiceSimulado(bus);
        await bus.StartAsync();
        return simulado;
    }

    public async Task EnviarAsync<T>(string fila, T comando, Guid? messageId = null) where T : class
    {
        var endpoint = await _bus.GetSendEndpoint(new Uri($"queue:{fila}"));
        await endpoint.Send(comando, c => c.MessageId = messageId ?? NewId.NextGuid());
    }

    public async Task<T> EsperarAsync<T>(int osId) where T : class, IMensagemOS
    {
        await Esperar.AteAsync(() => Recebidos.Any(r => r.Mensagem is T && r.Mensagem.OsId == osId),
            $"{typeof(T).Name} da OS {osId} não chegou ao os-service simulado.", segundos: 30);
        return (T)Recebidos.First(r => r.Mensagem is T && r.Mensagem.OsId == osId).Mensagem;
    }

    public int Quantos<T>(int osId) where T : class, IMensagemOS =>
        Recebidos.Count(r => r.Mensagem is T && r.Mensagem.OsId == osId);

    public async ValueTask DisposeAsync() => await _bus.StopAsync();

    private static Task Guardar<T>(OsServiceSimulado simulado, ConsumeContext<T> contexto) where T : class, IMensagemOS
    {
        simulado.Recebidos.Enqueue((contexto.MessageId, contexto.SupportedMessageTypes.ToArray(), contexto.Message));
        return Task.CompletedTask;
    }
}
