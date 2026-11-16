using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Oficina.Execution.UnitTests.TestSupport;

/// <summary>Logger que guarda cada mensagem com as propriedades dos escopos ativos.</summary>
public sealed class LogsCapturados : ILoggerProvider
{
    private readonly LoggerExternalScopeProvider _escopos = new();

    public ConcurrentQueue<(string Mensagem, IReadOnlyDictionary<string, object?> Escopo)> Registros { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly LogsCapturados _dono;

        public Logger(LogsCapturados dono) => _dono = dono;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _dono._escopos.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var escopo = new Dictionary<string, object?>();
            _dono._escopos.ForEachScope((s, acumulado) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object>> pares)
                    foreach (var (chave, valor) in pares)
                        acumulado[chave] = valor;
            }, escopo);
            _dono.Registros.Enqueue((formatter(state, exception), escopo));
        }
    }
}
