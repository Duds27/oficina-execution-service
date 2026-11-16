using Microsoft.Extensions.Logging;
using Oficina.Execution.Application.UseCases;

namespace Oficina.Execution.Infrastructure.Messaging;

internal static partial class LogsMensageria
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{TipoMensagem} da OS {NumeroOS}: {Resultado}")]
    public static partial void ComandoProcessado(this ILogger logger, string tipoMensagem, string numeroOS, ResultadoProcessamento resultado);
}
