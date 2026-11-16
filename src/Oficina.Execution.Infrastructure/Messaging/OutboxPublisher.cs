using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using Oficina.Execution.Infrastructure.Persistence;

namespace Oficina.Execution.Infrastructure.Messaging;

/// <summary>Seção <c>Outbox</c>.</summary>
public sealed class OutboxPublisherOptions
{
    public const string Secao = "Outbox";

    /// <summary>Intervalo entre as leituras do outbox.</summary>
    public int IntervaloMs { get; set; } = 2000;

    /// <summary>Quantas mensagens por leitura.</summary>
    public int Lote { get; set; } = 25;
}

/// <summary>De onde o publicador lê as mensagens pendentes (o DynamoDB; nos testes, memória).</summary>
internal interface IFonteOutbox
{
    Task<IReadOnlyList<RegistroOutbox>> ListarPendentesAsync(int limite, CancellationToken cancellationToken);

    Task RemoverAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Os eventos que este serviço publica, pelo nome gravado no outbox.</summary>
internal static class CatalogoMensagens
{
    internal static readonly IReadOnlyDictionary<string, Type> Publicaveis = new[]
    {
        typeof(DiagnosticoIniciado),
        typeof(DiagnosticoConcluido),
        typeof(ExecucaoIniciada),
        typeof(ExecucaoRejeitada),
        typeof(ExecucaoFinalizada)
    }.ToDictionary(t => t.FullName!, t => t);
}

/// <summary>
/// Lê o outbox do DynamoDB a cada poucos segundos, publica no RabbitMQ e apaga o item.
/// Entrega pelo menos uma vez: se cair entre publicar e apagar, publica de novo com o
/// mesmo MessageId (o id do item), e o inbox do os-service descarta a repetida.
/// </summary>
internal sealed partial class OutboxPublisher : BackgroundService
{
    private readonly IFonteOutbox _fonte;
    private readonly IBus _bus;
    private readonly OutboxPublisherOptions _opcoes;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(IFonteOutbox fonte, IBus bus, IOptions<OutboxPublisherOptions> opcoes, ILogger<OutboxPublisher> logger)
    {
        _fonte = fonte;
        _bus = bus;
        _opcoes = opcoes.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opcoes.IntervaloMs));
        do
        {
            try
            {
                await PublicarPendentesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // O item fica no outbox e sai na próxima leitura.
                LogFalhaAoPublicar(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Publica tudo o que está pendente, lote a lote. Devolve quantas saíram.</summary>
    internal async Task<int> PublicarPendentesAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        IReadOnlyList<RegistroOutbox> lote;
        do
        {
            lote = await _fonte.ListarPendentesAsync(_opcoes.Lote, cancellationToken);
            var publicadasNoLote = 0;

            foreach (var registro in lote)
            {
                if (!CatalogoMensagens.Publicaveis.TryGetValue(registro.TipoMensagem, out var tipo))
                {
                    // Não apaga: um deploy com o tipo de volta ainda pode publicar.
                    LogTipoDesconhecido(registro.Id, registro.TipoMensagem);
                    continue;
                }

                var mensagem = JsonSerializer.Deserialize(registro.Corpo, tipo, ItensDynamoDb.JsonOutbox)
                    ?? throw new InvalidOperationException($"Item {registro.Id} do outbox sem corpo.");

                await _bus.Publish(mensagem, tipo, Pipe.Execute<PublishContext>(contexto =>
                {
                    contexto.MessageId = registro.Id;
                    if (mensagem is IMensagemOS envelope)
                        contexto.CorrelationId = envelope.CorrelationId;
                }), cancellationToken);

                await _fonte.RemoverAsync(registro.Id, cancellationToken);
                LogPublicada(tipo.Name, registro.Id);
                publicadasNoLote++;
            }

            total += publicadasNoLote;

            // Lote cheio e tudo publicado: pode haver mais esperando.
            if (publicadasNoLote < lote.Count)
                break;
        }
        while (lote.Count == _opcoes.Lote);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox: {Tipo} publicada (MessageId {MessageId})")]
    private partial void LogPublicada(string tipo, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox: item {Id} com tipo desconhecido {Tipo}; ficou no outbox")]
    private partial void LogTipoDesconhecido(Guid id, string tipo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox: falha ao publicar; nova tentativa na próxima leitura")]
    private partial void LogFalhaAoPublicar(Exception ex);
}
