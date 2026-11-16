using Oficina.Execution.Domain;

namespace Oficina.Execution.Application.Abstractions;

/// <summary>
/// Persistência da execução. Toda gravação é uma transação só: o agregado, os apontamentos
/// novos, as mensagens de saída (outbox) e, quando vem de um consumidor, o registro da
/// mensagem processada (deduplicação).
/// </summary>
public interface IExecucaoRepositorio
{
    /// <summary>A execução com a linha do tempo completa, ou <c>null</c>.</summary>
    Task<Execucao?> ObterAsync(int osId, CancellationToken cancellationToken = default);

    /// <summary>Execuções numa etapa, por ordem de chegada nela.</summary>
    Task<IReadOnlyList<EstadoExecucao>> ListarFilaAsync(EtapaExecucao etapa, int limite, CancellationToken cancellationToken = default);

    /// <summary>Se a mensagem já foi processada. Atalho: a garantia está na condição da gravação.</summary>
    Task<bool> MensagemJaProcessadaAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava tudo numa transação.
    /// </summary>
    /// <param name="execucao">Agregado alterado, ou <c>null</c> quando nada mudou nele.</param>
    /// <param name="mensagens">Eventos a publicar (vão para o outbox).</param>
    /// <param name="mensagemProcessada">MessageId da mensagem consumida, para deduplicar.</param>
    /// <param name="cancellationToken">Cancelamento da operação.</param>
    /// <returns><see cref="ResultadoGravacao.MensagemDuplicada"/> quando a mensagem já tinha sido processada: nada é gravado.</returns>
    /// <exception cref="ConflitoDeVersaoException">A execução mudou desde a leitura.</exception>
    Task<ResultadoGravacao> SalvarAsync(
        Execucao? execucao,
        IReadOnlyList<object> mensagens,
        Guid? mensagemProcessada,
        CancellationToken cancellationToken = default);
}

public enum ResultadoGravacao
{
    Gravada,
    MensagemDuplicada
}

/// <summary>Outra gravação mudou a execução depois da leitura (concorrência otimista).</summary>
public sealed class ConflitoDeVersaoException : Exception
{
    public ConflitoDeVersaoException(string message) : base(message)
    {
    }

    public ConflitoDeVersaoException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ConflitoDeVersaoException()
    {
    }
}
