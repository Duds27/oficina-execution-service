using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Domain;
using Oficina.Execution.Infrastructure.Persistence;
using Oficina.Execution.IntegrationTests.Infraestrutura;

namespace Oficina.Execution.IntegrationTests.Persistence;

public sealed record MensagemDeTeste(int OsId, string Texto);

[Collection(ColecaoDynamoDb.Nome)]
public sealed class RepositorioDynamoDbTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Veiculo Gol = new("ABC1D23", "VW", "Gol", 2020);

    private readonly DynamoDbFixture _dynamo;
    private readonly RelogioDeTeste _relogio = new(T0);
    private string _tabela = null!;
    private ExecucaoRepositorioDynamoDb _repositorio = null!;
    private OutboxDynamoDb _outbox = null!;

    public RepositorioDynamoDbTests(DynamoDbFixture dynamo) => _dynamo = dynamo;

    public async Task InitializeAsync()
    {
        _tabela = await _dynamo.NovaTabelaAsync();
        var opcoes = Options.Create(new DynamoDbOptions { TableName = _tabela });
        _repositorio = new ExecucaoRepositorioDynamoDb(_dynamo.Cliente, opcoes, _relogio);
        _outbox = new OutboxDynamoDb(_dynamo.Cliente, opcoes);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Execucao Abrir(int osId, DateTime? em = null) =>
        Execucao.Abrir(osId, Guid.NewGuid(), $"OS-{osId:D4}", Gol, "Barulho no motor", em ?? _relogio.Agora);

    [Fact]
    public async Task Grava_e_le_a_execucao_com_a_linha_do_tempo_completa()
    {
        var execucao = Abrir(1);
        Assert.Equal(ResultadoGravacao.Gravada, await _repositorio.SalvarAsync(execucao, [], null));

        execucao.IniciarDiagnostico("joao", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        execucao.ConcluirDiagnostico("Trocar óleo", "joao", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        await _repositorio.SalvarAsync(execucao, [], null);
        execucao.IniciarExecucao([new ServicoPrevisto("Troca de óleo", 1.5m)], _relogio.Avancar(TimeSpan.FromMinutes(1)));
        await _repositorio.SalvarAsync(execucao, [], null);

        var lida = await _repositorio.ObterAsync(1);

        Assert.NotNull(lida);
        Assert.Equal(execucao.Estado with { Servicos = [] }, lida.Estado with { Servicos = [] });
        Assert.Equal(execucao.Estado.Servicos, lida.Estado.Servicos);
        Assert.Equal(3, lida.Version);
        Assert.Equal(execucao.Apontamentos, lida.Apontamentos);
        Assert.Equal(DateTimeKind.Utc, lida.Estado.EtapaDesde.Kind);
        Assert.Empty(lida.NovosApontamentos);
    }

    [Fact]
    public async Task Execucao_inexistente_volta_nula()
    {
        Assert.Null(await _repositorio.ObterAsync(999));
    }

    [Fact]
    public async Task Abrir_de_novo_a_mesma_OS_da_conflito()
    {
        await _repositorio.SalvarAsync(Abrir(2), [], null);

        await Assert.ThrowsAsync<ConflitoDeVersaoException>(() => _repositorio.SalvarAsync(Abrir(2), [], null));
    }

    [Fact]
    public async Task Gravacao_com_versao_desatualizada_da_conflito_e_nao_grava_nada()
    {
        await _repositorio.SalvarAsync(Abrir(3), [], null);
        var primeira = (await _repositorio.ObterAsync(3))!;
        var segunda = (await _repositorio.ObterAsync(3))!;

        primeira.IniciarDiagnostico("joao", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        await _repositorio.SalvarAsync(primeira, [new MensagemDeTeste(3, "da primeira")], null);

        segunda.IniciarDiagnostico("maria", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ConflitoDeVersaoException>(() =>
            _repositorio.SalvarAsync(segunda, [new MensagemDeTeste(3, "da segunda")], null));

        var gravada = (await _repositorio.ObterAsync(3))!;
        Assert.Equal("joao", gravada.Estado.Mecanico);
        Assert.Equal(2, gravada.Apontamentos.Count);
        var pendente = Assert.Single(await _outbox.ListarPendentesAsync(10, default));
        Assert.Contains("da primeira", pendente.Corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mensagem_repetida_nao_grava_nada_de_novo()
    {
        var messageId = Guid.NewGuid();
        await _repositorio.SalvarAsync(Abrir(4), [new MensagemDeTeste(4, "primeira vez")], messageId);

        Assert.True(await _repositorio.MensagemJaProcessadaAsync(messageId));
        Assert.False(await _repositorio.MensagemJaProcessadaAsync(Guid.NewGuid()));

        // A mesma mensagem de novo, aplicada sobre o estado atual: a transação inteira é descartada.
        var execucao = (await _repositorio.ObterAsync(4))!;
        execucao.Cancelar("repetida", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        var resultado = await _repositorio.SalvarAsync(execucao, [new MensagemDeTeste(4, "segunda vez")], messageId);

        Assert.Equal(ResultadoGravacao.MensagemDuplicada, resultado);
        var gravada = (await _repositorio.ObterAsync(4))!;
        Assert.Equal(EtapaExecucao.DiagnosticoPendente, gravada.Etapa);
        Assert.Equal(1, gravada.Version);
        Assert.Single(gravada.Apontamentos);
        Assert.Single(await _outbox.ListarPendentesAsync(10, default));
        Assert.Equal(1, execucao.Version); // Não foi marcada como gravada.
    }

    [Fact]
    public async Task Registro_da_mensagem_processada_expira_em_sete_dias()
    {
        var messageId = Guid.NewGuid();
        await _repositorio.SalvarAsync(null, [], messageId);

        var item = await _dynamo.Cliente.GetItemAsync(new GetItemRequest
        {
            TableName = _tabela,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = $"MSG#{messageId:N}" },
                ["SK"] = new() { S = "MSG" }
            }
        });

        var expiraEm = DateTimeOffset.FromUnixTimeSeconds(long.Parse(item.Item["ExpiraEm"].N, CultureInfo.InvariantCulture));
        Assert.Equal(new DateTimeOffset(T0.AddDays(7)), expiraEm);
        Assert.True(await _repositorio.MensagemJaProcessadaAsync(messageId));
    }

    [Fact]
    public async Task Outbox_lista_do_mais_antigo_para_o_mais_novo_e_remove()
    {
        await _repositorio.SalvarAsync(Abrir(5), [new MensagemDeTeste(5, "a")], null);
        _relogio.Avancar(TimeSpan.FromSeconds(1));
        await _repositorio.SalvarAsync(null, [new MensagemDeTeste(5, "b"), new MensagemDeTeste(5, "c")], null);

        var pendentes = await _outbox.ListarPendentesAsync(10, default);

        Assert.Equal(3, pendentes.Count);
        Assert.Equal("a", JsonSerializer.Deserialize<MensagemDeTeste>(pendentes[0].Corpo, ItensDynamoDb.JsonOutbox)!.Texto);
        Assert.All(pendentes, p => Assert.Equal(typeof(MensagemDeTeste).FullName, p.TipoMensagem));
        Assert.Equal(["b", "c"], pendentes.Skip(1)
            .Select(p => JsonSerializer.Deserialize<MensagemDeTeste>(p.Corpo, ItensDynamoDb.JsonOutbox)!.Texto)
            .Order());

        await _outbox.RemoverAsync(pendentes[0].Id, default);

        Assert.Equal(2, (await _outbox.ListarPendentesAsync(10, default)).Count);
        Assert.Single(await _outbox.ListarPendentesAsync(1, default));
    }

    [Fact]
    public async Task Fila_por_etapa_sai_em_ordem_de_chegada()
    {
        await _repositorio.SalvarAsync(Abrir(30, T0.AddMinutes(3)), [], null);
        await _repositorio.SalvarAsync(Abrir(10, T0.AddMinutes(1)), [], null);
        await _repositorio.SalvarAsync(Abrir(20, T0.AddMinutes(2)), [], null);

        Assert.Equal([10, 20, 30], (await _repositorio.ListarFilaAsync(EtapaExecucao.DiagnosticoPendente, 10)).Select(e => e.OsId));
        Assert.Equal([10, 20], (await _repositorio.ListarFilaAsync(EtapaExecucao.DiagnosticoPendente, 2)).Select(e => e.OsId));

        var primeira = (await _repositorio.ObterAsync(10))!;
        primeira.IniciarDiagnostico("joao", T0.AddMinutes(5));
        await _repositorio.SalvarAsync(primeira, [], null);

        Assert.Equal([20, 30], (await _repositorio.ListarFilaAsync(EtapaExecucao.DiagnosticoPendente, 10)).Select(e => e.OsId));
        var emDiagnostico = Assert.Single(await _repositorio.ListarFilaAsync(EtapaExecucao.EmDiagnostico, 10));
        Assert.Equal(10, emDiagnostico.OsId);
        Assert.Equal("joao", emDiagnostico.Mecanico);
    }

    [Fact]
    public async Task Cancelamento_antecipado_e_gravado_sem_veiculo()
    {
        var cancelada = Execucao.AbrirCancelada(6, Guid.NewGuid(), "OS-0006", "Orçamento recusado", T0);
        await _repositorio.SalvarAsync(cancelada, [], Guid.NewGuid());

        var lida = (await _repositorio.ObterAsync(6))!;

        Assert.Equal(EtapaExecucao.Cancelada, lida.Etapa);
        Assert.Null(lida.Estado.Veiculo);
        Assert.Empty(lida.Estado.Servicos);
    }

    [Fact]
    public async Task Gravar_sem_nada_nao_chama_o_banco()
    {
        Assert.Equal(ResultadoGravacao.Gravada, await _repositorio.SalvarAsync(null, [], null));
    }
}
