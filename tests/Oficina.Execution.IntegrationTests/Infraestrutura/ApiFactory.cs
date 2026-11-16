using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Oficina.Execution.Infrastructure.Messaging;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>
/// A API apontando para o DynamoDB Local da coleção, numa tabela só dela.
/// Em <c>Development</c>, o próprio serviço cria a tabela na inicialização.
/// Por padrão o RabbitMQ é trocado pelo transporte em memória do test harness.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string ChaveJwt = "OficinaMecanicaSecretKeyMustBe32CharactersMin!";

    private readonly string _serviceUrl;
    private readonly string _ambiente;

    public ApiFactory(string serviceUrl, string tabela, string ambiente = "Development")
    {
        _serviceUrl = serviceUrl;
        Tabela = tabela;
        _ambiente = ambiente;
    }

    public string Tabela { get; }

    /// <summary>Falso nos testes com RabbitMQ de verdade.</summary>
    protected virtual bool UsarTransporteEmMemoria => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_ambiente);
        builder.UseSetting("Jwt:Key", ChaveJwt);
        builder.UseSetting("DynamoDb:ServiceUrl", _serviceUrl);
        builder.UseSetting("DynamoDb:TableName", Tabela);
        builder.UseSetting("Outbox:IntervaloMs", "100");

        builder.ConfigureTestServices(services =>
        {
            if (!UsarTransporteEmMemoria)
                return;

            services.AddMassTransitTestHarness(x =>
            {
                MensageriaExtensions.AdicionarConsumidores(x);
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseConsumeFilter(typeof(EscopoDeLogFilter<>), context);
                    cfg.ConfigureEndpoints(context);
                });
            });
        });
    }
}
