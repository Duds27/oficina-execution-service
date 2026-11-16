using MassTransit;
using Microsoft.Extensions.Logging;
using Oficina.Contracts;
using Oficina.Execution.Application.UseCases;

namespace Oficina.Execution.Infrastructure.Messaging;

/// <summary>
/// Base dos consumidores dos comandos da saga: repassa ao caso de uso com o MessageId
/// (chave da deduplicação) e loga o desfecho. O escopo de log (CorrelationId, NumeroOS,
/// tipo) vem do <see cref="EscopoDeLogFilter{T}"/>.
/// </summary>
public abstract class ConsumidorDeComando<T> : IConsumer<T> where T : class, IMensagemOS
{
    private readonly ILogger _logger;

    protected ConsumidorDeComando(ILogger logger) => _logger = logger;

    public async Task Consume(ConsumeContext<T> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.MessageId
            ?? throw new InvalidOperationException($"{typeof(T).Name} sem MessageId: não dá para deduplicar.");

        var resultado = await ProcessarAsync(context.Message, messageId, context.CancellationToken);
        _logger.ComandoProcessado(typeof(T).Name, context.Message.NumeroOS, resultado);
    }

    protected abstract Task<ResultadoProcessamento> ProcessarAsync(T comando, Guid messageId, CancellationToken cancellationToken);
}

public sealed class SolicitarDiagnosticoConsumer : ConsumidorDeComando<SolicitarDiagnostico>
{
    private readonly ComandosSagaUseCases _useCases;

    public SolicitarDiagnosticoConsumer(ComandosSagaUseCases useCases, ILogger<SolicitarDiagnosticoConsumer> logger)
        : base(logger) => _useCases = useCases;

    protected override Task<ResultadoProcessamento> ProcessarAsync(SolicitarDiagnostico comando, Guid messageId, CancellationToken cancellationToken) =>
        _useCases.SolicitarDiagnosticoAsync(comando, messageId, cancellationToken);
}

public sealed class IniciarExecucaoConsumer : ConsumidorDeComando<IniciarExecucao>
{
    private readonly ComandosSagaUseCases _useCases;

    public IniciarExecucaoConsumer(ComandosSagaUseCases useCases, ILogger<IniciarExecucaoConsumer> logger)
        : base(logger) => _useCases = useCases;

    protected override Task<ResultadoProcessamento> ProcessarAsync(IniciarExecucao comando, Guid messageId, CancellationToken cancellationToken) =>
        _useCases.IniciarExecucaoAsync(comando, messageId, cancellationToken);
}

public sealed class CancelarExecucaoConsumer : ConsumidorDeComando<CancelarExecucao>
{
    private readonly ComandosSagaUseCases _useCases;

    public CancelarExecucaoConsumer(ComandosSagaUseCases useCases, ILogger<CancelarExecucaoConsumer> logger)
        : base(logger) => _useCases = useCases;

    protected override Task<ResultadoProcessamento> ProcessarAsync(CancelarExecucao comando, Guid messageId, CancellationToken cancellationToken) =>
        _useCases.CancelarExecucaoAsync(comando, messageId, cancellationToken);
}
