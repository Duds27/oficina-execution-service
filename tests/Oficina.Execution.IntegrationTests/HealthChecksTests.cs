using System.Net;
using Oficina.Execution.IntegrationTests.Infraestrutura;

namespace Oficina.Execution.IntegrationTests;

[Collection(ColecaoDynamoDb.Nome)]
public sealed class HealthChecksTests : IAsyncLifetime
{
    private readonly DynamoDbFixture _dynamo;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public HealthChecksTests(DynamoDbFixture dynamo) => _dynamo = dynamo;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_dynamo.ServiceUrl, $"health-{Guid.NewGuid():N}");
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_check_responde_200(string rota)
    {
        var resposta = await _client.GetAsync(rota);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Resposta_devolve_o_correlation_id_recebido()
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        requisicao.Headers.Add("X-Correlation-ID", "teste-123");

        var resposta = await _client.SendAsync(requisicao);

        Assert.Equal("teste-123", resposta.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Ready_responde_503_quando_a_tabela_nao_existe()
    {
        // Fora de Development o serviço não cria a tabela (na AWS, é o Terraform).
        await using var semTabela = new ApiFactory(_dynamo.ServiceUrl, $"inexistente-{Guid.NewGuid():N}", "Producao");
        using var client = semTabela.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
