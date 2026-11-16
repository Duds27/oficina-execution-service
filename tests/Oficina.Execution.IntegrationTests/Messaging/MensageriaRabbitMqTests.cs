using System.Net;
using System.Net.Http.Json;
using Oficina.Contracts;
using Oficina.Execution.Application.DTOs;
using Oficina.Execution.IntegrationTests.Infraestrutura;

namespace Oficina.Execution.IntegrationTests.Messaging;

/// <summary>
/// Ponta a ponta com RabbitMQ e DynamoDB Local: comandos chegam pelas filas do contrato,
/// os eventos saem pelo outbox e chegam a um bus de teste no papel do os-service.
/// Cada teste sobe a sua própria API, para duas instâncias não disputarem as mesmas filas.
/// </summary>
[Collection(ColecaoMensageria.Nome)]
public sealed class MensageriaRabbitMqTests : IAsyncLifetime
{
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");

    private readonly MensageriaFixture _fixture;
    private OsServiceSimulado _os = null!;

    public MensageriaRabbitMqTests(MensageriaFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _os = await OsServiceSimulado.IniciarAsync(_fixture);

    public async Task DisposeAsync() => await _os.DisposeAsync();

    private static async Task<HttpClient> ApiProntaAsync(RabbitMqApiFactory factory)
    {
        var api = factory.CreateClient().Autenticado(Tokens.DaOficina());
        // Pronta quando o bus conectou ao RabbitMQ e a tabela existe.
        await Esperar.AteAsync(
            async () => (await api.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK ? "pronta" : null,
            _ => true, "A API não ficou pronta.", segundos: 60);
        return api;
    }

    private static Task<ExecucaoDto> EsperarExecucaoAsync(HttpClient api, int osId, string etapa) =>
        Esperar.AteAsync(async () =>
        {
            var resposta = await api.GetAsync($"/api/execucoes/{osId}");
            return resposta.IsSuccessStatusCode ? await resposta.Content.ReadFromJsonAsync<ExecucaoDto>() : null;
        }, e => e.Etapa == etapa, $"A execução da OS {osId} não chegou a {etapa}.", segundos: 30);

    private static SolicitarDiagnostico Solicitar(int osId) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-{osId:D4}",
        OcorridoEm = DateTime.UtcNow,
        Veiculo = new VeiculoInfo { Placa = "ABC1D23", Marca = "VW", Modelo = "Gol", Ano = 2020 },
        Descricao = "Freio fazendo barulho"
    };

    private static IniciarExecucao Iniciar(int osId) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-{osId:D4}",
        OcorridoEm = DateTime.UtcNow,
        Servicos = [new ServicoExecucaoInfo { Descricao = "Troca de pastilhas", Quantidade = 1 }]
    };

    private async Task DiagnosticoConcluidoAsync(HttpClient api, int osId)
    {
        await _os.EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar(osId));
        await EsperarExecucaoAsync(api, osId, "DiagnosticoPendente");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/execucoes/{osId}/diagnostico/iniciar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync($"/api/execucoes/{osId}/diagnostico/concluir", new { observacoes = "ok" })).StatusCode);
    }

    [Fact]
    public async Task Pedido_de_diagnostico_abre_a_execucao_e_os_eventos_chegam_ao_os_service()
    {
        await using var factory = new RabbitMqApiFactory(_fixture);
        using var api = await ApiProntaAsync(factory);

        await DiagnosticoConcluidoAsync(api, 201);

        var iniciado = await _os.EsperarAsync<DiagnosticoIniciado>(201);
        var concluido = await _os.EsperarAsync<DiagnosticoConcluido>(201);
        Assert.Equal("joao", iniciado.Mecanico);
        Assert.Equal(Correlacao, iniciado.CorrelationId);
        Assert.Equal("ok", concluido.Observacoes);

        // URN do contrato no envelope: é por ele que o os-service reconhece o tipo.
        var recebido = _os.Recebidos.First(r => r.Mensagem is DiagnosticoIniciado);
        Assert.Contains("urn:message:oficina:diagnostico-iniciado:v1", recebido.Tipos);
        Assert.NotNull(recebido.MessageId);
    }

    [Fact]
    public async Task Reentrega_pelo_broker_com_o_mesmo_MessageId_nao_duplica_efeito_nem_evento()
    {
        await using var factory = new RabbitMqApiFactory(_fixture);
        using var api = await ApiProntaAsync(factory);
        await DiagnosticoConcluidoAsync(api, 202);

        var messageId = Guid.NewGuid();
        await _os.EnviarAsync(FilasExecucao.IniciarExecucao, Iniciar(202), messageId);
        await _os.EnviarAsync(FilasExecucao.IniciarExecucao, Iniciar(202), messageId);
        // Sentinela: sem execução, a OS 299 recebe uma rejeição. Quando ela chega, as duas
        // cópias já foram consumidas e o outbox já publicou o que tinha.
        await _os.EnviarAsync(FilasExecucao.IniciarExecucao, Iniciar(299));

        await _os.EsperarAsync<ExecucaoIniciada>(202);
        await _os.EsperarAsync<ExecucaoRejeitada>(299);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(1, _os.Quantos<ExecucaoIniciada>(202));
        Assert.Equal(0, _os.Quantos<ExecucaoRejeitada>(202));
        var execucao = await EsperarExecucaoAsync(api, 202, "AguardandoExecucao");
        Assert.Single(execucao.Apontamentos, a => a.Tipo == "ExecucaoIniciada");
    }

    [Fact]
    public async Task Fault_injection_forca_ExecucaoRejeitada_e_tira_a_execucao_da_fila()
    {
        await using var factory = new RabbitMqApiFactory(_fixture, rejeitarExecucao: true);
        using var api = await ApiProntaAsync(factory);
        await DiagnosticoConcluidoAsync(api, 203);

        await _os.EnviarAsync(FilasExecucao.IniciarExecucao, Iniciar(203));

        var rejeitada = await _os.EsperarAsync<ExecucaoRejeitada>(203);
        Assert.Equal("falha simulada", rejeitada.Motivo);
        var execucao = await EsperarExecucaoAsync(api, 203, "Cancelada");
        Assert.Equal("falha simulada", execucao.MotivoCancelamento);
        Assert.Equal(0, _os.Quantos<ExecucaoIniciada>(203));
    }

    [Fact]
    public async Task Cancelamento_pela_saga_encerra_a_execucao()
    {
        await using var factory = new RabbitMqApiFactory(_fixture);
        using var api = await ApiProntaAsync(factory);
        await _os.EnviarAsync(FilasExecucao.SolicitarDiagnostico, Solicitar(204));
        await EsperarExecucaoAsync(api, 204, "DiagnosticoPendente");

        await _os.EnviarAsync(FilasExecucao.CancelarExecucao, new CancelarExecucao
        {
            CorrelationId = Correlacao,
            OsId = 204,
            NumeroOS = "OS-0204",
            OcorridoEm = DateTime.UtcNow,
            Motivo = "Orçamento recusado"
        });

        var execucao = await EsperarExecucaoAsync(api, 204, "Cancelada");
        Assert.Equal("Orçamento recusado", execucao.MotivoCancelamento);
    }
}
