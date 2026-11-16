using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oficina.Execution.Application.UseCases;

namespace Oficina.Execution.Application;

public static class DependencyInjection
{
    /// <summary>Registra os casos de uso e validadores da camada de aplicação.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Relógio injetável: os testes controlam o tempo (ordem da fila, duração do reparo).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ComandosSagaUseCases>();
        services.AddSingleton<ExecucaoUseCases>();
        return services;
    }
}
