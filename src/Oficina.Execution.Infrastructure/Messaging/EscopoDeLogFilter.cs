using MassTransit;
using Microsoft.Extensions.Logging;
using Oficina.Contracts;

namespace Oficina.Execution.Infrastructure.Messaging;

/// <summary>
/// Todo log escrito durante o consumo de uma mensagem do catálogo leva o CorrelationId,
/// o NumeroOS, o OsId, o tipo e o MessageId (o Serilog transforma o escopo em propriedades).
/// </summary>
public sealed class EscopoDeLogFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    private readonly ILogger<EscopoDeLogFilter<T>> _logger;

    public EscopoDeLogFilter(ILogger<EscopoDeLogFilter<T>> logger) => _logger = logger;

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Message is not IMensagemOS mensagem)
        {
            await next.Send(context);
            return;
        }

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = mensagem.CorrelationId,
            ["NumeroOS"] = mensagem.NumeroOS,
            ["OsId"] = mensagem.OsId,
            ["TipoMensagem"] = typeof(T).Name,
            ["MessageId"] = context.MessageId?.ToString() ?? string.Empty
        }))
        {
            await next.Send(context);
        }
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("escopo-de-log");
}
