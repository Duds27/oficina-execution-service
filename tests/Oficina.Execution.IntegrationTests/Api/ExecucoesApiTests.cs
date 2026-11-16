using System.Net;
using System.Net.Http.Json;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Application.DTOs;
using Oficina.Execution.Domain;
using Oficina.Execution.IntegrationTests.Infraestrutura;

namespace Oficina.Execution.IntegrationTests.Api;

/// <summary>API com DynamoDB Local e o transporte em memória do test harness no lugar do RabbitMQ.</summary>
[Collection(ColecaoDynamoDb.Nome)]
public sealed class ExecucoesApiTests : IAsyncLifetime
{
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");

    private readonly DynamoDbFixture _dynamo;
    private ApiFactory _factory = null!;
    private HttpClient _api = null!;
    private ITestHarness _harness = null!;

    public ExecucoesApiTests(DynamoDbFixture dynamo) => _dynamo = dynamo;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_dynamo.ServiceUrl, $"api-{Guid.NewGuid():N}");
        _api = _factory.CreateClient().Autenticado(Tokens.DaOficina());
        _harness = _factory.Services.GetTestHarness();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _api.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task EnviarComandoAsync<T>(string fila, T comando) where T : class
    {
        var endpoint = await _harness.Bus.GetSendEndpoint(new Uri($"queue:{fila}"));
        await endpoint.Send(comando);
    }

    private Task<T> PublicadoAsync<T>(int osId) where T : class, IMensagemOS =>
        Esperar.AteAsync(
            () => Task.FromResult(_harness.Published.Select<T>(m => m.Context.Message.OsId == osId).FirstOrDefault()?.Context.Message),
            _ => true,
            $"{typeof(T).Name} da OS {osId} não foi publicado.");

    /// <summary>Null enquanto a execução não existe (404).</summary>
    private async Task<ExecucaoDto?> ObterAsync(int osId)
    {
        var resposta = await _api.GetAsync($"/api/execucoes/{osId}");
        return resposta.StatusCode == HttpStatusCode.NotFound ? null : await resposta.Content.ReadFromJsonAsync<ExecucaoDto>();
    }

    private async Task<Execucao> SemearAsync(int osId, DateTime? em = null)
    {
        var execucao = Execucao.Abrir(osId, Correlacao, $"OS-{osId:D4}", new Veiculo("ABC1D23", "VW", "Gol", 2020), "Freio fazendo barulho", em ?? DateTime.UtcNow);
        await _factory.Services.GetRequiredService<IExecucaoRepositorio>().SalvarAsync(execucao, [], null);
        return execucao;
    }

    [Fact]
    public async Task Fluxo_completo_do_diagnostico_ao_reparo_publica_os_eventos_da_saga()
    {
        const int osId = 42;

        // A saga pede o diagnóstico.
        await EnviarComandoAsync(FilasExecucao.SolicitarDiagnostico, new SolicitarDiagnostico
        {
            CorrelationId = Correlacao,
            OsId = osId,
            NumeroOS = "OS-0042",
            OcorridoEm = DateTime.UtcNow,
            Veiculo = new VeiculoInfo { Placa = "ABC1D23", Marca = "VW", Modelo = "Gol", Ano = 2020 },
            Descricao = "Freio fazendo barulho"
        });
        await Esperar.AteAsync(() => ObterAsync(osId), e => e.Etapa == "DiagnosticoPendente", "A execução não foi aberta.");
        var naFila = await _api.GetFromJsonAsync<List<ItemFilaDto>>("/api/execucoes/fila?etapa=DiagnosticoPendente");
        Assert.Equal(osId, Assert.Single(naFila!).OsId);

        // O mecânico diagnostica.
        var iniciado = await _api.PostAsync($"/api/execucoes/{osId}/diagnostico/iniciar", null);
        Assert.Equal(HttpStatusCode.OK, iniciado.StatusCode);
        Assert.Equal("EmDiagnostico", (await iniciado.Content.ReadFromJsonAsync<ExecucaoDto>())!.Etapa);
        var diagnosticoIniciado = await PublicadoAsync<DiagnosticoIniciado>(osId);
        Assert.Equal("joao", diagnosticoIniciado.Mecanico);
        Assert.Equal(Correlacao, diagnosticoIniciado.CorrelationId);

        var concluido = await _api.PostAsJsonAsync($"/api/execucoes/{osId}/diagnostico/concluir", new { observacoes = "Trocar pastilhas" });
        Assert.Equal(HttpStatusCode.OK, concluido.StatusCode);
        Assert.Equal("Trocar pastilhas", (await PublicadoAsync<DiagnosticoConcluido>(osId)).Observacoes);

        // Pagamento confirmado: a saga libera o reparo.
        await EnviarComandoAsync(FilasExecucao.IniciarExecucao, new IniciarExecucao
        {
            CorrelationId = Correlacao,
            OsId = osId,
            NumeroOS = "OS-0042",
            OcorridoEm = DateTime.UtcNow,
            Servicos = [new ServicoExecucaoInfo { Descricao = "Troca de pastilhas", Quantidade = 1 }]
        });
        await PublicadoAsync<ExecucaoIniciada>(osId);

        // O mecânico faz o reparo.
        Assert.Equal(HttpStatusCode.OK, (await _api.PostAsync($"/api/execucoes/{osId}/reparo/iniciar", null)).StatusCode);
        var apontamento = await _api.PostAsJsonAsync($"/api/execucoes/{osId}/apontamentos", new { texto = "Pastilhas trocadas" });
        Assert.Equal(HttpStatusCode.Created, apontamento.StatusCode);
        Assert.Equal("Pastilhas trocadas", (await apontamento.Content.ReadFromJsonAsync<ApontamentoDto>())!.Texto);
        var finalizado = await _api.PostAsync($"/api/execucoes/{osId}/finalizar", null);
        Assert.Equal(HttpStatusCode.OK, finalizado.StatusCode);
        Assert.True((await PublicadoAsync<ExecucaoFinalizada>(osId)).DuracaoSegundos >= 0);

        var final = (await _api.GetFromJsonAsync<ExecucaoDto>($"/api/execucoes/{osId}"))!;
        Assert.Equal("Finalizada", final.Etapa);
        Assert.Equal([new ServicoDto("Troca de pastilhas", 1)], final.Servicos);
        Assert.Equal(
            ["Abertura", "DiagnosticoIniciado", "DiagnosticoConcluido", "ExecucaoIniciada", "ReparoIniciado", "Observacao", "Finalizada"],
            final.Apontamentos.Select(a => a.Tipo));
        Assert.Equal("joao", final.Apontamentos[^1].Autor);
    }

    [Fact]
    public async Task Fila_lista_a_etapa_pedida_por_ordem_de_chegada()
    {
        var t0 = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        await SemearAsync(103, t0.AddMinutes(3));
        await SemearAsync(101, t0.AddMinutes(1));
        await SemearAsync(102, t0.AddMinutes(2));

        var fila = await _api.GetFromJsonAsync<List<ItemFilaDto>>("/api/execucoes/fila?etapa=DiagnosticoPendente&limite=10");
        var vazia = await _api.GetFromJsonAsync<List<ItemFilaDto>>("/api/execucoes/fila?etapa=EmReparo");
        var padrao = await _api.GetFromJsonAsync<List<ItemFilaDto>>("/api/execucoes/fila?limite=2");

        Assert.Equal([101, 102, 103], fila!.Select(i => i.OsId));
        Assert.Equal("ABC1D23", fila![0].Veiculo!.Placa);
        Assert.Empty(vazia!);
        Assert.Equal([101, 102], padrao!.Select(i => i.OsId));
    }

    [Fact]
    public async Task Operacao_fora_da_etapa_responde_409_com_o_motivo()
    {
        await SemearAsync(5);

        var resposta = await _api.PostAsync("/api/execucoes/5/finalizar", null);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains("EmReparo", await resposta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OS_sem_execucao_responde_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync("/api/execucoes/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync("/api/execucoes/999/diagnostico/iniciar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsJsonAsync("/api/execucoes/999/apontamentos", new { texto = "x" })).StatusCode);
    }

    [Theory]
    [InlineData("/api/execucoes/fila?etapa=Inexistente")]
    [InlineData("/api/execucoes/fila?limite=0")]
    [InlineData("/api/execucoes/fila?limite=101")]
    public async Task Parametro_invalido_da_fila_responde_400(string rota)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.GetAsync(rota)).StatusCode);
    }

    [Fact]
    public async Task Apontamento_sem_texto_responde_400()
    {
        await SemearAsync(6);

        var resposta = await _api.PostAsJsonAsync("/api/execucoes/6/apontamentos", new { texto = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Apontamento_em_execucao_encerrada_responde_409()
    {
        var execucao = await SemearAsync(8);
        execucao.Cancelar("teste", DateTime.UtcNow);
        await _factory.Services.GetRequiredService<IExecucaoRepositorio>().SalvarAsync(execucao, [], null);

        var resposta = await _api.PostAsJsonAsync("/api/execucoes/8/apontamentos", new { texto = "tarde demais" });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }

    [Fact]
    public async Task Sem_token_responde_401()
    {
        using var anonimo = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/execucoes/fila")).StatusCode);
    }

    [Fact]
    public async Task Token_de_cliente_nao_opera_a_fila()
    {
        using var cliente = _factory.CreateClient().Autenticado(Tokens.DeCliente());

        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync("/api/execucoes/fila")).StatusCode);
    }

    [Fact]
    public async Task Mecanico_tambem_opera_a_fila()
    {
        await SemearAsync(9);
        using var mecanico = _factory.CreateClient().Autenticado(Tokens.DaOficina("ana", "Mecanico"));

        var resposta = await mecanico.PostAsync("/api/execucoes/9/diagnostico/iniciar", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("ana", (await resposta.Content.ReadFromJsonAsync<ExecucaoDto>())!.Mecanico);
    }
}
