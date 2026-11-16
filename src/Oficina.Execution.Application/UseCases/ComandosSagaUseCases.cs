using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Application.Mensagens;
using Oficina.Execution.Domain;

namespace Oficina.Execution.Application.UseCases;

/// <summary>Desfecho do processamento de um comando, para log e testes.</summary>
public enum ResultadoProcessamento
{
    /// <summary>Mudou a execução e/ou publicou evento.</summary>
    Processada,

    /// <summary>Válida, mas sem efeito (ex.: cancelar o que já terminou). Fica registrada para dedup.</summary>
    Ignorada,

    /// <summary>Mesmo MessageId já processado: nada foi feito.</summary>
    Duplicada
}

/// <summary>
/// Comandos da saga do os-service. Cada um lê a execução, aplica a regra e grava tudo de
/// uma vez: agregado, evento de resposta (outbox) e o registro da mensagem (dedup).
/// </summary>
public sealed partial class ComandosSagaUseCases
{
    private readonly IExecucaoRepositorio _repositorio;
    private readonly TimeProvider _relogio;
    private readonly IOptionsMonitor<FaultInjectionOptions> _falhas;
    private readonly ILogger<ComandosSagaUseCases> _logger;

    public ComandosSagaUseCases(
        IExecucaoRepositorio repositorio,
        TimeProvider relogio,
        IOptionsMonitor<FaultInjectionOptions> falhas,
        ILogger<ComandosSagaUseCases> logger)
    {
        _repositorio = repositorio;
        _relogio = relogio;
        _falhas = falhas;
        _logger = logger;
    }

    /// <summary>Abre a execução na fila de diagnóstico.</summary>
    public async Task<ResultadoProcessamento> SolicitarDiagnosticoAsync(
        SolicitarDiagnostico comando, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (await _repositorio.MensagemJaProcessadaAsync(messageId, cancellationToken))
            return ResultadoProcessamento.Duplicada;

        var existente = await _repositorio.ObterAsync(comando.OsId, cancellationToken);
        if (existente is not null)
        {
            // Pedido repetido com outro MessageId, ou o cancelamento chegou antes (regra 5).
            LogExecucaoJaExiste(comando.NumeroOS, existente.Etapa);
            return await GravarAsync(null, [], messageId, ResultadoProcessamento.Ignorada, cancellationToken);
        }

        var veiculo = new Veiculo(comando.Veiculo.Placa, comando.Veiculo.Marca, comando.Veiculo.Modelo, comando.Veiculo.Ano);
        var execucao = Execucao.Abrir(comando.OsId, comando.CorrelationId, comando.NumeroOS, veiculo, comando.Descricao, Agora());
        return await GravarAsync(execucao, [], messageId, ResultadoProcessamento.Processada, cancellationToken);
    }

    /// <summary>Libera o reparo, ou rejeita. A resposta sempre volta para a saga.</summary>
    public async Task<ResultadoProcessamento> IniciarExecucaoAsync(
        IniciarExecucao comando, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (await _repositorio.MensagemJaProcessadaAsync(messageId, cancellationToken))
            return ResultadoProcessamento.Duplicada;

        var agora = Agora();
        var execucao = await _repositorio.ObterAsync(comando.OsId, cancellationToken);

        if (execucao is null)
        {
            var motivo = $"Não há execução para a OS {comando.NumeroOS}.";
            LogInicioRejeitado(comando.NumeroOS, motivo);
            return await GravarAsync(null, [FabricaMensagens.ExecucaoRejeitada(comando, motivo, agora)],
                messageId, ResultadoProcessamento.Processada, cancellationToken);
        }

        object resposta;
        if (_falhas.CurrentValue.RejeitarExecucao)
        {
            execucao.RejeitarInicio(FaultInjectionOptions.MotivoRejeicaoSimulada, agora);
            resposta = FabricaMensagens.ExecucaoRejeitada(execucao, FaultInjectionOptions.MotivoRejeicaoSimulada, agora);
            LogInicioRejeitado(comando.NumeroOS, FaultInjectionOptions.MotivoRejeicaoSimulada);
        }
        else
        {
            var servicos = comando.Servicos.Select(s => new ServicoPrevisto(s.Descricao, s.Quantidade)).ToList();
            var resultado = execucao.IniciarExecucao(servicos, agora);
            if (resultado.Aceita)
            {
                resposta = FabricaMensagens.ExecucaoIniciada(execucao, agora);
            }
            else
            {
                resposta = FabricaMensagens.ExecucaoRejeitada(execucao, resultado.Motivo!, agora);
                LogInicioRejeitado(comando.NumeroOS, resultado.Motivo!);
            }
        }

        return await GravarAsync(execucao, [resposta], messageId, ResultadoProcessamento.Processada, cancellationToken);
    }

    /// <summary>Compensação: cancela a execução. Em etapa final, não faz nada.</summary>
    public async Task<ResultadoProcessamento> CancelarExecucaoAsync(
        CancelarExecucao comando, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (await _repositorio.MensagemJaProcessadaAsync(messageId, cancellationToken))
            return ResultadoProcessamento.Duplicada;

        var agora = Agora();
        var execucao = await _repositorio.ObterAsync(comando.OsId, cancellationToken);

        if (execucao is null)
        {
            // Regra 5 da seção 5: registra o cancelamento e conclui sem erro.
            var cancelada = Execucao.AbrirCancelada(comando.OsId, comando.CorrelationId, comando.NumeroOS, comando.Motivo, agora);
            return await GravarAsync(cancelada, [], messageId, ResultadoProcessamento.Processada, cancellationToken);
        }

        if (!execucao.Cancelar(comando.Motivo, agora))
        {
            LogCancelamentoIgnorado(comando.NumeroOS, execucao.Etapa);
            return await GravarAsync(null, [], messageId, ResultadoProcessamento.Ignorada, cancellationToken);
        }

        return await GravarAsync(execucao, [], messageId, ResultadoProcessamento.Processada, cancellationToken);
    }

    private async Task<ResultadoProcessamento> GravarAsync(
        Execucao? execucao,
        IReadOnlyList<object> mensagens,
        Guid messageId,
        ResultadoProcessamento seGravar,
        CancellationToken cancellationToken)
    {
        var gravacao = await _repositorio.SalvarAsync(execucao, mensagens, messageId, cancellationToken);
        return gravacao == ResultadoGravacao.MensagemDuplicada ? ResultadoProcessamento.Duplicada : seGravar;
    }

    private DateTime Agora() => _relogio.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Information, Message = "Execução da OS {NumeroOS} já existe (etapa {Etapa}); pedido de diagnóstico ignorado")]
    private partial void LogExecucaoJaExiste(string numeroOS, EtapaExecucao etapa);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Início da execução da OS {NumeroOS} rejeitado: {Motivo}")]
    private partial void LogInicioRejeitado(string numeroOS, string motivo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancelamento da OS {NumeroOS} ignorado: execução já está em {Etapa}")]
    private partial void LogCancelamentoIgnorado(string numeroOS, EtapaExecucao etapa);
}
