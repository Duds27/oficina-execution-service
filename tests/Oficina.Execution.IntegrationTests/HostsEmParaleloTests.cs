using System.Net;
using Oficina.Execution.IntegrationTests.Infraestrutura;

namespace Oficina.Execution.IntegrationTests;

/// <summary>
/// Várias APIs subindo ao mesmo tempo no mesmo processo, como acontece quando as coleções de
/// teste rodam em paralelo. O logger do Serilog é estático: se uma subida congelar o logger da
/// outra, o Program termina sem construir o host ("The entry point exited without ever building
/// an IHost"). Foi o que derrubou o CI uma vez.
/// </summary>
[Collection(ColecaoDynamoDb.Nome)]
public sealed class HostsEmParaleloTests
{
    private readonly DynamoDbFixture _dynamo;

    public HostsEmParaleloTests(DynamoDbFixture dynamo) => _dynamo = dynamo;

    [Fact]
    public async Task Varias_APIs_sobem_ao_mesmo_tempo()
    {
        var fabricas = Enumerable.Range(0, 8)
            .Select(i => new ApiFactory(_dynamo.ServiceUrl, $"paralelo-{i}-{Guid.NewGuid():N}"))
            .ToList();
        try
        {
            var respostas = await Task.WhenAll(fabricas.Select(f => Task.Run(async () =>
            {
                using var cliente = f.CreateClient();
                return (await cliente.GetAsync("/health/live")).StatusCode;
            })));

            Assert.All(respostas, status => Assert.Equal(HttpStatusCode.OK, status));
        }
        finally
        {
            foreach (var f in fabricas)
                await f.DisposeAsync();
        }
    }
}
