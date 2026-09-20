using Microsoft.Extensions.DependencyInjection;
using Viegard.Application.Inference;
using Viegard.Application.Inference.Prompts;

namespace Viegard.Inference.Ollama;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddViegardOllamaInference(this IServiceCollection services)
    {
        services.AddSingleton<PromptAssembler>();
        services.AddSingleton<IInferenceProvider, OllamaInferenceProvider>();
        return services;
    }
}
