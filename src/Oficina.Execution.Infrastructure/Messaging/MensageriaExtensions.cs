using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oficina.Contracts;
using Oficina.Execution.Domain;
using Oficina.Execution.Infrastructure.Persistence;

namespace Oficina.Execution.Infrastructure.Messaging;

public static class MensageriaExtensions
{
    /// <summary>
    /// MassTransit 8.4.1 + RabbitMQ, no mesmo padrão do os-service (ADR-007). A deduplicação
    /// e o outbox são do próprio serviço, no DynamoDB: o outbox do MassTransit só existe para EF Core.
    /// </summary>
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxPublisherOptions>(configuration.GetSection(OutboxPublisherOptions.Secao));
        services.AddSingleton<IFonteOutbox>(sp => sp.GetRequiredService<OutboxDynamoDb>());

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();
            AdicionarConsumidores(x);

            x.ConfigureHealthCheckOptions(o =>
            {
                o.Name = "masstransit";
                o.MinimalFailureStatus = HealthStatus.Unhealthy;
                o.Tags.Add("ready");
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = configuration.GetSection(RabbitMqOptions.Secao).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
                cfg.Host(rabbit.Host, rabbit.Port, rabbit.VirtualHost, h =>
                {
                    h.Username(rabbit.Username);
                    h.Password(rabbit.Password);
                });

                cfg.UseConsumeFilter(typeof(EscopoDeLogFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        // Depois do MassTransit: publica pelo bus já registrado.
        services.AddHostedService<OutboxPublisher>();
        return services;
    }

    /// <summary>
    /// Os consumidores dos comandos, cada um na fila de nome fixo do contrato, com retry.
    /// Público para os testes registrarem os mesmos consumidores no test harness.
    /// </summary>
    public static void AdicionarConsumidores(IBusRegistrationConfigurator x)
    {
        ArgumentNullException.ThrowIfNull(x);

        x.AddConsumer<SolicitarDiagnosticoConsumer>(ComRetry).Endpoint(e => e.Name = FilasExecucao.SolicitarDiagnostico);
        x.AddConsumer<IniciarExecucaoConsumer>(ComRetry).Endpoint(e => e.Name = FilasExecucao.IniciarExecucao);
        x.AddConsumer<CancelarExecucaoConsumer>(ComRetry).Endpoint(e => e.Name = FilasExecucao.CancelarExecucao);
    }

    /// <summary>
    /// Falha transitória (broker, DynamoDB, conflito de versão): novas tentativas com intervalo
    /// crescente, como no os-service. Esgotou, a mensagem vai para <c>&lt;fila&gt;_error</c>.
    /// O redelivery atrasado precisa do plugin de delayed exchange e fica para a Etapa 4.
    /// </summary>
    private static void ComRetry<T>(IConsumerConfigurator<T> consumidor) where T : class =>
        consumidor.UseMessageRetry(r =>
        {
            r.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500));
            // Erro de regra ou de dado inválido não se resolve tentando de novo.
            r.Ignore<EtapaInvalidaException>();
            r.Ignore<ArgumentException>();
        });
}
