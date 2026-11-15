using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Oficina.Execution.IntegrationTests;

public class HealthChecksTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthChecksTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
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
}
