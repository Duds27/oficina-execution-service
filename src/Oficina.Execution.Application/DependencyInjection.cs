using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Oficina.Execution.Application;

public static class DependencyInjection
{
    /// <summary>Registra os casos de uso e validadores da camada de aplicação.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Relógio injetável: os testes controlam o tempo (prazos, ordem da fila, duração).
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
