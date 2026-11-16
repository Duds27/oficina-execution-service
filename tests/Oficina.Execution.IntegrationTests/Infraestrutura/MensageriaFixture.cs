using Testcontainers.RabbitMq;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>DynamoDB Local + RabbitMQ (mesma imagem do docker-compose) para a coleção.</summary>
public sealed class MensageriaFixture : IAsyncLifetime
{
    public const string Usuario = "oficina";
    public const string Senha = "oficina_dev";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:4.1-management")
        .WithUsername(Usuario)
        .WithPassword(Senha)
        .Build();

    public DynamoDbFixture Dynamo { get; } = new();

    public string RabbitHost => _rabbit.Hostname;

    public ushort RabbitPort => _rabbit.GetMappedPublicPort(5672);

    public Task InitializeAsync() => Task.WhenAll(_rabbit.StartAsync(), Dynamo.InitializeAsync());

    public async Task DisposeAsync()
    {
        await Dynamo.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoMensageria : ICollectionFixture<MensageriaFixture>
{
    public const string Nome = "DynamoDB Local + RabbitMQ";
}

/// <summary>A API com o RabbitMQ do Testcontainers no lugar do transporte em memória.</summary>
public sealed class RabbitMqApiFactory : ApiFactory
{
    private readonly MensageriaFixture _fixture;
    private readonly bool _rejeitarExecucao;

    public RabbitMqApiFactory(MensageriaFixture fixture, bool rejeitarExecucao = false)
        : base(fixture.Dynamo.ServiceUrl, $"rabbit-{Guid.NewGuid():N}")
    {
        _fixture = fixture;
        _rejeitarExecucao = rejeitarExecucao;
    }

    protected override bool UsarTransporteEmMemoria => false;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RabbitMq:Host", _fixture.RabbitHost);
        builder.UseSetting("RabbitMq:Port", _fixture.RabbitPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RabbitMq:VirtualHost", "/");
        builder.UseSetting("RabbitMq:Username", MensageriaFixture.Usuario);
        builder.UseSetting("RabbitMq:Password", MensageriaFixture.Senha);
        builder.UseSetting("FaultInjection:RejeitarExecucao", _rejeitarExecucao ? "true" : "false");
    }
}
