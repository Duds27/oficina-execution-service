using MassTransit;
using Oficina.Contracts;
using Serilog.Core;
using Serilog.Core.Enrichers;
using SerilogContext = Serilog.Context.LogContext;

namespace Oficina.Execution.Infrastructure.Messaging;

/// <summary>
/// Põe no LogContext do Serilog, durante o consumo, o tipo da mensagem e, nas mensagens da
/// saga, o CorrelationId e o NumeroOS. Todo log do consumer (e do MassTransit) sai com eles,
/// e filtrar os logs por NumeroOS no New Relic mostra a OS nos 3 serviços.
/// </summary>
public sealed class ContextoDeLogConsumeFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        using (SerilogContext.Push(Propriedades(context.Message)))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("contextoDeLog");

    public static ILogEventEnricher[] Propriedades(T mensagem) => mensagem is IMensagemOS os
        ?
        [
            new PropertyEnricher("MessageType", typeof(T).Name),
            new PropertyEnricher("CorrelationId", os.CorrelationId),
            new PropertyEnricher("NumeroOS", os.NumeroOS)
        ]
        : [new PropertyEnricher("MessageType", typeof(T).Name)];
}
