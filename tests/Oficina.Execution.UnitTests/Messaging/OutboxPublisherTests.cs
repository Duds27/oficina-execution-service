using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using Oficina.Execution.Infrastructure.Messaging;
using Oficina.Execution.Infrastructure.Persistence;

namespace Oficina.Execution.UnitTests.Messaging;

public sealed class OutboxPublisherTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");

    private readonly FonteEmMemoria _fonte = new();
    private readonly ContadorDePublicacoes _publicacoes = new();
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddMassTransitTestHarness(x => x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(10), testInactivityTimeout: TimeSpan.FromSeconds(1)))
            .BuildServiceProvider(validateScopes: true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _harness.Bus.ConnectPublishObserver(_publicacoes);
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private OutboxPublisher Publicador(int lote = 25, int intervaloMs = 2000) => new(
        _fonte,
        _harness.Bus,
        Options.Create(new OutboxPublisherOptions { Lote = lote, IntervaloMs = intervaloMs }),
        NullLogger<OutboxPublisher>.Instance);

    private static DiagnosticoIniciado Evento(int osId) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-{osId:D4}",
        OcorridoEm = T0,
        Mecanico = "joao"
    };

    [Fact]
    public async Task Publica_cada_pendente_com_o_id_do_outbox_como_MessageId_e_apaga()
    {
        var id = _fonte.Adicionar(Evento(1));
        var finalizada = _fonte.Adicionar(new ExecucaoFinalizada { CorrelationId = Correlacao, OsId = 1, NumeroOS = "OS-0001", DuracaoSegundos = 360 });

        var publicadas = await Publicador().PublicarPendentesAsync(default);

        Assert.Equal(2, publicadas);
        Assert.Empty(_fonte.Pendentes);
        Assert.True(await _harness.Published.Any<DiagnosticoIniciado>(m =>
            m.Context.MessageId == id && m.Context.CorrelationId == Correlacao && m.Context.Message.Mecanico == "joao"));
        Assert.True(await _harness.Published.Any<ExecucaoFinalizada>(m =>
            m.Context.MessageId == finalizada && m.Context.Message.DuracaoSegundos == 360));
    }

    [Fact]
    public async Task Esvazia_o_outbox_mesmo_com_mais_de_um_lote()
    {
        for (var os = 1; os <= 7; os++)
            _fonte.Adicionar(Evento(os));

        var publicadas = await Publicador(lote: 3).PublicarPendentesAsync(default);

        Assert.Equal(7, publicadas);
        Assert.Empty(_fonte.Pendentes);
    }

    [Fact]
    public async Task Tipo_desconhecido_fica_no_outbox_e_nao_trava_os_outros()
    {
        _fonte.AdicionarRegistro(new RegistroOutbox(Guid.NewGuid(), "Oficina.Contracts.MensagemQueNaoExiste", "{}", T0));
        var valido = _fonte.Adicionar(Evento(2));

        var publicadas = await Publicador().PublicarPendentesAsync(default);

        Assert.Equal(1, publicadas);
        Assert.Equal("Oficina.Contracts.MensagemQueNaoExiste", Assert.Single(_fonte.Pendentes).TipoMensagem);
        Assert.True(await _harness.Published.Any<DiagnosticoIniciado>(m => m.Context.MessageId == valido));
    }

    [Fact]
    public async Task Se_cair_antes_de_apagar_publica_de_novo_com_o_mesmo_MessageId()
    {
        var id = _fonte.Adicionar(Evento(3));
        _fonte.FalharAoRemover = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publicador().PublicarPendentesAsync(default));
        Assert.Single(_fonte.Pendentes);

        _fonte.FalharAoRemover = false;
        await Publicador().PublicarPendentesAsync(default);

        // O harness guarda uma entrada por MessageId; o observador conta cada publicação.
        Assert.Empty(_fonte.Pendentes);
        Assert.Equal(2, _publicacoes.Count(id));
    }

    [Fact]
    public async Task Servico_em_segundo_plano_publica_o_que_entra_no_outbox()
    {
        // A primeira leitura falha: o serviço loga e tenta de novo no intervalo seguinte.
        _fonte.FalharAoListarUmaVez = true;
        var id = _fonte.Adicionar(Evento(4));
        var publicador = Publicador(intervaloMs: 50);

        await publicador.StartAsync(default);

        Assert.True(await _harness.Published.Any<DiagnosticoIniciado>(m => m.Context.MessageId == id));
        await publicador.StopAsync(default);
        Assert.Empty(_fonte.Pendentes);
    }

    private sealed class ContadorDePublicacoes : IPublishObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<Guid> _ids = [];

        public int Count(Guid messageId) => _ids.Count(i => i == messageId);

        public Task PrePublish<T>(PublishContext<T> context) where T : class => Task.CompletedTask;

        public Task PostPublish<T>(PublishContext<T> context) where T : class
        {
            if (context.MessageId is { } id)
                _ids.Add(id);
            return Task.CompletedTask;
        }

        public Task PublishFault<T>(PublishContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }

    private sealed class FonteEmMemoria : IFonteOutbox
    {
        private readonly List<RegistroOutbox> _itens = [];
        private readonly object _trava = new();

        public bool FalharAoRemover { get; set; }

        public bool FalharAoListarUmaVez { get; set; }

        public IReadOnlyList<RegistroOutbox> Pendentes
        {
            get { lock (_trava) return [.. _itens]; }
        }

        public Guid Adicionar(object mensagem)
        {
            var id = Guid.NewGuid();
            // O mesmo caminho do DynamoDB: item gravado e lido de volta.
            AdicionarRegistro(ItensDynamoDb.ParaRegistroOutbox(ItensDynamoDb.ItemOutbox(id, mensagem, T0)));
            return id;
        }

        public void AdicionarRegistro(RegistroOutbox registro)
        {
            lock (_trava) _itens.Add(registro);
        }

        public Task<IReadOnlyList<RegistroOutbox>> ListarPendentesAsync(int limite, CancellationToken cancellationToken)
        {
            if (FalharAoListarUmaVez)
            {
                FalharAoListarUmaVez = false;
                throw new InvalidOperationException("DynamoDB fora do ar (simulado).");
            }

            lock (_trava) return Task.FromResult<IReadOnlyList<RegistroOutbox>>(_itens.Take(limite).ToList());
        }

        public Task RemoverAsync(Guid id, CancellationToken cancellationToken)
        {
            if (FalharAoRemover)
                throw new InvalidOperationException("Queda entre publicar e apagar (simulada).");

            lock (_trava) _itens.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        }
    }
}
